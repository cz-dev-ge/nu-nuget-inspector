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
    public string? PackagesConfigPath { get; set; }
    public string? ProjectJsonPath { get; set; }
    public string? ProjectJsonLockPath { get; set; }
    public string? ProjectAssetsJsonPath { get; set; }
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

        // TODO: Also rarer files named packages.<project name>.config
        // See CommandLineUtility.IsValidConfigFileName(Path.GetFileName(path) 
        if (string.IsNullOrWhiteSpace(_ScannerOptions.PackagesConfigPath))
            _ScannerOptions.PackagesConfigPath = CombinePaths(projectDirectory, "packages.config");

        if (string.IsNullOrWhiteSpace(_ScannerOptions.ProjectAssetsJsonPath))
            _ScannerOptions.ProjectAssetsJsonPath = CombinePaths(projectDirectory, "obj/project.assets.json");

        if (string.IsNullOrWhiteSpace(_ScannerOptions.ProjectJsonPath))
            _ScannerOptions.ProjectJsonPath = CombinePaths(projectDirectory, "project.json");

        if (string.IsNullOrWhiteSpace(_ScannerOptions.ProjectJsonLockPath))
            _ScannerOptions.ProjectJsonLockPath = CombinePaths(projectDirectory, "project.lock.json");

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

    private static string CombinePaths(string? projectDirectory, string fileName)
    {
        return Path
            .Combine(projectDirectory ?? string.Empty, fileName)
            .Replace("\\", "/");
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
    ///     Removes all dependencies from the list which are references to projects instead of packages.
    /// </summary>
    /// <param name="scanResult"></param>
    public void RemoveInternalProjectDependencies(ScanResult scanResult)
    {
        Log.Trace("Removing dependencies which are internal project references");
        scanResult.ProjectPackage.Dependencies = scanResult.ProjectPackage.Dependencies
            .Where(dep => !dep.Type.Equals(ComponentType.Project))
            .ToList();
    }

    /// <summary>
    ///     Scan the project properly and return ScanResult for this project.
    /// </summary>
    /// <returns></returns>
    public ScanResult RunScan()
    {
        Log.Trace($"\nRunning scan of: {_ScannerOptions.ProjectFilePath} with fallback: {_ScannerOptions.WithFallback}");

        var project = new BasePackage(
            _ScannerOptions.ProjectName!,
            ComponentType.Project,
            _ScannerOptions.ProjectVersion,
            datafilePath: _ScannerOptions.ProjectFilePath
        );
        
        /*
         * Try each data file in sequence to resolve packages for a project:
         * 1. start with modern lockfiles such as project-assets.json and older projects.json.lock
         * 2. Then semi-legacy package.config package references
         * 3. Then legacy formats such as projects.json
         * 4. Then modern package references or semi-modern references using MSBuild
         * 4. Then package references as bare XML
         */

        var result = new ScanResult
        {
            Options = _ScannerOptions,
            ProjectPackage = project
        };
        
        // project.assets.json is the gold standard when available
        // TODO: make the use of lock files optional
        if (TryScanProjectAssetsJson(result)) 
            return result;

        // projects.json.lock is legacy but should be used if present
        if (TryScanProjectJsonLock(result))
            return result;

        // packages.config is semi-legacy but should be used if present over a project file
        if (TryScanPackagesConfig(result)) 
            return result;

        // project.json is legacy but should be used if present
        if (TryScanProjectJson(result)) 
            return result;

        // In the most common case we use the *proj file and its PackageReference

        if (TryScanProjectFile(result)) 
            return result;

        // In the case of older proj file we process the bare XML as a last resort option
        ScanBareXml(result);

        return result;
    }

    private void ScanBareXml(ScanResult result)
    {
        // bare XML is a fallback considered as an error even if returns something
        Log.Trace($"  Using fallback processor of project file as bare XML: {_ScannerOptions.ProjectFilePath}");

        try
        {
            var project = result.ProjectPackage;
            var resolver = new ProjectXmlFileProcessor(
                _ScannerOptions.ProjectFilePath,
                _NugetApiService,
                _ProjectFramework);

            var resolution = resolver.Resolve();
            
            project.DatasourceId = ProjectXmlFileProcessor.DatasourceId;
            project.Dependencies = resolution.Dependencies;

            if (!resolution.Success)
                return;
            
            project.DatasourceId = ProjectFileProcessor.DatasourceId;
            project.Dependencies = resolution.Dependencies;

            Log.Trace($"    Found #{project.Dependencies.Count} dependencies with data_source_id: {project.DatasourceId}");
            Log.Trace($"    Project resolved: {_ScannerOptions.ProjectName}");

            // even success here is a failure as we could not get the full power of a project resolution
            result.Status = ScanResult.ResultStatus.Success;
        }
        catch (Exception ex)
        {
            var message = $"Failed to process *.*proj project file as bare XML: {_ScannerOptions.ProjectFilePath} with:\n{ex}";
            result.Errors.Add(message);
            result.Status = ScanResult.ResultStatus.Error;
            Log.Trace($"\nERROR: {message}\n");
        }
    }

    private bool TryScanProjectFile(ScanResult result)
    {
        // first we try using MSBuild to read the project
        Log.Trace($"  Using project file: {_ScannerOptions.ProjectFilePath}");

        try
        {
            var project = result.ProjectPackage;
            var resolver = new ProjectFileProcessor(
                _ScannerOptions.ProjectFilePath,
                _NugetApiService,
                _ProjectFramework);

            var resolution = resolver.Resolve();

            if (resolution.Success)
            {
                project.DatasourceId = ProjectFileProcessor.DatasourceId;
                project.Dependencies = resolution.Dependencies;
                result.Status = ScanResult.ResultStatus.Success;
                
                Log.Trace($"    Found #{project.Dependencies.Count} dependencies with data_source_id: {project.DatasourceId}");
                Log.Trace($"    Project resolved: {_ScannerOptions.ProjectName}");

                return true;
            }
        }
        catch (Exception ex)
        {
            var message = $"Failed to process project file: {_ScannerOptions.ProjectFilePath} with:\n{ex}";
            result.Errors.Add(message);
            result.Status = ScanResult.ResultStatus.Error;
            Log.Trace($"\nERROR: {message}\n");
        }

        return !_ScannerOptions.WithFallback;
    }

    private bool TryScanProjectJson(ScanResult result)
    {
        if (!FileExists(_ScannerOptions.ProjectJsonPath!))
            return false;
            
        Log.Trace($"  Using legacy project.json lockfile: {_ScannerOptions.ProjectJsonPath}");
        try
        {
            var project = result.ProjectPackage;
            var resolver = new ProjectJsonProcessor(
                _ScannerOptions.ProjectName,
                _ScannerOptions.ProjectJsonPath!);
            var resolution = resolver.Resolve();
            project.DatasourceId = ProjectJsonProcessor.DatasourceId;
            project.Dependencies = resolution.Dependencies;

            Log.Trace($"    Found #{project.Dependencies.Count} dependencies with data_source_id: {project.DatasourceId}");
            Log.Trace($"    Project resolved: {_ScannerOptions.ProjectName}");
            return true;
        }
        catch (Exception ex)
        {
            var message = $"Failed to process project.json lockfile: {_ScannerOptions.ProjectJsonPath} with: {ex}";
            result.Warnings.Add(message);
            Log.Trace($"    {message}");
        }

        return false;
    }

    private bool TryScanPackagesConfig(ScanResult result)
    {
        if (!FileExists(_ScannerOptions.PackagesConfigPath!))
            return false;
        
        Log.Trace($"  Using packages.config references: {_ScannerOptions.PackagesConfigPath}");
        try
        {
            var project = result.ProjectPackage;
            var resolver = new PackagesConfigProcessor(
                _ScannerOptions.PackagesConfigPath!,
                _NugetApiService,
                _ProjectFramework);
            var resolution = resolver.Resolve();
            project.DatasourceId = PackagesConfigProcessor.DatasourceId;
            project.Dependencies = resolution.Dependencies;
                
            Log.Trace($"    Found #{project.Dependencies.Count} dependencies with data_source_id: {project.DatasourceId}");
            Log.Trace($"    Project resolved: {_ScannerOptions.ProjectName}");

            return true;
        }
        catch (Exception ex)
        {
            var message = $"Failed to process packages.config references: {_ScannerOptions.PackagesConfigPath} with: {ex}";
            result.Errors.Add(message);
            Log.Trace($"    {message}");
        }

        return false;
    }

    private bool TryScanProjectJsonLock(ScanResult result)
    {
        if (!FileExists(_ScannerOptions.ProjectJsonLockPath!))
            return false;
        
        Log.Trace($"  Using projects.json.lock lockfile: {_ScannerOptions.ProjectJsonLockPath}");
        try
        {
            var project = result.ProjectPackage;
            var resolver = new ProjectLockJsonProcessor(_ScannerOptions.ProjectJsonLockPath!);
            var resolution = resolver.Resolve();
            project.DatasourceId = ProjectLockJsonProcessor.DatasourceId;
            project.Dependencies = resolution.Dependencies;
                
            Log.Trace($"    Found #{project.Dependencies.Count} dependencies with data_source_id: {project.DatasourceId}");
            Log.Trace($"    Project resolved: {_ScannerOptions.ProjectName}");

            return true;
        }
        catch (Exception ex)
        {
            var message = $"    Failed to process projects.json.lock lockfile: {_ScannerOptions.ProjectJsonLockPath} with: {ex}";
            result.Warnings.Add(message);
            Log.Trace($"    {message}");
        }

        return false;
    }

    private bool TryScanProjectAssetsJson(ScanResult result )
    {
        if (!FileExists(_ScannerOptions.ProjectAssetsJsonPath!))
            return false;
        
        Log.Trace($"  Using project.assets.json lockfile at: {_ScannerOptions.ProjectAssetsJsonPath}");
        try
        {
            var project = result.ProjectPackage;
            var resolver = new ProjectAssetsJsonProcessor(_ScannerOptions.ProjectAssetsJsonPath!);
            var resolution = resolver.Resolve();

            Log.Info("## DEPENDENCIES ##");
            foreach (var dep in resolution.Dependencies)
                Log.Info($"{dep.Name} {dep.Type} ");

            project.DatasourceId = ProjectAssetsJsonProcessor.DatasourceId;
            project.Dependencies = resolution.Dependencies;
                
            Log.Trace($"    Found #{project.Dependencies.Count} dependencies with data_source_id: {project.DatasourceId}");
            Log.Trace($"    Project resolved: {_ScannerOptions.ProjectName}");

            return true;
        }
        catch (Exception ex)
        {
            var message = $"    Failed to process project.assets.json lockfile: {_ScannerOptions.ProjectAssetsJsonPath} with: {ex}";
            result.Warnings.Add(message);
            Log.Trace($"    {message}");
        }

        return false;
    }

    /// <summary>
    ///     Return true if the "path" strings is an existing file.
    /// </summary>
    /// <param name="path"></param>
    /// <returns>bool</returns>
    private static bool FileExists(string path)
    {
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }
}