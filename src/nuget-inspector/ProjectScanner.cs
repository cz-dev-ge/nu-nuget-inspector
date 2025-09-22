using NuGet.Frameworks;

namespace NugetInspector;

public class ScanResult
{
    public enum ResultStatus
    {
        Success,
        Error,
        Warning,
    }

    public Exception? Exception;
    public ProjectScannerOptions? Options;
    public BasePackage ProjectPackage = new();
    public ResultStatus Status;

    public List<string> Warnings = [];
    public List<string> Errors = [];

    public void Sort()
    {
        ProjectPackage.Sort();
    }
}

/// <summary>
/// An Options subclass that track project scan-specific options.
/// </summary>
public class ProjectScannerOptions : Options
{
    public string? ProjectName { get; set; }
    public string? ProjectVersion { get; set; }
    public string ProjectDirectory { get; set; } = "";
    public string? PackagesConfigPath { get; set; }
    public string? ProjectJsonPath { get; set; }
    public string? ProjectJsonLockPath { get; set; }
    public string? ProjectAssetsJsonPath { get; set; }
    public string? ProjectFramework { get; set; } = "";

    public ProjectScannerOptions(Options options)
    {
        ProjectFilePath = options.ProjectFilePath;
        TargetFramework = options.TargetFramework;
        ProjectDirectory = Directory.GetParent(options.ProjectFilePath)?.FullName ?? string.Empty;
        Verbose = options.Verbose;
        NugetConfigPath = options.NugetConfigPath;
        OutputFilePath = options.OutputFilePath;
    }
}

internal class ProjectScanner
{
    public readonly ProjectScannerOptions ScannerOptions;
    public readonly NugetApi NugetApiService;
    public readonly NuGetFramework ProjectFramework;

    /// <summary>
    /// A Scanner for project "*proj" project file input such as .csproj file
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
        static string CombinePaths(string? projectDirectory, string fileName)
        {
            return Path
                .Combine(projectDirectory ?? string.Empty, fileName)
                .Replace("\\", "/");
        }

        ScannerOptions = options;
        NugetApiService = nugetApiService;
        this.ProjectFramework = projectFramework;

        if (string.IsNullOrWhiteSpace(ScannerOptions.OutputFilePath))
        {
            throw new Exception("Missing required output JSON file path.");
        }

        if (string.IsNullOrWhiteSpace(ScannerOptions.ProjectDirectory))
            ScannerOptions.ProjectDirectory = Directory.GetParent(ScannerOptions.ProjectFilePath)?.FullName ?? string.Empty;

        var projectDirectory = ScannerOptions.ProjectDirectory;

        // TODO: Also rarer files named packages.<project name>.config
        // See CommandLineUtility.IsValidConfigFileName(Path.GetFileName(path) 
        if (string.IsNullOrWhiteSpace(ScannerOptions.PackagesConfigPath))
            ScannerOptions.PackagesConfigPath = CombinePaths(projectDirectory, "packages.config");

        if (string.IsNullOrWhiteSpace(ScannerOptions.ProjectAssetsJsonPath))
            ScannerOptions.ProjectAssetsJsonPath = CombinePaths(projectDirectory, "obj/project.assets.json");

        if (string.IsNullOrWhiteSpace(ScannerOptions.ProjectJsonPath))
            ScannerOptions.ProjectJsonPath = CombinePaths(projectDirectory, "project.json");

        if (string.IsNullOrWhiteSpace(ScannerOptions.ProjectJsonLockPath))
            ScannerOptions.ProjectJsonLockPath = CombinePaths(projectDirectory, "project.lock.json");

        if (string.IsNullOrWhiteSpace(ScannerOptions.ProjectName))
        {
            ScannerOptions.ProjectName = Path.GetFileNameWithoutExtension(ScannerOptions.ProjectFilePath);
            if (Config.TRACE)
                Console.WriteLine($"\nProjectScanner: Using filename as project name: {ScannerOptions.ProjectName}");
        }

        if (!string.IsNullOrWhiteSpace(ScannerOptions.ProjectVersion))
            return;
        
