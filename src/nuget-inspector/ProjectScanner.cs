using NuGet.Frameworks;

namespace NugetInspector;

public class ScanResult
{
    public enum ResultStatus
    {
        Success,
        Error,
        Warning
    }

    public List<string> Errors { get; } = [];

    public Exception? Exception { get; set; }
    public required ProjectScannerOptions Options { get; init; }
    public required BasePackage ProjectPackage { get; init; }
    public ResultStatus Status { get; set; }

    public List<string> Warnings { get; } = [];

    public void Sort()
    {
        ProjectPackage.Sort();
    }
}

/// <summary>
///     An Options subclass that track project scan-specific options.
/// </summary>
public class ProjectScannerOptions : Options
{
    public ProjectScannerOptions(Options options)
    {
        ProjectFilePath = options.ProjectFilePath;
        TargetFramework = options.TargetFramework;
        ProjectDirectory = Directory.GetParent(options.ProjectFilePath)?.FullName ?? "";
        Verbose = options.Verbose;
        NugetConfigPath = options.NugetConfigPath;
        OutputFilePath = options.OutputFilePath;
    }

    public string? ProjectName { get; set; }
    public string? ProjectVersion { get; set; }
    public string ProjectDirectory { get; set; }
    public string ProjectFramework { get; set; } = "";
}

internal class ProjectScanner
{
    private readonly NugetApi _NugetApiService;
    private readonly NuGetFramework _ProjectFramework;
    private readonly ProjectScannerOptions _ScannerOptions;

    /// <summary>
    ///     A Scanner for project "*proj" project file input such as .csproj file
    /// </summary>
    /// <param name="options"></param>
    /// <param name="nugetApiService"></param>
    /// <param name="projectFramework"></param>
    /// <exception cref="Exception"></exception>
    public ProjectScanner(
        ProjectScannerOptions options,
        NugetApi nugetApiService,
        NuGetFramework projectFramework)
    {
        _ScannerOptions = options;
        _NugetApiService = nugetApiService;
        _ProjectFramework = projectFramework;

        if (string.IsNullOrWhiteSpace(_ScannerOptions.OutputFilePath))
            throw new Exception("Missing required output JSON file path.");

        if (string.IsNullOrWhiteSpace(_ScannerOptions.ProjectDirectory))
            _ScannerOptions.ProjectDirectory = Directory.GetParent(_ScannerOptions.ProjectFilePath)?.FullName ?? "";

        var projectDirectory = _ScannerOptions.ProjectDirectory;

        if (string.IsNullOrWhiteSpace(_ScannerOptions.ProjectName))
        {
            _ScannerOptions.ProjectName = Path.GetFileNameWithoutExtension(_ScannerOptions.ProjectFilePath);
            Log.Trace($"\nProjectScanner: Using filename as project name: {_ScannerOptions.ProjectName}");
        }

        if (!string.IsNullOrWhiteSpace(_ScannerOptions.ProjectVersion))
            return;

        _ScannerOptions.ProjectVersion = AssemblyInfoParser.GetProjectAssemblyVersion(projectDirectory);
        Log.Trace(string.IsNullOrWhiteSpace(_ScannerOptions.ProjectVersion)
                ? "      No project version found"
                : $"      Using AssemblyInfoParser for project version: {_ScannerOptions.ProjectVersion}");
    }

    /// <summary>
    ///     Enhance the dependencies recursively in scan results with metadata
    ///     fetched from the NuGet API.
    /// </summary>
    public void FetchDependenciesMetadata(ScanResult scanResult, bool withDetails = false)
    {
        Log.TraceMeta($"\nFetchDependenciesMetadata: with_details: {withDetails}");

        foreach (var dep in scanResult.ProjectPackage.Dependencies)
        {
            Log.Info($"ProjectScanner > FetchDependenciesMetadata |{dep.Type} {dep.Name}");
            if (dep.Type.Equals(ComponentType.Project))
            {
                Log.Info($"    Internal project {dep.Name} - skipping metadata fetching");
                continue;
            }

            dep.Update(_NugetApiService, withDetails);

            Log.TraceMeta($"    Fetched for {dep.Name}@{dep.Version}");
        }
    }

    /// <summary>
    ///     Scan the project properly and return ScanResult for this project.
    /// </summary>
    /// <returns></returns>
    public ScanResult RunScan()
    {
        Log.Trace($"\nRunning scan of: {_ScannerOptions.ProjectFilePath}");

        var project = new BasePackage(
            _ScannerOptions.ProjectName!,
            ComponentType.Project,
            _ScannerOptions.ProjectVersion,
            datafilePath: _ScannerOptions.ProjectFilePath
        );

        var result = new ScanResult
        {
            Options = _ScannerOptions,
            ProjectPackage = project
        };

        TryScanWithComponentDetection(result);

        return result;
    }

    /// <summary>
    ///     Resolve the NuGet dependency graph for the project directory using
    ///     Microsoft.ComponentDetection instead of nuget-inspector's own parsers
    ///     for project.assets.json, packages.config, project.json and *proj files.
    /// </summary>
    private bool TryScanWithComponentDetection(ScanResult result)
    {
        Log.Trace($"  Using Microsoft.ComponentDetection to resolve NuGet dependencies in: {_ScannerOptions.ProjectDirectory}");

        try
        {
            var resolver = new ComponentDetectionProcessor(_ScannerOptions.ProjectDirectory);
            var resolution = resolver.Resolve();

            result.ProjectPackage.DatasourceId = ComponentDetectionProcessor.DatasourceId;
            result.ProjectPackage.Dependencies = resolution.Dependencies;

            result.Status = resolution.Success
                ? ScanResult.ResultStatus.Success
                : ScanResult.ResultStatus.Error;

            if (!resolution.Success)
                result.Errors.Add(
                    $"Microsoft.ComponentDetection scan of {_ScannerOptions.ProjectDirectory} did not complete successfully.");

            Log.Trace($"    Found #{result.ProjectPackage.Dependencies.Count} top-level dependencies with data_source_id: {result.ProjectPackage.DatasourceId}");
            Log.Trace($"    Project resolved: {_ScannerOptions.ProjectName}");

            return resolution.Success;
        }
        catch (Exception ex)
        {
            var message = $"Failed to resolve dependencies via Microsoft.ComponentDetection for: {_ScannerOptions.ProjectDirectory} with:\n{ex}";
            result.Errors.Add(message);
            result.Status = ScanResult.ResultStatus.Error;
            Log.Trace($"\nERROR: {message}\n");
            return false;
        }
    }
}