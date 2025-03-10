using CommandLine;
using Google.Cloud.Speech.V2;
using Google.Protobuf;
using Newtonsoft.Json;
using TranscriptionServer;

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

            Console.WriteLine("Initializing Speech Recognizer");
            using (SpeechRecognizer recognizer = await SpeechRecognizer.CreateAsync(sro))
            {
                Console.WriteLine("Speech Recognizer initialized");

                Console.WriteLine("Starting Speech Recognizer");
                recognizer.Start();
                Console.WriteLine("Speech Recognizer started");

                int delayMs = 1000 / options.RefreshRate;
                int frame = 0;
                while (true)
                {
                    Console.WriteLine(frame++);

                    while (recognizer.TryGetNextResult(out var sentence))
                    {
                        Console.WriteLine(JsonConvert.SerializeObject(sentence));
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