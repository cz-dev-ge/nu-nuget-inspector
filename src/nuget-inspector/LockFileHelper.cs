using System.Diagnostics.CodeAnalysis;
using NuGet.ProjectModel;
using NuGet.Versioning;

namespace NugetInspector;

/// <summary>
/// Parse legacy style (project-lock.json) and new style lock files (project.assets.json)
/// See https://learn.microsoft.com/en-us/nuget/archive/project-json#projectlockjson
/// See https://kimsereyblog.blogspot.com/2018/08/sdk-style-project-and-projectassetsjson.html
/// See https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-build?tabs=netcore2x#implicit-restore
/// </summary>
public class LockFileHelper(LockFile lockfile)
{
    private static NuGetVersion? GetBestVersion(string name, VersionRange range, IList<LockFileTargetLibrary> libraries)
    {
        var versions = libraries
            .Where(lib => lib.Name == name && lib.Version != null )
            .Select(lib => lib.Version)
            .Cast<NuGetVersion>()
            .ToList();
        
        var bestVersion = range.FindBestMatch(versions);
        
        if (bestVersion != null)
            return bestVersion;

        if (versions.Count == 1)
            return versions[0];

        Log.TraceDeep($"GetBestVersion: WARNING: Unable to find a '{name}' version that satisfies range {range.PrettyPrint()}");
        Log.TraceDeep($"    Using min version in range: {range.MinVersion?.ToFullString()}");

        return range.MinVersion;
    }

    private static NuGetVersion? GetBestLibraryVersion(string? name, VersionRange range, IList<LockFileLibrary> libraries)
    {
        var versions = libraries
            .Where(lib => lib.Name == name)
            .Select(lib => lib.Version)
            .ToList();
        
        var bestVersion = range.FindBestMatch(versions);
        if (bestVersion != null)
            return bestVersion;

        if (versions.Count == 1)
            return versions[0];

        Log.Trace($"GetBestLibraryVersion: WARNING: Unable to find a '{name}' version that satisfies range {range.PrettyPrint()}");

        if (range is { HasUpperBound: true, HasLowerBound: false })
        {
            Log.Trace($"    Using max version in range: {range.MaxVersion.ToFullString()}");
            return range.MaxVersion;
        }

        Log.Trace($"    Using min version in range: {range.MinVersion?.ToFullString()}");
        return range.MinVersion;
    }

    private static string GetComponentType(List<string> projectReferences, string dependencyName)
    {
        return projectReferences.Contains( dependencyName ) 
            ? ComponentType.Project 
            : ComponentType.NuGet;
    }
    
