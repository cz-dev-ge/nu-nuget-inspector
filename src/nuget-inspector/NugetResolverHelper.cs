using NuGet.Protocol;

namespace NugetInspector;

/// <summary>
/// A helper class to resolve NuGet dependencies as a tree, once at a time.
/// </summary>
public class NugetResolverHelper
{
    private readonly PackageTree _PackageTree = new();
    private readonly NugetApi _NugetApi;

    public NugetResolverHelper(NugetApi nugetApi)
    {
        this._NugetApi = nugetApi;
    }

    public List<BasePackage> GetPackageList()
    {
        return _PackageTree.GetPackageList();
    }

    public void ResolveManyOneByOne(List<Dependency> dependencies)
    {
        foreach (var dep in dependencies)
        {
            if (Config.TRACE)
                Console.WriteLine($"NugetApiHelper.ResolveManyOneByOne: {dep}");
            ResolveOne(dependency: dep);
        }
    }

    /// <summary>
    /// Resolve a Dependency and add it to the PackageTree.
    /// </summary>
    public void ResolveOne(Dependency dependency)
    {
        if (Config.TRACE)
            Console.WriteLine($"\nNugetApiHelper.ResolveOne: name: {dependency.Name} range: {dependency.VersionRange}");

        if (string.IsNullOrWhiteSpace(dependency.Name))
            throw new ArgumentNullException($"Dependency: {dependency} name cannot be null");

        var psmr = _NugetApi.FindPackageVersion(
            name: dependency.Name,
            versionRange: dependency.VersionRange);

        if (psmr == null)
        {
            var version = dependency.VersionRange?.MinVersion.ToNormalizedString();
            if (Config.TRACE)
            {
                Console.WriteLine(
                    $"    Failed to find package: '{dependency.Name}' "
                    + $"range: '{dependency.VersionRange}', picking instead min version: '{version}'");
            }

            if (dependency.Name != null)
                _PackageTree.AddOrUpdatePackage(id: new BasePackage(name: dependency.Name, version: version));
            return;
        }

        var basePackage = new BasePackage(
            name: dependency.Name!,
            version: psmr.Identity.Version.ToNormalizedString());

        var packages = _NugetApi.GetPackageDependenciesForPackage(
            identity: psmr.Identity,
            framework: dependency.Framework);

        var dependencies = new List<BasePackage>();
        foreach (var pkg in packages)
        {
            var resolvedVersion = _PackageTree.GetResolvedVersion(name: pkg.Id, range: pkg.VersionRange);
            if (resolvedVersion != null)
            {
                var basePkg = new BasePackage(name: pkg.Id, version: resolvedVersion);
                dependencies.Add(item: basePkg);
                if (Config.TRACE)
                    Console.WriteLine($"        dependencies.Add name: {pkg.Id}, version: {resolvedVersion}");
            }
            else
            {
                var psrm = _NugetApi.FindPackageVersion(
                    name: pkg.Id,
                    versionRange: pkg.VersionRange);
                if (psrm == null)
                {
                    if (Config.TRACE)
                        Console.WriteLine($"        Unable to find package for '{pkg.Id}' version '{pkg.VersionRange}'");
                    continue;
                }

                var dependentPackage = new BasePackage(
                    name: psrm.Identity.Id,
                    version: psrm.Identity.Version.ToNormalizedString());

                dependencies.Add(item: dependentPackage);

                if (_PackageTree.DoesPackageExist(package: dependentPackage))
                    continue;
                
                Dependency pd = new(
                    name: pkg.Id,
                    versionRange: pkg.VersionRange,
                    framework: dependency.Framework);

                ResolveOne(dependency: pd);
                if (Config.TRACE)
                    Console.WriteLine($"        ResolveOne: {pkg.Id} range: {pkg.VersionRange}");
            }
        }

        _PackageTree.AddOrUpdatePackage(basePackage: basePackage, dependencies: dependencies!);
    }
}