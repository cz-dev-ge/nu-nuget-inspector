using Microsoft.ComponentDetection.Contracts;
using Microsoft.ComponentDetection.Contracts.BcdeModels;
using Microsoft.ComponentDetection.Contracts.TypedComponent;
using Microsoft.ComponentDetection.Orchestrator.Commands;
using Microsoft.ComponentDetection.Orchestrator.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
// Alias to disambiguate from NugetInspector's own ScanResult (see ProjectScanner.cs).
using CdScanResult = Microsoft.ComponentDetection.Contracts.BcdeModels.ScanResult;

namespace NugetInspector;

/// <summary>
/// Resolves NuGet dependency graphs using Microsoft.ComponentDetection
/// (https://github.com/microsoft/component-detection) instead of nuget-inspector's
/// own project.assets.json / packages.config / *proj parsers.
///
/// Microsoft.ComponentDetection's "NuGet" detector category covers:
/// - project.assets.json (NuGetProjectCentric): gives a full dependency graph with
///   direct vs. transitive information.
/// - packages.config (NuGetPackagesConfig): flat list, all entries explicit/top-level.
/// - *.nupkg / *.nuspec / nuget.config (NuGet): flat list from packages on disk.
/// </summary>
internal class ComponentDetectionProcessor(string projectDirectory) : IDependencyProcessor
{
    public const string DatasourceId = "component-detection";

    // Normalized, absolute form of the target project directory, used to filter out
    // components/graphs belonging to other manifests found deeper in the scanned tree.
    private readonly string _NormalizedProjectDirectory = NormalizeDirectory(projectDirectory);

    public DependencyResolution Resolve()
    {
        return ResolveAsync().GetAwaiter().GetResult();
    }

    private async Task<DependencyResolution> ResolveAsync()
    {
        var scanResult = await RunComponentDetectionScanAsync();
        if (scanResult == null)
            return new DependencyResolution(success: false);

        var packagesById = BuildPackagesById(scanResult);
        var topLevelIds = LinkDependencyGraphs(scanResult, packagesById);

        return BuildResolution(packagesById, topLevelIds);
    }

