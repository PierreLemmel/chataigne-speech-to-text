using CommandLine;

namespace TranscriptionServer;

public class CLIOptions
{
    [Option('c', "credentials", Required = true, Default = "service-account.json", HelpText = "Path to credentials json file")]
    public required string GoogleCredentials { get; set; }

    [Option('p', "gcloud-project-id", Required = true, HelpText = "Id du projet Google Cloud")]
    public required string GoogleCloudProjectId { get; set; }

    [Option('s', "sample-rate", Required = false, Default = 44100, HelpText = "Sample rate of audio recording")]
    public int SampleRate { get; set; }

    [Option('d', "device", Required = false, Default = 0, HelpText = "Device")]
    public int Device { get; set; }

    [Option('r', "refresh", Required = false, Default = 30, HelpText = "Refresh rate in hz")]
    public int RefreshRate { get; set; }
}