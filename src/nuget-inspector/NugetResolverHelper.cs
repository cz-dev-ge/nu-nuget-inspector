using System.Diagnostics.CodeAnalysis;
using NuGet.Packaging.Core;
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
        _NugetApi = nugetApi;
    }

    public List<BasePackage> GetPackageList()
    {
        return _PackageTree.GetPackageList();
    }

    public void ResolveManyOneByOne(List<Dependency> dependencies)
    {
        foreach (var dep in dependencies)
        {
            Log.Trace($"NugetApiHelper.ResolveManyOneByOne: {dep}");
            ResolveOne(dep);
        }
    }

    /// <summary>
    /// Resolve a Dependency and add it to the PackageTree.
    /// </summary>
    public void ResolveOne(Dependency dependency)
    {
        Log.Trace($"\nNugetApiHelper.ResolveOne: name: {dependency.Name} range: {dependency.VersionRange}");

        if (string.IsNullOrWhiteSpace(dependency.Name))
            throw new ArgumentNullException($"Dependency: {dependency} name cannot be null");

        var packageMetadata = _NugetApi.FindPackageVersion(
            dependency.Name,
            dependency.VersionRange);

        if (packageMetadata == null)
        {
            var version = dependency.VersionRange?.MinVersion?.ToNormalizedString();
            Log.Trace($"    Failed to find package: '{dependency.Name}' " 
                      + $"range: '{dependency.VersionRange}', picking instead min version: '{version}'");

            if (dependency.Name != null)
                _PackageTree.AddOrUpdatePackage(id: new BasePackage(name: dependency.Name, type: dependency.Type, version: version));
            return;
        }

        var basePackage = new BasePackage(
            dependency.Name!,
            dependency.Type,
            packageMetadata.Identity.Version.ToNormalizedString());

        var packages = _NugetApi.GetPackageDependenciesForPackage(
            packageMetadata.Identity,
            dependency.Framework);

        var dependencies = new List<BasePackage>();
        foreach (var package in packages)
        {
            if (_PackageTree.GetResolvedVersion(package.Id, package.VersionRange) is not {} resolvedVersion)
            {
                if (!TryFindDependency(dependency, package, dependencies, out var transitiveDependency))
                    continue;

                ResolveOne(transitiveDependency);
                Log.Trace($"        ResolveOne: {package.Id} range: {package.VersionRange}");
            }
            else
            {
                var basePkg = new BasePackage(package.Id, ComponentType.NuGet, resolvedVersion);
                dependencies.Add(basePkg);
                Log.Trace($"        dependencies.Add name: {package.Id}, version: {resolvedVersion}");
            }
        }

        _PackageTree.AddOrUpdatePackage(basePackage, dependencies!);
    }

    private bool TryFindDependency(
        Dependency dependency, 
        PackageDependency pkg, 
        List<BasePackage> dependencies, 
        [NotNullWhen(true)] out Dependency? transitiveDependency)
    {
        var packageMetaData = _NugetApi.FindPackageVersion(
            pkg.Id,
            pkg.VersionRange);
        
        if (packageMetaData == null)
        {
            Log.Trace($"        Unable to find package for '{pkg.Id}' version '{pkg.VersionRange}'");
            transitiveDependency = null;
            return false;
        }

        var dependentPackage = new BasePackage(
            packageMetaData.Identity.Id,
            ComponentType.NuGet,
            packageMetaData.Identity.Version.ToNormalizedString());

        dependencies.Add(dependentPackage);

        if (_PackageTree.DoesPackageExist(dependentPackage))
        {
            transitiveDependency = null;
            return false;
        }
                
        transitiveDependency = new(
            pkg.Id,
            ComponentType.NuGet,
            pkg.VersionRange,
            dependency.Framework);
        
        return true;
    }
}