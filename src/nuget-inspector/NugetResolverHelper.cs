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
            ResolveOne(dep);
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
            dependency.Name,
            dependency.VersionRange);

        if (psmr == null)
        {
            var version = dependency.VersionRange?.MinVersion?.ToNormalizedString();
            if (Config.TRACE)
            {
                Console.WriteLine(
                    $"    Failed to find package: '{dependency.Name}' "
                    + $"range: '{dependency.VersionRange}', picking instead min version: '{version}'");
            }

            if (dependency.Name != null)
                _PackageTree.AddOrUpdatePackage(id: new BasePackage(name: dependency.Name, type: dependency.Type, version: version));
            return;
        }

        var basePackage = new BasePackage(
            dependency.Name!,
            dependency.Type,
            psmr.Identity.Version.ToNormalizedString());

        var packages = _NugetApi.GetPackageDependenciesForPackage(
            psmr.Identity,
            dependency.Framework);

        var dependencies = new List<BasePackage>();
        foreach (var pkg in packages)
        {
            var resolvedVersion = _PackageTree.GetResolvedVersion(pkg.Id, pkg.VersionRange);
            if (resolvedVersion != null)
            {
                var basePkg = new BasePackage(pkg.Id, ComponentType.NuGet, resolvedVersion);
                dependencies.Add(basePkg);
                if (Config.TRACE)
                    Console.WriteLine($"        dependencies.Add name: {pkg.Id}, version: {resolvedVersion}");
            }
            else
            {
                var psrm = _NugetApi.FindPackageVersion(
                    pkg.Id,
                    pkg.VersionRange);
                if (psrm == null)
                {
                    if (Config.TRACE)
                        Console.WriteLine($"        Unable to find package for '{pkg.Id}' version '{pkg.VersionRange}'");
                    continue;
                }

                var dependentPackage = new BasePackage(
                    psrm.Identity.Id,
                    ComponentType.NuGet,
                    psrm.Identity.Version.ToNormalizedString());

                dependencies.Add(dependentPackage);

                if (_PackageTree.DoesPackageExist(dependentPackage))
                    continue;
                
                Dependency pd = new(
                    pkg.Id,
                    ComponentType.NuGet,
                    pkg.VersionRange,
                    dependency.Framework);

                ResolveOne(pd);
                if (Config.TRACE)
                    Console.WriteLine($"        ResolveOne: {pkg.Id} range: {pkg.VersionRange}");
            }
        }

        _PackageTree.AddOrUpdatePackage(basePackage, dependencies!);
    }
}