        ScannerOptions.ProjectVersion = AssemblyInfoParser.GetProjectAssemblyVersion(projectDirectory);
        if (Config.TRACE)
        {
            Console.WriteLine(!string.IsNullOrWhiteSpace(ScannerOptions.ProjectVersion)
                ? $"      Using AssemblyInfoParser for project version: {ScannerOptions.ProjectVersion}"
                : "      No project version found");
        }
    }

    /// <summary>
    /// Enhance the dependencies recursively in scan results with metadata
    /// fetched from the NuGet API.
    /// </summary>
    /// <param name="scanResult"></param>
    public void FetchDependenciesMetadata(ScanResult scanResult, bool withDetails = false)
    {
        if (Config.TRACE_META)
            Console.WriteLine($"\nFetchDependenciesMetadata: with_details: {withDetails}");

        foreach (var dep in scanResult.ProjectPackage.Dependencies)
        {
            Console.WriteLine($"ProjectScanner > FetchDependenciesMetadata |{dep.Type} {dep.Name}" );
            if (dep.Type.Equals( ComponentType.Project))
            {
                Console.WriteLine($"    Internal project {dep.Name} - skipping metadata fetching");
                continue;
            }
            
            dep.Update(NugetApiService, withDetails);

            if (Config.TRACE_META)
                Console.WriteLine($"    Fetched for {dep.Name}@{dep.Version}");
        }
    }
    
    /// <summary>
    /// Removes all dependencies from the list which are references to projects instead of packages.
    /// </summary>
    /// <param name="scanResult"></param>
    public void RemoveInternalProjectDependencies(ScanResult scanResult)
    {
        if (Config.TRACE)
            Console.WriteLine($"Removing dependencies which are internal project references");
        scanResult.ProjectPackage.Dependencies = scanResult.ProjectPackage.Dependencies
            .Where(dep => !dep.Type.Equals(ComponentType.Project) )
            .ToList();
    }

    /// <summary>
    /// Scan the project properly and return ScanResult for this project.
    /// </summary>
    /// <returns></returns>
    public ScanResult RunScan()
    {
        if (Config.TRACE)
            Console.WriteLine($"\nRunning scan of: {ScannerOptions.ProjectFilePath} with fallback: {ScannerOptions.WithFallback}");

        var project = new BasePackage(
            ScannerOptions.ProjectName!,
            ComponentType.Project,
            version: ScannerOptions.ProjectVersion,
            datafilePath: ScannerOptions.ProjectFilePath
        );

        var scanResult = new ScanResult() {
            Options = ScannerOptions,
            ProjectPackage = project
        };

        /*
         * Try each data file in sequence to resolve packages for a project:
         * 1. start with modern lockfiles such as project-assets.json and older projects.json.lock
         * 2. Then semi-legacy package.config package references
         * 3. Then legacy formats such as projects.json
         * 4. Then modern package references or semi-modern references using MSbuild
         * 4. Then package references as bare XML
         */

        DependencyResolution resolution;
        IDependencyProcessor resolver;

        // project.assets.json is the gold standard when available
        // TODO: make the use of lock files optional
        if (FileExists(ScannerOptions.ProjectAssetsJsonPath!))
        {
            if (Config.TRACE)
                Console.WriteLine($"  Using project.assets.json lockfile at: {ScannerOptions.ProjectAssetsJsonPath}");
            try
            {
                resolver = new ProjectAssetsJsonProcessor(ScannerOptions.ProjectAssetsJsonPath!);
                resolution = resolver.Resolve();
                
                Console.WriteLine("## DEPENDENCIES ##");
                foreach( var dep in resolution.Dependencies )
                    Console.WriteLine($"{dep.Name} {dep.Type} ");
                
                project.DatasourceId = ProjectAssetsJsonProcessor.DatasourceId;
                project.Dependencies = resolution.Dependencies;
                if (!Config.TRACE)
                    return scanResult;
                
                Console.WriteLine($"    Found #{project.Dependencies.Count} dependencies with data_source_id: {project.DatasourceId}");
                Console.WriteLine($"    Project resolved: {ScannerOptions.ProjectName}");
                return scanResult;
            }
            catch (Exception ex)
            {
                var message = $"    Failed to process project.assets.json lockfile: {ScannerOptions.ProjectAssetsJsonPath} with: {ex}";
                scanResult.Warnings.Add(message);
                if (Config.TRACE) Console.WriteLine($"    {message}");
            }
        }

        // projects.json.lock is legacy but should be used if present
        if (FileExists(ScannerOptions.ProjectJsonLockPath!))
        {
            if (Config.TRACE)
                Console.WriteLine($"  Using projects.json.lock lockfile: {ScannerOptions.ProjectJsonLockPath}");
            try
            {
                resolver = new ProjectLockJsonProcessor(ScannerOptions.ProjectJsonLockPath!);
                resolution = resolver.Resolve();
                project.DatasourceId = ProjectLockJsonProcessor.DatasourceId;
                project.Dependencies = resolution.Dependencies;
                if (Config.TRACE)
                {
                    Console.WriteLine($"    Found #{project.Dependencies.Count} dependencies with data_source_id: {project.DatasourceId}");
                    Console.WriteLine($"    Project resolved: {ScannerOptions.ProjectName}");
                }
                return scanResult;
            }
            catch (Exception ex)
            {
                var message = $"    Failed to process projects.json.lock lockfile: {ScannerOptions.ProjectJsonLockPath} with: {ex}";
                scanResult.Warnings.Add(message);
                if (Config.TRACE) Console.WriteLine($"    {message}");
            }
        }

        // packages.config is semi-legacy but should be used if present over a project file
        if (FileExists(ScannerOptions.PackagesConfigPath!))
        {
            if (Config.TRACE)
                Console.WriteLine($"  Using packages.config references: {ScannerOptions.PackagesConfigPath}");
            try
            {
                resolver = new PackagesConfigProcessor(
                    ScannerOptions.PackagesConfigPath!,
                    NugetApiService,
                    ProjectFramework);
                resolution = resolver.Resolve();
                project.DatasourceId = PackagesConfigProcessor.DatasourceId;
                project.Dependencies = resolution.Dependencies;
                if (Config.TRACE)
                {
                    Console.WriteLine($"    Found #{project.Dependencies.Count} dependencies with data_source_id: {project.DatasourceId}");
                    Console.WriteLine($"    Project resolved: {ScannerOptions.ProjectName}");
                }
                return scanResult;
            }
            catch (Exception ex)
            {
                var message = $"Failed to process packages.config references: {ScannerOptions.PackagesConfigPath} with: {ex}";
                scanResult.Errors.Add(message);
                if (Config.TRACE) Console.WriteLine($"    {message}");
            }
        }

        // project.json is legacy but should be used if present
        if (FileExists(ScannerOptions.ProjectJsonPath!))
        {
            if (Config.TRACE) Console.WriteLine($"  Using legacy project.json lockfile: {ScannerOptions.ProjectJsonPath}");
            try
            {
                resolver = new ProjectJsonProcessor(
                    ScannerOptions.ProjectName,
                    ScannerOptions.ProjectJsonPath!);
                resolution = resolver.Resolve();
                project.DatasourceId = ProjectJsonProcessor.DatasourceId;
                project.Dependencies = resolution.Dependencies;
                if (!Config.TRACE)
                    return scanResult;
                
                Console.WriteLine($"    Found #{project.Dependencies.Count} dependencies with data_source_id: {project.DatasourceId}");
                Console.WriteLine($"    Project resolved: {ScannerOptions.ProjectName}");
                return scanResult;
            }
            catch (Exception ex)
            {
                var message = $"Failed to process project.json lockfile: {ScannerOptions.ProjectJsonPath} with: {ex}";
                scanResult.Warnings.Add(message);
                if (Config.TRACE) Console.WriteLine($"    {message}");
            }
        }

        // In the most common case we use the *proj file and its PackageReference

        // first we try using MSbuild to read the project
        if (Config.TRACE)
            Console.WriteLine($"  Using project file: {ScannerOptions.ProjectFilePath}");

        try
        {
            resolver = new ProjectFileProcessor(
                ScannerOptions.ProjectFilePath,
                NugetApiService,
                ProjectFramework);

            resolution = resolver.Resolve();

            if (resolution.Success)
            {
                project.DatasourceId = ProjectFileProcessor.DatasourceId;
                project.Dependencies = resolution.Dependencies;
                scanResult.Status = ScanResult.ResultStatus.Success;
                if (Config.TRACE)
                {
                    Console.WriteLine($"    Found #{project.Dependencies.Count} dependencies with data_source_id: {project.DatasourceId}");
                    Console.WriteLine($"    Project resolved: {ScannerOptions.ProjectName}");
                }
                return scanResult;
            }
        }
        catch (Exception ex)
        {
            var message = $"Failed to process project file: {ScannerOptions.ProjectFilePath} with:\n{ex}";
            scanResult.Errors.Add(message);
            scanResult.Status = ScanResult.ResultStatus.Error;
            if (Config.TRACE) Console.WriteLine($"\nERROR: {message}\n");
        }

        if (!ScannerOptions.WithFallback)
            return scanResult;

        // In the case of older proj file we process the bare XML as a last resort option
        // bare XML is a fallback considered as an error even if returns something
        if (Config.TRACE)
            Console.WriteLine($"  Using fallback processor of project file as bare XML: {ScannerOptions.ProjectFilePath}");

        try
        {
            resolver = new ProjectXmlFileProcessor(
            ScannerOptions.ProjectFilePath,
            NugetApiService,
            ProjectFramework);

            resolution = resolver.Resolve();

            project.DatasourceId = ProjectXmlFileProcessor.DatasourceId;
            project.Dependencies = resolution.Dependencies;

            if (resolution.Success)
            {
                project.DatasourceId = ProjectFileProcessor.DatasourceId;
                project.Dependencies = resolution.Dependencies;
                if (Config.TRACE)
                {
                    Console.WriteLine($"    Found #{project.Dependencies.Count} dependencies with data_source_id: {project.DatasourceId}");
                    Console.WriteLine($"    Project resolved: {ScannerOptions.ProjectName}");
                }
                // even success here is a failure as we could not get the full power of a project resolution
                scanResult.Status = ScanResult.ResultStatus.Success;
                return scanResult;
            }
        }
        catch (Exception ex)
        {
            var message = $"Failed to process *.*proj project file as bare XML: {ScannerOptions.ProjectFilePath} with:\n{ex}";
            scanResult.Errors.Add(message);
            scanResult.Status = ScanResult.ResultStatus.Error;
            if (Config.TRACE) Console.WriteLine($"\nERROR: {message}\n");
        }

        return scanResult;
    }

    /// <summary>
    /// Return true if the "path" strings is an existing file.
    /// </summary>
    /// <param name="path"></param>
    /// <returns>bool</returns>
    private static bool FileExists(string path)
    {
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }
}
