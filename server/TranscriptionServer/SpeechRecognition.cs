using Google.Apis.Auth.OAuth2;
using Google.Cloud.Speech.V2;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Grpc.Core;
using NAudio.Wave;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace TranscriptionServer;


public class SpeechRecognizer : IDisposable
{
    private enum RecognizerState
    {
        Idle,
        Starting,
        Running,
        Stopping,
    }

    private readonly IWaveIn waveIn;
    private readonly SpeechRecognizerOptions options;
    private readonly SpeechClient.StreamingRecognizeStream streamingCall;

    private Stopwatch watch;
    private TimeSpan streamingCallBeginning;

    private RecognizerState state = RecognizerState.Idle;
    private Guid? currentRecognitionId = null;
    private TimeSpan? currentRecognitionStartingTime = null;


    private readonly ConcurrentQueue<SpeechSentence> resultQueue;
    private readonly string[] languages;

    private SpeechRecognizer(SpeechClient speech, IWaveIn waveIn, SpeechRecognizerOptions options)
    {
        this.waveIn = waveIn;
        this.options = options;
        this.streamingCall = speech.StreamingRecognize();

        resultQueue = new();

        languages = [options.Language];

        watch = Stopwatch.StartNew();
    }

    public void Start()
    {
        Console.WriteLine("Initializing SpeechRecognizer...");
        if (state != RecognizerState.Idle)
            throw new InvalidOperationException($"Speech recognizer shouldn't be started when it's not Idle (state: {state})");

        waveIn.DataAvailable += OnDataAvailable!;
        waveIn.StartRecording();

        watch = Stopwatch.StartNew();

        Task.Run(MainTask);

        state = RecognizerState.Running;
    }


    public void Dispose()
    {
        streamingCall.WriteCompleteAsync().ContinueWith(
            _ => Task.Run(streamingCall.Dispose)
        );
        waveIn.Dispose();
    }

    ~SpeechRecognizer() => Dispose();

    public bool TryGetNextResult([MaybeNullWhen(false)] out SpeechSentence sentence) => resultQueue.TryDequeue(out sentence);

    private async Task InitializeStreamingCall()
    {
        StreamingRecognizeRequest request = new()
        {
            StreamingConfig = new()
            {
                Config = new()
                {
                    Features = new()
                    {
                        EnableWordConfidence = true,
                        EnableWordTimeOffsets = true,
                        EnableAutomaticPunctuation = true,
                    },
                    ExplicitDecodingConfig = new()
                    {
                        AudioChannelCount = 1,
                        Encoding = ExplicitDecodingConfig.Types.AudioEncoding.Linear16,
                        SampleRateHertz = options.SampleRate
                    },
                    Model = "long",
                },
                StreamingFeatures = new()
                {
                    InterimResults = true,
                },
            },
            Recognizer = $"projects/{options.GcloudProjectId}/locations/global/recognizers/_"
        };
        request.StreamingConfig.Config.LanguageCodes.AddRange(languages);

        await streamingCall.WriteAsync(request);
    }

    private async Task MainTask()
    {
        Console.WriteLine("Performing asynchronous initialization");
        state = RecognizerState.Starting;
        await InitializeStreamingCall();

        Console.WriteLine("SpeechRecognizer started");


        Console.WriteLine("Beginning of speech Api streaming call");
        streamingCallBeginning = watch.Elapsed;


        while (await streamingCall.GetResponseStream().MoveNextAsync())
        {
            RepeatedField<StreamingRecognitionResult> results = streamingCall.GetResponseStream().Current.Results;
            
            if (results.IsEmpty()) continue;

            HandleApiResult(results);
        }
    }

    private void HandleApiResult(RepeatedField<StreamingRecognitionResult> results)
    {
        SpeechSentence result;

        if (results.First().IsFinal)
        {
            SpeechRecognitionAlternative? alternative = results
                .Single()
                .Alternatives
                .SingleOrDefault();

            if (alternative is null) return;

            IReadOnlyCollection<Word> words = alternative
                .Words
                .Select(wordInfo =>
                {
                    TimeSpan wordStartTime = wordInfo.StartOffset is not null ?
                        streamingCallBeginning + wordInfo.StartOffset.ToTimeSpan():
                        streamingCallBeginning;

                    TimeSpan wordEndTime = wordInfo.EndOffset is not null ? 
                        streamingCallBeginning + wordInfo.EndOffset.ToTimeSpan():
                        streamingCallBeginning;

                    string word = wordInfo.Word;

                    return new Word(wordStartTime, wordEndTime, word);
                })
                .ToList();

            float confidence = alternative.Confidence;
            TimeSpan startTime = currentRecognitionStartingTime ?? TimeSpan.Zero;
            TimeSpan endTime = watch.Elapsed;
            result = new FinalizedSentence(
                currentRecognitionId ?? throw new ArgumentNullException(),
                words, confidence, 
                startTime, endTime
            );

            TerminateRecognitionSession();
        }
        else
        {
            if (!currentRecognitionId.HasValue)
                InitRecognitionSession();

            IReadOnlyCollection<ElaboratingSentenceElement> recognitionResults = results
                .Select(apiResult =>
                {
                    SpeechRecognitionAlternative alternative = apiResult.Alternatives.Single();

                    string transcript = alternative.Transcript;
                    bool isStable = apiResult.Stability > 0.5f;

                    return new ElaboratingSentenceElement(transcript, isStable);
                })
                .ToList();

            result = new ElaboratingSentence(
                currentRecognitionId ?? throw new NullReferenceException(),
                recognitionResults, 
                currentRecognitionStartingTime ?? throw new NullReferenceException());
        }

        resultQueue.Enqueue(result);

        void TerminateRecognitionSession()
        {
            currentRecognitionId = null;
            currentRecognitionStartingTime = null;
        }

        void InitRecognitionSession()
        {
            currentRecognitionId = Guid.NewGuid();
            currentRecognitionStartingTime = watch.Elapsed;
        }
    }

    private const int WHOLE_ARRAY = -1;
    private void OnDataAvailable(object sender, WaveInEventArgs wiea)
    {
        byte[] data = wiea.Buffer;
        int count = wiea.BytesRecorded;

        ByteString audioContent = count == WHOLE_ARRAY ?
            ByteString.CopyFrom(data) :
            ByteString.CopyFrom(data, 0, count);
        StreamingRecognizeRequest writeRequest = new()
        {
            Audio = audioContent,
        };

        streamingCall.TryWriteAsync(writeRequest);
    }


    public record SpeechRecognizerOptions(
        string Credentials,
        string GcloudProjectId,
        int SampleRate,
        int Device,
        string Language
    );
    public static async Task<SpeechRecognizer> CreateAsync(SpeechRecognizerOptions options)
    {
        (string credentials, string gcloudProjectId, int sampleRate, int device, string language) = options;


        SpeechClientBuilder clientBuilder = new()
        {
            CredentialsPath = credentials,
        };
        
        SpeechClient speech = await clientBuilder.BuildAsync();
        

        IWaveIn soundIn = new WaveInEvent()
        {
            WaveFormat = new(sampleRate, 16, 1),
            DeviceNumber = device
        };


        SpeechRecognizer speechRecognizer = new(speech, soundIn, options);
        return speechRecognizer;
    }
}