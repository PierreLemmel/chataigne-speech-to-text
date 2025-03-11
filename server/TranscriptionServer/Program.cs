using CommandLine;
using TranscriptionServer;
using Vizcon.OSC;

internal class Program
{
    private static async Task Main(string[] args) => await Parser.Default
        .ParseArguments<CLIOptions>(args)
        .WithNotParsed(HandleParseErrors)
        .WithParsedAsync(MainTask);

    private static async Task MainTask(CLIOptions options)
    {
        try
        {
            SpeechRecognizer.SpeechRecognizerOptions sro = new(
                Credentials: options.GoogleCredentials,
                GcloudProjectId: options.GoogleCloudProjectId,
                SampleRate: options.SampleRate,
                Device: options.Device
            );

            int oscIn = options.OscIn;
            int oscOut = options.OscOut;
            string ip = options.IPAddress;

            bool endRequested = false;
            void HandleParentMessage(OscMessage message)
            {
                switch (message.Address)
                {
                    case "/transcription/stop":
                        endRequested = true;
                        break;
                    default:
                        Console.WriteLine($"Unexpected address: '{message.Address}'");
                        break;
                }
                Console.WriteLine(message.Address);
                foreach (var arg in message.Arguments)
                    Console.WriteLine($"    {arg}");
            }

            var sender = new UDPSender(ip, oscOut);

            var receiver = new UDPListener(oscIn, (OscPacket packet) =>
            {
                if (packet is OscMessage message)
                {
                    HandleParentMessage(message);
                }
                else
                {
                    Console.Error.WriteLine("Only osc messages are supported yet");
                }
            });

            HashSet<Guid> sentenceDic = new();
            void HandleSentence(SpeechSentence sentence)
            {
                Guid id = sentence.SentenceId;
                switch (sentence)
                {
                    case ElaboratingSentence elaborating:

                        double startTime = elaborating.StartTime.TotalMilliseconds;
                        var elts = elaborating.Elements;

                        if (!sentenceDic.Contains(id))
                        {
                            sentenceDic.Add(id);
                            OscMessage startMsg = new(
                                $"/transcription/started/{id}",
                                startTime
                            );
                            sender.Send(startMsg);
                        }
                        
                        string elaboratingMsgAddress = $"/transcription/elaborating/{id}";

                        string stablePart = string.Join(" ", elts.Where(elt => elt.IsStable));
                        string unstablePart = string.Join(" ", elts.Where(elt => !elt.IsStable));

                        sender.Send(new OscMessage(
                            $"/transcription/elaborating/{id}",
                            stablePart,
                            unstablePart,
                            startTime
                        ));

                        break;
                    case FinalizedSentence finalized:
                        OscMessage finalizedBundle = new(
                            $"/transcription/finalized/{id}",
                            string.Join(" ", finalized.Words.Select(w => w.Transcript)),
                            finalized.StartTime.TotalMilliseconds,
                            finalized.EndTime.TotalMilliseconds
                        );
                        break;
                }
                
            }

#pragma warning disable CS4014
            Task.Run(async () =>
            {
                while (!endRequested)
                {
                    sender.Send(new OscMessage("/transcription/keepalive"));
                    await Task.Delay(1000);
                }

                sender.Send(new OscMessage("/transcription/stopped"));
            });
#pragma warning restore CS4014

            Console.WriteLine("Initializing Speech Recognizer");
            using (SpeechRecognizer recognizer = await SpeechRecognizer.CreateAsync(sro))
            {
                Console.WriteLine("Speech Recognizer initialized");

                Console.WriteLine("Starting Speech Recognizer");
                recognizer.Start();
                Console.WriteLine("Speech Recognizer started");

                sender.Send(new OscMessage("/transcription/started"));

                int delayMs = 1000 / options.RefreshRate;
                int frame = 0;
                while (!endRequested)
                {
                    frame++;

                    while (recognizer.TryGetNextResult(out var sentence))
                    {
                        HandleSentence(sentence);
                    }
                    await Task.Delay(delayMs);
                }
            }

        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
        }
    }

    private static void HandleParseErrors(IEnumerable<Error> errors)
    {
        Console.WriteLine("Error during arguments parsing");
        foreach (var error in errors)
        {
            Console.Error.WriteLine(error.ToString());
        }
    }
}