    /// <summary>
    /// Runs the Microsoft.ComponentDetection scan (scoped to the "NuGet" detector category)
    /// against <see cref="projectDirectory"/>. Returns null if the scan did not complete
    /// successfully.
    /// </summary>
    private async Task<CdScanResult?> RunComponentDetectionScanAsync()
    {
        var services = new ServiceCollection()
            .AddComponentDetection()
            .AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));

        var serviceProvider = services.BuildServiceProvider();

        var settings = new ScanSettings
        {
            SourceDirectory = new DirectoryInfo(projectDirectory),
            DetectorCategories = ["NuGet", "NuGetProjectCentric"],
            Output = Path.GetTempPath(),
        };

        Log.TraceDeep($"ComponentDetectionProcessor: scanning directory: {projectDirectory}");

        var scanCommand = ActivatorUtilities.CreateInstance<ScanCommand>(serviceProvider);
        var scanResult = await scanCommand.ExecuteScanCommandAsync(settings);

        if (scanResult.ResultCode == ProcessingResultCode.Success)
            return scanResult;

        Log.Trace($"ComponentDetectionProcessor: scan did not complete successfully: {scanResult.ResultCode}");
        return null;
    }

    /// <summary>
    /// Builds a lookup of component id -> BasePackage for every NuGet component found
    /// that actually belongs to this project's own manifest(s), not to some other
    /// project nested elsewhere under the scanned source directory. Microsoft.ComponentDetection's
    /// detectors scan the whole SourceDirectory tree recursively, so a monorepo-style layout
    /// with nested sub-projects would otherwise leak sibling projects' packages into this result.
    /// </summary>
    private Dictionary<string, BasePackage> BuildPackagesById(CdScanResult scanResult)
    {
        var packagesById = new Dictionary<string, BasePackage>();

        foreach (var scanned in scanResult.ComponentsFound)
        {
            if (scanned.Component is not NuGetComponent nuget)
                continue;

            if (packagesById.ContainsKey(scanned.Component.Id))
                continue;

            if (!BelongsToScannedProject(scanned.LocationsFoundAt))
                continue;

            packagesById[scanned.Component.Id] = new BasePackage(
                nuget.Name,
                ComponentType.NuGet,
                nuget.Version)
            {
                Purl = nuget.PackageUrl?.ToString() ?? "",
                DatasourceId = DatasourceId,
            };
        }

        return packagesById;
    }

    /// <summary>
    /// Wires up parent/child relationships between the given packages using the per-manifest
    /// dependency graphs, when available (this is the case for project.assets.json-based
    /// detection). Only graphs whose manifest belongs to this project directory are
    /// considered, for the same reason as in <see cref="BuildPackagesById"/>. Returns the
    /// set of explicitly referenced (top-level) component ids across those graphs.
    /// </summary>
    private HashSet<string> LinkDependencyGraphs(CdScanResult scanResult, Dictionary<string, BasePackage> packagesById)
    {
        var topLevelIds = new HashSet<string>();

        if (scanResult is not DefaultGraphScanResult { DependencyGraphs: not null } graphResult)
            return topLevelIds;

        foreach (var (manifestPath, graphWithMetadata) in graphResult.DependencyGraphs)
        {
            if (!string.Equals(GetOwningDirectory(manifestPath), _NormalizedProjectDirectory, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var (componentId, children) in graphWithMetadata.Graph)
            {
                if (children == null || !packagesById.TryGetValue(componentId, out var parent))
                    continue;

                foreach (var childId in children)
                {
                    if (packagesById.TryGetValue(childId, out var child) && !parent.Dependencies.Contains(child))
                        parent.Dependencies.Add(child);
                }
            }

            foreach (var topLevelId in graphWithMetadata.ExplicitlyReferencedComponentIds ?? [])
                topLevelIds.Add(topLevelId);
        }

        return topLevelIds;
    }

    /// <summary>
    /// Assembles the final DependencyResolution from the resolved packages and top-level ids.
    /// Detectors without graph data (e.g. packages.config, bare nuspec/nupkg scans) mark every
    /// found component as explicitly referenced; fall back to treating every found package as
    /// top-level when no graph-derived top-level set exists.
    /// </summary>
    private static DependencyResolution BuildResolution(Dictionary<string, BasePackage> packagesById, HashSet<string> topLevelIds)
    {
        var resolution = new DependencyResolution(success: true);

        foreach (var topLevelId in topLevelIds)
        {
            if (packagesById.TryGetValue(topLevelId, out var topLevel))
                resolution.Dependencies.Add(topLevel);
        }

        if (resolution.Dependencies.Count == 0)
            resolution.Dependencies.AddRange(packagesById.Values);

        return resolution;
    }

    /// <summary>
    /// True when at least one of the manifest/file locations a component was found at
    /// belongs to this project's own directory (not a nested sub-project's). A component
    /// with no reported locations is kept, to avoid dropping valid results from detectors
    /// that don't populate location info.
    /// </summary>
    /// <remarks>This is not working for projects built under Windows and scanned under Linux.</remarks>
    private bool BelongsToScannedProject(IEnumerable<string>? locationsFoundAt)
    {
        var locations = locationsFoundAt?.ToList() ?? [];
        if (locations.Count == 0)
            return true;

        return locations.Any(location => IsSameDirectory(location, _NormalizedProjectDirectory));
    }

    private bool IsSameDirectory(string directory1, string directory2)
    {
        var dir1 = GetOwningDirectory(directory1);

        dir1 = Environment.OSVersion.Platform != PlatformID.Unix
            ? dir1
            : dir1.Split(['\\','/'], StringSplitOptions.RemoveEmptyEntries).Last();
        var dir2 = Environment.OSVersion.Platform != PlatformID.Unix
            ? directory2
            : directory2.Split(['\\','/'], StringSplitOptions.RemoveEmptyEntries).Last();

        return string.Equals(dir1, dir2, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the directory that "owns" a manifest/component location reported by
    /// Microsoft.ComponentDetection. Locations may be absolute (e.g. dependency graph
    /// manifest keys) or relative to the scanned SourceDirectory (e.g. ScannedComponent.LocationsFoundAt,
    /// which look like "/Some.csproj" or "/Nested/Nested.csproj"). project.assets.json paths
    /// are un-wrapped from their "obj" folder to the actual project directory.
    /// </summary>
    private string GetOwningDirectory(string location)
    {
        var resolved = Path.IsPathFullyQualified(location)
            ? location
            : Path.Combine(projectDirectory, location.TrimStart('/', '\\'));

        resolved = Path.GetFullPath(resolved);
        var directory = Path.GetDirectoryName(resolved) ?? resolved;

        if (string.Equals(Path.GetFileName(resolved), "project.assets.json", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(directory), "obj", StringComparison.OrdinalIgnoreCase))
        {
            directory = Path.GetDirectoryName(directory) ?? directory;
        }

        return NormalizeDirectory(directory);
    }

    private static string NormalizeDirectory(string directory)
    {
        return Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
