using Google.Apis.Auth.OAuth2;
using Google.Cloud.Speech.V2;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using NAudio.Wave;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace TranscriptionServer;


public class SpeechRecognizer : IDisposable
{
    private const double TimeoutFromWhichWeCanReset = 540.0;
    private const double GoogleSpeechApiTimeout = 600.0;

    private enum RecognizerState
    {
        Idle,
        Starting,
        Running,
        Stopping,
        Restarting
    }

    private readonly SpeechClient speech;
    private readonly IWaveIn waveIn;
    private readonly SpeechRecognizerOptions options;

    private Stopwatch watch;
    private TimeSpan streamingCallBeginning;

    private RecognizerState state = RecognizerState.Idle;
    private Guid? currentRecognitionId = null;
    private TimeSpan? currentRecognitionStartingTime = null;

    private SpeechClient.StreamingRecognizeStream? streamingCall;

    private Task? startingTask;
    private Task? mainTask;
    private Task? stoppingTask;
    private Task? restartingTask;

    private object catchupLock = new();
    private readonly ICollection<byte[]> catchupData = [];
    private readonly ConcurrentQueue<SpeechSentence> resultQueue;

    private SpeechRecognizer(SpeechClient speech, IWaveIn waveIn, SpeechRecognizerOptions options)
    {
        this.speech = speech;
        this.waveIn = waveIn;
        this.options = options;

        resultQueue = new();

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
        startingTask = Task.Run(CreateStartingTask);

        startingTask.ContinueWith(
            _ => RunMainTask(),
            TaskContinuationOptions.OnlyOnRanToCompletion);

        startingTask.ContinueWith(
            task => LogErrorsForTask(task, "starting task"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    public void Stop()
    {
        Console.WriteLine("Stopping SpeechRecognizer...");
        if (state != RecognizerState.Running)
            throw new InvalidOperationException("Speech recognizer not initialized");

        state = RecognizerState.Stopping;

        waveIn.StopRecording();
        waveIn.DataAvailable -= OnDataAvailable!;

        Console.WriteLine("Marking streaming call as completed");
        stoppingTask = Task.Run(CreateStoppingTask);

        stoppingTask.ContinueWith(
            task => LogErrorsForTask(task, "stopping task"),
            TaskContinuationOptions.OnlyOnFaulted);

        stoppingTask.ContinueWith(
            task => Console.WriteLine("Streaming call marked as completed"),
            TaskContinuationOptions.OnlyOnRanToCompletion);

        stoppingTask.ContinueWith(_ => watch.Stop());
    }


    public void Dispose()
    {
        Stop();
        waveIn.Dispose();
    }

    ~SpeechRecognizer() => Dispose();

    public bool TryGetNextResult([MaybeNullWhen(false)] out SpeechSentence sentence) => resultQueue.TryDequeue(out sentence);

    private async Task CreateStartingTask()
    {
        Console.WriteLine("Performing asynchronous initialization");
        state = RecognizerState.Starting;
        await InitializeStreamingCall();

        Console.WriteLine("SpeechRecognizer started");
    }

    private async Task InitializeStreamingCall()
    {
        

        streamingCall = speech.StreamingRecognize();
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
        request.StreamingConfig.Config.LanguageCodes.Add("fr-fr");
        request.StreamingConfig.Config.LanguageCodes.Add("en-us");


        await streamingCall.WriteAsync(request);
    }

    private async Task CreateMainTask(CancellationTokenSource cancellationSource)
    {
        CancellationToken cancellationToken = cancellationSource.Token;

        Console.WriteLine("Beginning of speech Api streaming call");
        streamingCallBeginning = watch.Elapsed;

        SendCatchupRequestIfNeeded();

        while (await MoveToNextResponse())
        {
            cancellationToken.ThrowIfCancellationRequested();

            RepeatedField<StreamingRecognitionResult> results = streamingCall!.GetResponseStream().Current.Results;
            
            if (results.IsEmpty()) continue;

            bool isResultFinal = HandleApiResult(results);
            if (isResultFinal)
                CancelTaskIfStreamingCallApproachesFromTimeout();
        }
        Console.WriteLine("Speech api streaming call ended ");

        async Task<bool> MoveToNextResponse()
        {
            try
            {
                bool result = await streamingCall!.GetResponseStream().MoveNextAsync(cancellationToken);
                return result;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
            {
                throw new TaskCanceledException("Rpc call cancelled", ex);
            }
        }

        void CancelTaskIfStreamingCallApproachesFromTimeout()
        {
            TimeSpan timeSinceStreamingCallRunning = watch.Elapsed - streamingCallBeginning;
            if (timeSinceStreamingCallRunning > TimeSpan.FromSeconds(TimeoutFromWhichWeCanReset))
                cancellationSource.Cancel();
        }

        void SendCatchupRequestIfNeeded()
        {
            lock (catchupLock)
            {
                if (catchupData.Any())
                {
                    byte[] data = Arrays.Merge(catchupData);
                    SendRequestToSpeechApi(data);
                    catchupData.Clear();
                }
            }
        }
    }

    private async Task CreateStoppingTask()
    {
        if (state == RecognizerState.Restarting)
            await restartingTask!;

        await streamingCall!.WriteCompleteAsync();
        await mainTask!;
    }

    private async Task RestartMainTask()
    {
        Console.WriteLine("Restarting SpeechRecognizer to bypass googlespeech api 1 minute limitation");
        state = RecognizerState.Restarting;

        await InitializeStreamingCall();
        RunMainTask();


        Console.WriteLine("SpeechRecognizer restarded");
    }

    private void RunMainTask()
    {
        CancellationTokenSource source = new CancellationTokenSource();

        mainTask = Task.Run(() => CreateMainTask(source));
        source.CancelAfter(TimeSpan.FromSeconds(GoogleSpeechApiTimeout));

        state = RecognizerState.Running;

        mainTask.ContinueWith(
            task =>
            {
                LogErrorsForTask(task, "mainTask");
            },
            TaskContinuationOptions.OnlyOnFaulted);

        mainTask.ContinueWith(
            _ => restartingTask = Task.Run(RestartMainTask),
            TaskContinuationOptions.OnlyOnCanceled);

        mainTask.ContinueWith(
            _ => Console.WriteLine("SpeechRecognizer main task ran to completion"),
            TaskContinuationOptions.OnlyOnRanToCompletion);
    }


    private void LogErrorsForTask(Task task, string taskName)
    {
        Console.Error.WriteLine($"Error on {taskName}");

        if (task.Exception is null) return;

        Console.Error.WriteLine(task.Exception.GetType().FullName);
        Console.Error.WriteLine(task.Exception.Message);

        foreach (Exception ex in task.Exception.InnerExceptions)
        {
            Console.Error.WriteLine($"\t{ex.GetType().FullName}");
            Console.Error.WriteLine($"\t{ex.Message}");
        }
    }

    private bool HandleApiResult(RepeatedField<StreamingRecognitionResult> results)
    {
        SpeechSentence result;
        bool isResultFinal;

        if (results.First().IsFinal)
        {
            SpeechRecognitionAlternative? alternative = results
                .Single()
                .Alternatives
                .SingleOrDefault();

            if (alternative is null) return false;

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
            isResultFinal = true;

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

            isResultFinal = false;
        }

        resultQueue.Enqueue(result);
        return isResultFinal;

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

    private void OnDataAvailable(object sender, WaveInEventArgs wiea)
    {
        byte[] buffer = wiea.Buffer;
        int count = wiea.BytesRecorded;

        if (state == RecognizerState.Running)
        {
            SendRequestToSpeechApi(wiea.Buffer, wiea.BytesRecorded);
        }
        else if (state == RecognizerState.Restarting)
        {
            lock (catchupLock)
            {
                if (buffer.Length == wiea.BytesRecorded)
                    catchupData.Add(buffer);
                else
                {
                    byte[] data = new byte[wiea.BytesRecorded];
                    Array.Copy(buffer, data, count);
                    catchupData.Add(data);
                }
            }
        }
    }

    private const int WHOLE_ARRAY = -1;
    private void SendRequestToSpeechApi(byte[] data, int count = WHOLE_ARRAY)
    {
        ByteString audioContent = count == WHOLE_ARRAY ?
            ByteString.CopyFrom(data) :
            ByteString.CopyFrom(data, 0, count);
        StreamingRecognizeRequest writeRequest = new()
        {
            Audio = audioContent,
        };

        streamingCall!.WriteAsync(writeRequest);
    }


    public record SpeechRecognizerOptions(
        string Credentials,
        string GcloudProjectId,
        int SampleRate,
        int Device
    );
    public static async Task<SpeechRecognizer> CreateAsync(SpeechRecognizerOptions options)
    {
        (string credentials, string gcloudProjectId, int sampleRate, int device) = options;


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