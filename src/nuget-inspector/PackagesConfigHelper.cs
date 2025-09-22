using NuGet.Frameworks;
using NuGet.Versioning;

namespace NugetInspector;

/// <summary>
/// Resolve using packages.config strategy from:
/// See https://docs.microsoft.com/en-us/nuget/consume-packages/dependency-resolution#dependency-resolution-with-packagesconfig
/// See https://learn.microsoft.com/en-us/nuget/reference/packages-config
/// It means that only one package version can exist in the dependencies tree.
/// </summary>
public class PackagesConfigHelper
{
    private readonly NugetApi _NugetApi;
    private readonly Dictionary<string, ResolutionData> _ResolutionDatas = new();

    public PackagesConfigHelper(NugetApi nugetApi)
    {
        _NugetApi = nugetApi;
    }

    private List<VersionRange?> FindAllVersionRangesFor(string id)
    {
        id = id.ToLower();
        var result = new List<VersionRange?>();
        foreach (var pkg in _ResolutionDatas.Values)
        {
            foreach (var depPair in pkg.Dependencies)
            {
                if (depPair.Key == id)
                    result.Add(item: depPair.Value);
            }
        }

        return result;
    }

    public List<BasePackage> ProcessAll(List<Dependency> dependencies)
    {
        foreach (var dependency in dependencies)
        {
            Add(
                id: dependency.Name!,
                name: dependency.Name,
                range: dependency.VersionRange,
                framework: dependency.Framework);
        }

        var builder = new PackageTree();
        foreach (var data in _ResolutionDatas.Values)
        {
            var deps = new List<BasePackage>();
            foreach (var dep in data.Dependencies.Keys)
            {
                if (!_ResolutionDatas.ContainsKey(key: dep))
                {
                    throw new Exception($"Unable to resolve dependencies: {dep}");
                }

                deps.Add(item: new BasePackage(
                    name: _ResolutionDatas[key: dep].Name!,
                    version: _ResolutionDatas[key: dep].CurrentVersion?.ToNormalizedString()));
            }

            builder.AddOrUpdatePackage(
                basePackage: new BasePackage(name: data.Name!,
                    version: data.CurrentVersion?.ToNormalizedString()),
                    dependencies: deps!);
        }

        return builder.GetPackageList();
    }

    public void Add(string id, string? name, VersionRange? range, NuGetFramework? framework)
    {
        id = id.ToLower();
        Resolve(
            id: id,
            name: name,
            projectTargetFramework: framework,
            overrideRange: range);
    }

    private void Resolve(
        string id,
        string? name,
        NuGetFramework? projectTargetFramework = null,
        VersionRange? overrideRange = null)
    {
        id = id.ToLower();
        ResolutionData data = new();
        if (_ResolutionDatas.ContainsKey(key: id))
        {
            data = _ResolutionDatas[key: id];
            if (overrideRange != null)
            {
                if (data.ExternalVersionRange == null)
                    data.ExternalVersionRange = overrideRange;
                else
                    throw new Exception(message: "Cannot set more than one external version range.");
            }
        }
        else
        {
            data.ExternalVersionRange = overrideRange;
            data.Name = name;
            _ResolutionDatas[key: id] = data;
        }

        var allVersions = FindAllVersionRangesFor(id: id);
        if (data.ExternalVersionRange != null) allVersions.Add(item: data.ExternalVersionRange);
        var combo = VersionRange.CommonSubSet(ranges: allVersions);
        var best = _NugetApi.FindPackageVersion(name: id, versionRange: combo);

        if (best == null)
        {
            if (Config.TRACE)
                Console.WriteLine( value: $"Unable to find package for '{id}' with versions range '{combo}'.");

            if (data.CurrentVersion == null)
                data.CurrentVersion = combo.MinVersion;

            return;
        }

        if (data.CurrentVersion == best.Identity.Version) return;

        data.CurrentVersion = best.Identity.Version;
        data.Dependencies.Clear();

        var packages = _NugetApi.GetPackageDependenciesForPackage(identity: best.Identity, framework: projectTargetFramework);
        foreach (var dependency in packages)
        {
            if (!data.Dependencies.ContainsKey(key: dependency.Id.ToLower()))
            {
                data.Dependencies.Add(key: dependency.Id.ToLower(), value: dependency.VersionRange);
                Resolve(
                    id: dependency.Id.ToLower(),
                    name: dependency.Id,
                    projectTargetFramework: projectTargetFramework);
            }
        }
    }

    private class ResolutionData
    {
        public NuGetVersion? CurrentVersion;
        public readonly Dictionary<string, VersionRange?> Dependencies = new();
        public VersionRange? ExternalVersionRange;
        public string? Name;
    }
}