    public DependencyResolution Process()
    {
        var treeBuilder = new PackageTree();
        var resolution = new DependencyResolution();
        var projectReferences = lockfile
            .Libraries
            .Where( l => l.Type.Equals(ComponentType.Project) )
            .Select( l =>  l.Name)
            .ToList();
        
        Log.Trace($"LockFile.PackageSpec: {lockfile.PackageSpec}");
        Log.Trace($"LockFile.PackageSpec.TargetFrameworks: {lockfile.PackageSpec?.TargetFrameworks}");

        foreach (var target in lockfile.Targets)
        {
            foreach (var library in target.Libraries)
            {
                var type = library.Type;
                var version = library.Version?.ToNormalizedString();
                
                var package = new BasePackage(library.Name ?? "Error: Library name unknown", type ?? "Unknown", version);

                var dependencies = new List<BasePackage?>();
                foreach (var dependency in library.Dependencies)
                {
                    var depName = dependency.Id;
                    var depVersionRange = dependency.VersionRange;
                    //vr.Float.FloatBehavior = NuGet.Versioning.NuGetVersionFloatBehavior.
                    var libraries = target.Libraries;
                    var bestVersion = GetBestVersion(depName, depVersionRange, libraries);
                    if (bestVersion == null)
                    {
                        Log.Trace(dependency.Id);
                        
                        _ = GetBestVersion(depName, depVersionRange, libraries);
                    }
                    else
                    {
                        var dependencyType = projectReferences.Contains( depName) ? ComponentType.Project : ComponentType.NuGet;
                        Log.Info($"ProjectLockFile > Target > Libraries > Dependencies | Adding dependency {dependencyType}  {depName} to dependencies");
                        var depId = new BasePackage(depName, dependencyType, bestVersion.ToNormalizedString());

                        dependencies.Add(depId);
                    }
                }

                treeBuilder.AddOrUpdatePackage(package, dependencies);
            }
        }

        Log.Trace($"LockFile: {lockfile}");
        Log.Trace($"LockFile.Path: {lockfile.Path}");

        if (lockfile?.PackageSpec?.Dependencies is { Count: > 0 })
        {
            foreach (var dep in lockfile.PackageSpec.Dependencies)
            {
                if (dep.LibraryRange.VersionRange is null)
                    throw new ArgumentException("Version range cannot be null");
                
                var version = treeBuilder.GetResolvedVersion(dep.Name, dep.LibraryRange.VersionRange);
                var dependencyType = GetComponentType( projectReferences, dep.Name );
                Log.Info($"ProjectLockFile > PackageSpec > Dependencies | Adding dependency {dependencyType}  {dep.Name} to dependencies");
                resolution.Dependencies.Add(item: new BasePackage(name: dep.Name, dependencyType, version: version));
            }
        }
        else
        {
            Log.Trace($"LockFile.PackageSpec: {lockfile?.PackageSpec}");
            Log.Trace($"LockFile.PackageSpec.TargetFrameworks: {lockfile?.PackageSpec?.TargetFrameworks}");

            var targetFrameworks = lockfile
                ?.PackageSpec
                ?.TargetFrameworks 
                                   ?? new List<TargetFrameworkInformation>();
            
            foreach (var framework in targetFrameworks)
            {
                foreach (var dep in framework.Dependencies)
                {
                    if (dep.LibraryRange.VersionRange is null)
                        throw new ArgumentException("Version range cannot be null");
                    
                    var dependencyType = GetComponentType( projectReferences, dep.Name );
                    Console.WriteLine($"ProjectLockFile > PackageSpec > TargetFramework > Dependencies | Adding dependency {dependencyType}  {dep.Name} to dependencies");

                    var version = treeBuilder.GetResolvedVersion(dep.Name, dep.LibraryRange.VersionRange);
                    resolution.Dependencies.Add(item: new BasePackage(name: dep.Name, dependencyType, version: version));
                }
            }
        }

        if (lockfile == null)
            return resolution;

        foreach (var dependencyGroup in lockfile.ProjectFileDependencyGroups)
        {
            foreach (var dependency in dependencyGroup.Dependencies)
            {
                var component = DetectDependency(dependencyGroup, dependency, projectReferences);
                
                Console.WriteLine($"ProjectLockFile > ProjectFileDependencyGroups > Dependencies | Adding dependency {component.Type}  {component.Name} to dependencies");
                resolution.Dependencies.Add(component);
            }
        }

        if (resolution.Dependencies.Count == 0 && Config.TRACE) 
            Console.WriteLine($"Found no dependencies for lock file: {lockfile.Path}");
        
        return resolution;
    }

    private BasePackage DetectDependency(ProjectFileDependencyGroup dependencyGroup, string dependency, List<string> projectReferences )
    {
        // if it is an external reference (not another project), we should find a reference in the
        // framework dependencies
        if( !TryGetPackageDependency( dependencyGroup, dependency, out var projectDependency ))
            projectDependency = ParseProjectFileDependencyGroup(dependency);
                
        var libraryVersion = GetBestLibraryVersion(
            projectDependency.GetName(),
            projectDependency.GetVersionRange(), 
            lockfile.Libraries);
                    
        string? version = null;
        if (libraryVersion != null) 
            version = libraryVersion.ToNormalizedString();
                
        var name = projectDependency.GetName()!;
                
        var dependencyType = GetComponentType( projectReferences, name );

        return new BasePackage(name, dependencyType, version);
    }

