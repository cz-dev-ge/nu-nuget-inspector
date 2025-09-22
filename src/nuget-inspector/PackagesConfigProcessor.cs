using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Versioning;

namespace NugetInspector;

/// <summary>
/// Handles legacy packages.config format originally designed for NuGet projects
/// See https://learn.microsoft.com/en-us/nuget/reference/packages-config
/// https://docs.microsoft.com/en-us/nuget/consume-packages/dependency-resolution#dependency-resolution-with-packagesconfig
/// and https://learn.microsoft.com/en-us/nuget/consume-packages/migrate-packages-config-to-package-reference
/// </summary>
internal class PackagesConfigProcessor : IDependencyProcessor
{
    public const string DatasourceId = "nuget-packages.config";
    private readonly NugetApi _NugetApi;

    private readonly string _PackagesConfigPath;

    private readonly NuGetFramework _ProjectTargetFramework;

    public PackagesConfigProcessor(
        string packagesConfigPath,
        NugetApi nugetApi,
        NuGetFramework projectFramework)
    {
        _PackagesConfigPath = packagesConfigPath;
        _NugetApi = nugetApi;
        _ProjectTargetFramework = projectFramework;
    }

    /// <summary>
    /// Resolve dependencies for a packages.config file.
    /// A packages.config is a lockfile that contains all the dependencies.
    /// </summary>
    public DependencyResolution Resolve()
    {
        DependencyResolution resolution = new()
        {
            Dependencies = []
        };
        var dependencies = GetDependencies();
        var packages = CreateBasePackage(dependencies: dependencies);
        foreach (var package in packages)
        {
            var hasPackageReferences = packages.Any(pkg => pkg.Dependencies.Contains(item: package));
            if (!hasPackageReferences && package != null)
                resolution.Dependencies.Add(item: package);
        }
        return resolution;
    }

    /// <summary>
    /// Return a list of Dependency found in a packages.config file.
    /// Skip packages with a TargetFramework that is not compatible with
    /// the requested Project TargetFramework.
    /// </summary>
    private List<Dependency> GetDependencies()
    {
        Stream stream = new FileStream(
            path: _PackagesConfigPath,
            mode: FileMode.Open,
            access: FileAccess.Read);

        PackagesConfigReader reader = new(stream: stream);
        List<PackageReference> packages = reader.GetPackages(allowDuplicatePackageIds: true).ToList();

        var compat = DefaultCompatibilityProvider.Instance;
        var projectFramework = _ProjectTargetFramework;

        var dependencies = new List<Dependency>();

        if (Config.TRACE)
            Console.WriteLine("PackagesConfigHandler.GetDependencies");

        foreach (var package in packages)
        {
            var name = package.PackageIdentity.Id;
            var version = package.PackageIdentity.Version;
            var packageFramework = package.TargetFramework;

            if  (packageFramework?.IsUnsupported != false)
                packageFramework = NuGetFramework.AnyFramework;

            if (Config.TRACE)
                Console.WriteLine($"    for: {name}@{version}  project_framework: {projectFramework} package_framework: {packageFramework}");

            if  (projectFramework?.IsUnsupported == false
                && !compat.IsCompatible(framework: projectFramework, other: packageFramework))
            {
                if (Config.TRACE)
                    Console.WriteLine("    incompatible frameworks");
                continue;
            }
            var range = new VersionRange(
                minVersion: version,
                includeMinVersion: true,
                maxVersion: version,
                includeMaxVersion: true
            );

            Dependency dep = new(
                name: name,
                versionRange: range,
                framework: packageFramework,
                isDirect: true,
                isDevelopmentDependency: package.IsDevelopmentDependency);
            dependencies.Add(item: dep);
        }

        return dependencies;
    }

    private List<BasePackage> CreateBasePackage(List<Dependency> dependencies)
    {
        try
        {
            var resolverHelper = new PackagesConfigHelper(nugetApi: _NugetApi);
            var packages = resolverHelper.ProcessAll(dependencies: dependencies);
            return packages;
        }
        catch (Exception listex)
        {
            if (Config.TRACE)
                Console.WriteLine($"PackagesConfigHandler.CreateBasePackage: Failed processing packages.config as list: {listex.Message}");
            try
            {
                var resolver = new NugetResolverHelper(nugetApi: _NugetApi);
                resolver.ResolveManyOneByOne(dependencies: dependencies);
                return resolver.GetPackageList();
            }
            catch (Exception treeex)
            {
                if (Config.TRACE)
                    Console.WriteLine($"PackagesConfigHandler.CreateBasePackage: Failed processing packages.config as a tree: {treeex.Message}");
                var packages =
                    new List<BasePackage>(
                        collection: dependencies.Select(selector: dependency => dependency.CreateEmptyBasePackage()));
                return packages;
            }
        }
    }
}