using Newtonsoft.Json;

namespace NugetInspector;

/// <summary>
/// Dump results to JSON
/// </summary>
public class ScanHeader
{
    #pragma warning disable IDE1006
    public string ToolName { get; set; } = "nuget-inspector";
    public string ToolHomepageurl { get; set; } = "https://github.com/nexB/nuget-inspector";
    public string ToolVersion { get; set; } = Config.NUGET_INSPECTOR_VERSION;
    public List<string> Options { get; set; }

    public string ProjectFramework { get; set; } = "";

    public string Notice { get; set; } = "Dependency tree generated with nuget-inspector.\n" +
                                         "nuget-inspector is a free software tool from nexB Inc. and others.\n" +
                                         "Visit https://github.com/nexB/nuget-inspector/ for support and download.";

    public List<string> Warnings { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    #pragma warning restore IDE1006
    public ScanHeader(Options options)
    {
        Options = options.AsCliList();
    }
}

public class ScanOutput
{
    [JsonProperty("headers")]
    public List<ScanHeader> Headers { get; set; } = [];

    [JsonProperty("files")]
    public List<ScannedFile> Files { get; set; } = [];

    [JsonProperty("packages")]
    public List<BasePackage> Packages { get; set; } = [];

    [JsonProperty("dependencies")]
    public List<BasePackage> Dependencies { get; set; } = [];
}

internal class OutputFormatJson
{
    public readonly ScanOutput ScanOutput;
    public readonly ScanResult ScanResult;

    public OutputFormatJson(ScanResult scanResult)
    {
        scanResult.Sort();
        ScanResult = scanResult;

        ScanOutput = new ScanOutput();
        ScanOutput.Packages.Add(scanResult.ProjectPackage);

        ScanHeader scanHeader = new(scanResult.Options!)
        {
            ProjectFramework = scanResult.Options!.ProjectFramework!,
            Warnings = scanResult.Warnings,
            Errors = scanResult.Errors
        };
        ScanOutput.Headers.Add(scanHeader);
        ScanOutput.Dependencies = scanResult.ProjectPackage.GetFlatDependencies();
    }

    public void Write()
    {
        var outputFilePath = ScanResult.Options!.OutputFilePath;
        using var fs = new FileStream(outputFilePath!, FileMode.Create);
        using var sw = new StreamWriter(fs);
        var serializer = new JsonSerializer
        {
            Formatting = Formatting.Indented
        };
        var writer = new JsonTextWriter(sw);
        serializer.Serialize(writer, ScanOutput);
    }
}