    /// <summary>
    /// If the framework dependencies contain a matching package, use that version range.
    /// </summary>
    private bool TryGetPackageDependency(
        ProjectFileDependencyGroup dependencyGroup,
        string dependency,
        [NotNullWhen(true)] out ProjectFileDependency? packageDependency )
    {
        packageDependency = null;
        var packageId = dependency.Split(' ')[0];
        var targetFrameworkInformation = lockfile
            .PackageSpec
            ?.TargetFrameworks
            ?.Where(x => x.FrameworkName.ToString().Equals(dependencyGroup.FrameworkName));
        if (targetFrameworkInformation?.FirstOrDefault() is not { } framework)
            return packageDependency != null;
        
        var frameworkDependency = framework
            .Dependencies
            .FirstOrDefault(x => x.Name.Equals(packageId, StringComparison.InvariantCultureIgnoreCase));
        var range = frameworkDependency?.LibraryRange.VersionRange;

        if (range != null)
            packageDependency = new ProjectFileDependency(packageId, range);

        return packageDependency != null;
    }

    /// <summary>
    /// Parse a ProjectFile DependencyGroup
    /// </summary>
    /// <param name="projectFileDependency"></param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    ///
    /// See: https://github.com/NuGet/NuGet.Client/blob/538727480d93b7d8474329f90ccb9ff3b3543714/nuget-inspector/NuGet.Core/NuGet.LibraryModel/LibraryRange.cs#L68
    /// FIXME: there are some rare cases where we have multiple constraints as in this JSON snippet for FSharp.Core:
    /// "projectFileDependencyGroups": {
    ///     ".NETFramework,Version=v4.7.2": ["FSharp.Core >= 4.3.4 < 5.0.0",]
    /// },
    /// This a case that is NOT handled yet
    public static ProjectFileDependency ParseProjectFileDependencyGroup(string projectFileDependency)
    {
        if (TryParseProjectFileDependencyGroupTokens(
                projectFileDependency,
                " >= ",
                out var projectName,
                out var versionRaw))
        {
            return new ProjectFileDependency(
                projectName,
                MinVersionOrFloat(
                    versionRaw,
                    true));
        }

        if (TryParseProjectFileDependencyGroupTokens(
                projectFileDependency,
                " > ",
                out var projectName2,
                out var versionRaw2))
        {
            return new ProjectFileDependency(
                projectName2,
                MinVersionOrFloat(
                    versionRaw2,
                    false));
        }

        if (TryParseProjectFileDependencyGroupTokens(
                projectFileDependency,
                " <= ",
                out var projectName3,
                out var versionRaw3))
        {
            var maxVersion = NuGetVersion.Parse(versionRaw3);
            return new ProjectFileDependency(
                projectName3,
                new VersionRange(
                    null,
                    false,
                    maxVersion,
                    true));
        }

        if (!TryParseProjectFileDependencyGroupTokens(
                projectFileDependency,
                " < ",
                out var projectName4,
                out var versionRaw4))
            throw new Exception($"Unable to parse project file dependency group: {projectFileDependency}");
        
        var maxVersion2 = NuGetVersion.Parse(versionRaw4);
        return new ProjectFileDependency(
            projectName4,
            new VersionRange(
                null,
                false,
                maxVersion2));
    }

    private static bool TryParseProjectFileDependencyGroupTokens(
        string input, 
        string tokens,
        [NotNullWhen(true)] out string? projectName,
        [NotNullWhen(true)] out string? projectVersion)
    {
        if (input.Contains(tokens))
        {
            var pieces = input.Split(tokens);
            projectName = pieces[0].Trim();
            projectVersion = pieces[1].Trim();
            return true;
        }

        projectName = null;
        projectVersion = null;
        return false;
    }

    private static VersionRange MinVersionOrFloat(string? versionValueRaw, bool includeMin)
    {
        //could be Floating or MinVersion
        if( NuGetVersion.TryParse(versionValueRaw, out var minVersion) )
            return new VersionRange(minVersion, includeMin);
        
        return versionValueRaw is null 
            ? throw new ArgumentException(versionValueRaw) 
            : VersionRange.Parse(versionValueRaw, true);
    }

    public class ProjectFileDependency(string? name, VersionRange versionRange)
    {
        public string? GetName()
        {
            return name;
        }

        public VersionRange GetVersionRange()
        {
            return versionRange;
        }
    }
}