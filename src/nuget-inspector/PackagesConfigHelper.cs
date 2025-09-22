using NuGet.Frameworks;
using NuGet.Versioning;

namespace NugetInspector;

/// <summary>
/// Resolve using packages.config strategy from:
/// See https://docs.microsoft.com/en-us/nuget/consume-packages/dependency-resolution#dependency-resolution-with-packagesconfig
/// See https://learn.microsoft.com/en-us/nuget/reference/packages-config
/// It means that only one package version can exist in the dependencies tree.
/// </summary>
public class PackagesConfigHelper(NugetApi nugetApi)
{
    private readonly Dictionary<string, ResolutionData> _ResolutionDatas = new();

    private List<VersionRange?> FindAllVersionRangesFor(string id)
    {
        id = id.ToLower();
        var result = new List<VersionRange?>();
        foreach (var pkg in _ResolutionDatas.Values)
        {
            foreach (var depPair in pkg.Dependencies)
            {
                if (depPair.Key == id)
                    result.Add(depPair.Value);
            }
        }

        return result;
    }

    public List<BasePackage> ProcessAll(List<Dependency> dependencies)
    {
        foreach (var dependency in dependencies)
        {
            Add(
                dependency.Name!,
                dependency.Name,
                dependency.VersionRange,
                dependency.Framework);
        }

        var builder = new PackageTree();
        foreach (var data in _ResolutionDatas.Values)
        {
            var deps = new List<BasePackage>();
            foreach (var dep in data.Dependencies.Keys)
            {
                if (!_ResolutionDatas.ContainsKey(dep))
                {
                    throw new Exception($"Unable to resolve dependencies: {dep}");
                }

                deps.Add(new BasePackage(
                    _ResolutionDatas[dep].Name!,
                    _ResolutionDatas[dep].CurrentVersion?.ToNormalizedString()));
            }

            builder.AddOrUpdatePackage(
                new BasePackage(data.Name!,
                    data.CurrentVersion?.ToNormalizedString()),
                    deps!);
        }

        return builder.GetPackageList();
    }

    public void Add(string id, string? name, VersionRange? range, NuGetFramework? framework)
    {
        id = id.ToLower();
        Resolve(
            id,
            name,
            framework,
            range);
    }

    private void Resolve(
        string id,
        string? name,
        NuGetFramework? projectTargetFramework = null,
        VersionRange? overrideRange = null)
    {
        id = id.ToLower();
        ResolutionData data = new();
        if (_ResolutionDatas.ContainsKey(id))
        {
            data = _ResolutionDatas[id];
            if (overrideRange != null)
            {
                if (data.ExternalVersionRange == null)
                    data.ExternalVersionRange = overrideRange;
                else
                    throw new Exception("Cannot set more than one external version range.");
            }
        }
        else
        {
            data.ExternalVersionRange = overrideRange;
            data.Name = name;
            _ResolutionDatas[id] = data;
        }

        var allVersions = FindAllVersionRangesFor(id);
        if (data.ExternalVersionRange != null) allVersions.Add(data.ExternalVersionRange);
        var combo = VersionRange.CommonSubSet(allVersions);
        var best = nugetApi.FindPackageVersion(id, combo);

        if (best == null)
        {
            if (Config.TRACE)
                Console.WriteLine( $"Unable to find package for '{id}' with versions range '{combo}'.");

            if (data.CurrentVersion == null)
                data.CurrentVersion = combo.MinVersion;

            return;
        }

        if (data.CurrentVersion == best.Identity.Version) return;

        data.CurrentVersion = best.Identity.Version;
        data.Dependencies.Clear();

        var packages = nugetApi.GetPackageDependenciesForPackage(best.Identity, projectTargetFramework);
        foreach (var dependency in packages)
        {
            if (data.Dependencies.ContainsKey(dependency.Id.ToLower()))
                continue;
            
            data.Dependencies.Add(dependency.Id.ToLower(), dependency.VersionRange);
            Resolve(
                dependency.Id.ToLower(),
                dependency.Id,
                projectTargetFramework);
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