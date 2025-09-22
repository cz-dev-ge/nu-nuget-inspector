using System.Net;
using System.Net.Cache;
using Newtonsoft.Json.Linq;
using NuGet.Commands;
using NuGet.Configuration;
using NuGet.DependencyResolver;
using NuGet.Frameworks;
using NuGet.LibraryModel;
using NuGet.PackageManagement;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Resolver;
using NuGet.Versioning;

namespace NugetInspector;

/// <summary>
/// See https://learn.microsoft.com/en-us/nuget/api/overview
/// </summary>
public class NugetApi
{
    private readonly SourceCacheContext _SourceCacheContext = new(){
        NoCache=false,
        DirectDownload=false,
        MaxAge= new DateTimeOffset(DateTime.Now.AddDays(5))
    };
    private readonly GatherCache _GatherCache = new();

    private readonly Dictionary<string, JObject?> _CatalogEntryByCatalogUrl = new();
    private readonly Dictionary<string, List<PackageSearchMetadataRegistration>> _PsmrsByPackageName = new();
    private readonly Dictionary<PackageIdentity, PackageSearchMetadataRegistration?> _PsmrByIdentity = new();
    private readonly Dictionary<PackageIdentity, PackageDownload?> _DownloadByIdentity = new();
    private readonly Dictionary<PackageIdentity, SourcePackageDependencyInfo> _SpdiByIdentity = new();

    private readonly List<SourceRepository> _SourceRepositories = [];
    private readonly List<PackageMetadataResource> _MetadataResources = [];
    private readonly List<DependencyInfoResource> _DependencyInfoResources = [];

        private readonly ISettings _Settings;
    private readonly List<Lazy<INuGetResourceProvider>> _Providers = [];
    private readonly NuGetFramework _ProjectFramework;
    private readonly List<PackageSource> _PackageSources = [];
    private readonly NugetLogger _NugetLogger = new();

    public NugetApi(
        string nugetConfigPath,
        string projectRootPath,
        NuGetFramework projectFramework,
        bool withNugetOrg)
    {
        _ProjectFramework = projectFramework;
        _Providers.AddRange(Repository.Provider.GetCoreV3());

        _Settings = LoadNugetConfigSettings(
            nugetConfigPath,
            projectRootPath);

        PopulateResources(
            _Providers,
            _Settings,
            withNugetOrg);
    }

    /// <summary>
    /// Return PackageSearchMetadataRegistration querying the API
    /// </summary>
    /// <param name="name"></param>
    /// <param name="versionRange"></param>
    /// <returns>PackageSearchMetadataRegistration or null</returns>
    public PackageSearchMetadataRegistration? FindPackageVersion(string? name, VersionRange? versionRange)
    {
        if (Config.TRACE_NET)
            Console.WriteLine($"FindPackageVersion for {name} range: {versionRange}");

        if (name == null)
            return null;

        var packageVersions = FindPackageVersionsThroughCache(name);
        // TODO: we may need to error out if version is not known/existing upstream
        if (packageVersions.Count == 0)
            return null;
        var versions = packageVersions.Select(package => package.Identity.Version);
        var bestVersion = versionRange?.FindBestMatch(versions);
        return packageVersions.Find(package => package.Identity.Version == bestVersion);
    }

    /// <summary>
    /// Return a list of NuGet package metadata and cache it.
    /// </summary>
    /// <param name="name">id (e.g., the name)</param>
    private List<PackageSearchMetadataRegistration> FindPackageVersionsThroughCache(string name)
    {
        if (_PsmrsByPackageName.TryGetValue(name, out var psmrs))
        {
            if (Config.TRACE_NET)
                Console.WriteLine($"Metadata Cache hit for '{name}'");
            return psmrs;
        }

        if (Config.TRACE_NET)
            Console.WriteLine($"Metadata Cache miss for '{name}'");

        psmrs = FindPackagesOnline(name);
        // Update caches
        _PsmrsByPackageName[name] = psmrs;
        foreach (var psmr in psmrs)
            _PsmrByIdentity[psmr.Identity] = psmr;
        return psmrs;
    }

    /// <summary>
    /// Return a single NuGet package PackageSearchMetadataRegistration querying the API
    /// using a PackageIdentity, or null. Cache calls.
    /// </summary>
    /// <param name="pid">identity</param>
    /// <returns>PackageSearchMetadataRegistration or null</returns>
    public PackageSearchMetadataRegistration? FindPackageVersion(PackageIdentity pid)
    {
        if (Config.TRACE)
            Console.WriteLine($"      Fetching package metadata for: {pid}");

        if (_PsmrByIdentity.TryGetValue(pid, out var psmr))
        {
            if (Config.TRACE_META)
                Console.WriteLine($"  Metadata Cache hit for '{pid}'");
            return psmr;
        }

        var exceptions = new List<Exception>();

        foreach (var metadataResource in _MetadataResources)
        {
            try
            {
                psmr = (PackageSearchMetadataRegistration)metadataResource.GetMetadataAsync(
                    pid,
                    _SourceCacheContext,
                    _NugetLogger,
                    CancellationToken.None
                ).Result;

                if (psmr == null)
                    continue;
                
                if (Config.TRACE_META)
                    Console.WriteLine($"  Found metadata for '{pid}' from: {metadataResource}");
                _PsmrByIdentity[pid] = psmr;
                return psmr;
            }
            catch (Exception ex)
            {
                if (Config.TRACE)
                    Console.WriteLine($"    FAILED to Fetch metadata for '{pid}' with: {ex.StackTrace}");

                exceptions.Add(ex);
            }
        }

        var errorMessage = $"No package metadata found for {pid}.";
        foreach (var ex in exceptions)
            errorMessage += $"\n    {ex}";

        if (Config.TRACE)
            Console.WriteLine(errorMessage);

        // cache this null too
        _PsmrByIdentity[pid] = null;

        throw new Exception(errorMessage);
    }

    /// <summary>
    /// Find NuGet packages online using the configured NuGet APIs
    /// </summary>
    /// <param name="name"></param>
    /// <returns>List of PackageSearchMetadataRegistration</returns>
    private List<PackageSearchMetadataRegistration> FindPackagesOnline(string name)
    {
        if (Config.TRACE)
            Console.WriteLine($"Find package versions online for: {name}");

        var foundPsrms = new List<PackageSearchMetadataRegistration>();
        var exceptions = new List<Exception>();

        foreach (var metadataResource in _MetadataResources)
        {
            try
            {
                var psmrs =
                    (IEnumerable<PackageSearchMetadataRegistration>) metadataResource.GetMetadataAsync(
                        name,
                        true,
                        true,
                        _SourceCacheContext,
                        new NugetLogger(),
                        CancellationToken.None
                    ).Result;

                if (psmrs == null)
                    continue;
                
                var psmrs2 = psmrs.ToList();
                if (Config.TRACE)
                {
                    var mr = (PackageMetadataResourceV3)metadataResource;
                    Console.WriteLine($"    Fetched #{psmrs2.Count} metadata for '{name}' from: {mr.ToJson}");
                }

                foundPsrms.AddRange(psmrs2);

                if (Config.TRACE_NET)
                {
                    foreach (var psmr in psmrs2)
                        Console.WriteLine($"        Fetched: {psmr.PackageId}@{psmr.Version}");
                }
                break;
            }
            catch (Exception ex)
            {
                if (Config.TRACE)
                    Console.WriteLine($"        FAILED to Fetch metadata for '{name}' with: {ex.StackTrace}");

                exceptions.Add(ex);
            }
        }

        if (!Config.TRACE || exceptions.Count == 0)
            return foundPsrms;
        
        Console.WriteLine($"ERROR: No package found for {name}.");
        foreach (var ex in exceptions)
            Console.WriteLine($"    {ex}");
        
        return foundPsrms;
    }

    /// <summary>
    /// Return settings loaded from a specific nuget.config file if provided or from the
    /// nuget.config in the code tree, using the NuGet search procedure otherwise.
    /// </summary>
    public static ISettings LoadNugetConfigSettings(
        string nugetConfigPath,
        string projectRootPath)
    {
        ISettings settings;
        if (!string.IsNullOrWhiteSpace(nugetConfigPath))
        {
            if (!File.Exists(nugetConfigPath))
                throw new FileNotFoundException("Missing requested nuget.config", nugetConfigPath);

            if (Config.TRACE)
                Console.WriteLine($"Loading nuget.config: {nugetConfigPath}");

            var root = Directory.GetParent(nugetConfigPath)!.FullName;
            var nugetConfigFileName = Path.GetFileName(nugetConfigPath);
            settings = Settings.LoadSpecificSettings(root, nugetConfigFileName);
        }
        else
        {
            // Load defaults settings the NuGet way.
            // Note that we ignore machine-wide settings by design as they are not be relevant to
            // the resolution at hand.
            settings = Settings.LoadDefaultSettings(
                projectRootPath,
                null,
                null);
        }

        if (!Config.TRACE_DEEP)
            return settings;
        
        Console.WriteLine("\nLoadNugetConfigSettings");
        var sectionNames = new List<string> {
            "packageSources",
            "disabledPackageSources",
            "activePackageSource",
            "packageSourceMapping",
            "packageManagement"};

        if (!Config.TRACE_DEEP)
            return settings;
        
        foreach (var sn in sectionNames)
        {
            var section =  settings.GetSection(sn);
            if (section == null)
                continue;

            Console.WriteLine($"    section: {section.ElementName}");
            foreach (var item in section.Items)
                Console.WriteLine($"        item:{item}, ename: {item.ElementName} {item.ToJson()}");
        }
        return settings;
    }

    /// <summary>
    /// Populate the NuGet repositories "Resources" lists using PackageSource
    /// from a feed URL and a nuget.config file path.
    /// These are MetadataResourceList and DependencyInfoResourceList attributes.
    /// </summary>
    private void PopulateResources(
        List<Lazy<INuGetResourceProvider>> providers,
        ISettings settings,
        bool withNugetOrg = false)
    {
        PackageSourceProvider packageSourceProvider = new(settings);
        _PackageSources.AddRange(packageSourceProvider.LoadPackageSources());

        if (Config.TRACE)
            Console.WriteLine($"\nPopulateResources: Loaded {_PackageSources.Count} package sources from nuget.config");

        if (withNugetOrg || _PackageSources.Count == 0)
        {
            // Use nuget.org as last resort
            var nugetSource = new PackageSource(
                "https://api.nuget.org/v3/index.json",
                "nuget.org");
            _PackageSources.Add(nugetSource);
        }

        HashSet<string> seen = [];
        foreach (var packageSource in _PackageSources)
        {
            var sourceUrl = packageSource.SourceUri.ToString();
            if (seen.Contains(sourceUrl))
                continue;
            SourceRepository sourceRepository = new(packageSource, providers);
            AddSourceRepo(sourceRepository);
            seen.Add(sourceUrl);
        }
    }

    /// <summary>
    /// Add package_source (e.g., a NuGet repo API URL, aka. PackageSource) to the list of known NuGet APIs.
    /// Also keep track of SourceRepository in source_repositories.
    /// </summary>
    /// <param name="sourceRepo">package_source</param>
    private void AddSourceRepo(SourceRepository sourceRepo)
    {
        if (Config.TRACE)
            Console.WriteLine($"    AddSourceRepo: adding new {sourceRepo.PackageSource.SourceUri}");

        try
        {
            _SourceRepositories.Add(sourceRepo);

            var packageMetadataEndpoint = sourceRepo.GetResource<PackageMetadataResource>();
            _MetadataResources.Add(packageMetadataEndpoint);

            var dependencyInfoEndpoint = sourceRepo.GetResource<DependencyInfoResource>();
            _DependencyInfoResources.Add(dependencyInfoEndpoint);
        }
        catch (Exception e)
        {
            var message = $"Error loading NuGet API Resource from url: {sourceRepo.PackageSource.SourceUri}";
            if (!Config.TRACE)
                throw new Exception(message, e);
            
            Console.WriteLine($"    {message}");
            if (e.InnerException != null)
                Console.WriteLine(e.InnerException.Message);
            throw new Exception (message, e);
        }
    }

    /// <summary>
    /// Return a list of PackageDependency for a given package PackageIdentity and framework.
    /// </summary>
    public IEnumerable<PackageDependency> GetPackageDependenciesForPackage(PackageIdentity identity, NuGetFramework? framework)
    {
        if (framework == null)
            framework = _ProjectFramework;

        var spdi = GetResolvedSourcePackageDependencyInfo(identity, framework);
        return spdi == null 
            ? new List<PackageDependency>() 
            : spdi.Dependencies;
    }

    /// <summary>
    /// Return a SourcePackageDependencyInfo or null for a given package.
    /// </summary>
    public SourcePackageDependencyInfo? GetResolvedSourcePackageDependencyInfo(
        PackageIdentity identity,
        NuGetFramework? framework)
    {
        if (framework == null)
            framework = _ProjectFramework;

        if (_SpdiByIdentity.TryGetValue(identity, out var spdi))
        {
            return spdi;
        }

        if (Config.TRACE_META)
            Console.WriteLine($"  GetPackageInfo: {identity} framework: {framework}");

        foreach (var dir in _DependencyInfoResources)
        {
            try
            {
                Task<SourcePackageDependencyInfo>? infoTask = dir.ResolvePackage(
                    identity,
                    framework,
                    _SourceCacheContext,
                    _NugetLogger,
                    CancellationToken.None);

                spdi = infoTask.Result;

                if (Config.TRACE_META && spdi != null)
                    Console.WriteLine($"    Found download URL: {spdi.DownloadUri} hash: {spdi.PackageHash}");

                if (spdi == null)
                    continue;
                
                _SpdiByIdentity[identity] = spdi;
                return spdi;
            }
            catch (Exception e)
            {
                if (Config.TRACE)
                {
                    Console.WriteLine($"        Failed to collect SourcePackageDependencyInfo for package: {identity} from source: {dir}");
                    if (e.InnerException != null)
                        Console.WriteLine(e.InnerException.Message);
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Return the download URL of the package or null.
    /// This is based on the logic of private NuGet code.
    /// 1. Try the SourcePackageDependencyInfo.DownloadUri if present.
    /// 2. Try the registration metadata JObject, since there is no API
    /// 3. FUTURE: fall back to craft a URL by hand.
    /// </summary>
    public string? GetDownloadUrl(PackageIdentity identity)
    {
        if (!_SpdiByIdentity.TryGetValue(identity, out var spdi))
            return null;
        
        var du = spdi.DownloadUri;
        if (du != null && !string.IsNullOrWhiteSpace(du.ToString()))
            return du.ToString();

        var rrv3 = spdi.Source.GetResource<RegistrationResourceV3>(CancellationToken.None);
        if (rrv3 == null)
            return null;
        
        var meta = rrv3.GetPackageMetadata(
            identity,
            _SourceCacheContext,
            _NugetLogger,
            CancellationToken.None).Result;
        var content = meta?["packageContent"];
        if (content != null)
            return content.ToString();
        // TODO last resort: Try if we have package base address URL
        // var base = "TBD";
        // var name = identity.Id.ToLowerInvariant();
        // var version = identity.Version.ToNormalizedString().ToLowerInvariant();
        // return $"{base}/{name}/{version}/{name}.{version}.nupkg";

        return null;
    }

    /// <summary>
    /// Return a PackageDownload for a given package identity.
    /// Cache entries for a given package identity as needed
    /// and reuse previsouly cached entries.
    /// </summary>
    /// <param name="identity">a PackageIdentity</param>
    /// <param name="with_details">if true, all fetches the download size, and SHA512 hash. Very slow!!</param>
    public PackageDownload? GetPackageDownload(PackageIdentity identity, bool withDetails = false)
    {
        // Get download with download URL and checksum (not always there per https://github.com/NuGet/NuGetGallery/issues/9433)

        if (Config.TRACE_NET)
            Console.WriteLine($"    GetPackageDownload: {identity}, with_details: {withDetails} project_framework: {_ProjectFramework}");

        PackageDownload? download = null;
        // try the cache
        if (_DownloadByIdentity.TryGetValue(identity, out download))
        {
            if (Config.TRACE_NET)
                Console.WriteLine($"        Caching hit for package '{identity}'");
            if (download != null)
                return download;
        }
        else
        {
            // fetch from the API otherwise: this is the dependency info that contains these details
            if (Config.TRACE_NET)
                Console.WriteLine($"        Caching miss: Fetching SPDI for package '{identity}'");
            var spdi = GetResolvedSourcePackageDependencyInfo(
                identity,
                _ProjectFramework);
            if (Config.TRACE_DEEP)
                Console.WriteLine($"      Info available for package '{spdi}'");

            if (spdi != null)
            {
                download = PackageDownload.FromSpdi(spdi);
            }
        }

        if (download != null && string.IsNullOrWhiteSpace(download.DownloadUrl))
            download.DownloadUrl = GetDownloadUrl(identity) ?? "";
        
        _DownloadByIdentity[identity] = download;

        if (Config.TRACE_NET)
            Console.WriteLine($"       Found download: {download}'");

        if (!withDetails || (withDetails && download?.IsEnhanced() == true))
            return download;

        // We need to fetch the SHA512 (and the size)
        // Note: we fetch catalog-only data, such as the SHA512, but the "catalog" is not used by
        // the NuGet client and typically not available with other NuGet servers beyond nuget.org
        // For now we do this ugly cooking. We could use instead the NuGet.Protocol.Catalog experimental library
        // we should instead consider a HEAD request as explained at
        // https://github.com/NuGet/NuGetGallery/issues/9433#issuecomment-1472286080
        // which is going to be lighter weight! but will NOT work anywhere but on NuGet.org 
        if (Config.TRACE_NET)
            Console.WriteLine($"       Fetching registration for package '{identity}'");

        var registration = FindPackageVersion(identity);
        if (registration == null)
            return download;

        var packageCatalogUrl = registration.CatalogUri.ToString();
        if (string.IsNullOrWhiteSpace(packageCatalogUrl))
            return download;

        if (Config.TRACE_NET)
            Console.WriteLine($"       Fetching catalog for package_catalog_url: {packageCatalogUrl}");

        JObject? catalogEntry;
        if (_CatalogEntryByCatalogUrl.TryGetValue(packageCatalogUrl, out var value))
        {
            catalogEntry = value;
        }
        else
        {
            // note: this is caching across runs 
            try
            {
                RequestCachePolicy policy = new(RequestCacheLevel.Default);
                var request = WebRequest.Create(packageCatalogUrl);
                request.CachePolicy = policy;
                var response = (HttpWebResponse)request.GetResponse();
                var catalog = new StreamReader(response.GetResponseStream()).ReadToEnd();
                catalogEntry = JObject.Parse(catalog);
                // note: this is caching accross calls in a run 
                _CatalogEntryByCatalogUrl[packageCatalogUrl] = catalogEntry;
            }
            catch (Exception ex)
            {
                if (Config.TRACE_NET)
                    Console.WriteLine($"        failed to fetch metadata details for: {packageCatalogUrl}: {ex}");
                _CatalogEntryByCatalogUrl[packageCatalogUrl] = null;
                return download;
            }
        }

        if (catalogEntry == null)
            return download;
        
        var hash = catalogEntry["packageHash"]
            !.ToString();
        
        if (download != null)
        {
            download.Hash = Convert.ToHexString(Convert.FromBase64String(hash));
            download.HashAlgorithm = catalogEntry["packageHashAlgorithm"]!.ToString();
            download.Size = (int)catalogEntry["packageSize"]!;
        }
        
        if (Config.TRACE_NET)
            Console.WriteLine($"        download: {download}");
        return download;
    }

    /// <summary>
    /// Gather all possible dependencies given a list of primary target
    /// identities. Use the configured source_repositories for gathering.
    /// </summary>
    public ISet<SourcePackageDependencyInfo> GatherPotentialDependencies(
        List<PackageIdentity> directDependencies,
        NuGetFramework framework)
    {
        if (Config.TRACE)
        {
            Console.WriteLine("\nNugetApi.GatherPotentialDependencies:");
            Console.WriteLine("    direct_dependencies");
            foreach (var pid in directDependencies)
                Console.WriteLine($"        {pid} IsPrerelease: {pid.Version.IsPrerelease}");
        }
        var resolutionContext = new ResolutionContext(
            DependencyBehavior.Lowest,
            true,
            true,
            VersionConstraints.None,
            _GatherCache,
            _SourceCacheContext
        );

        var targetNames = new HashSet<string>(directDependencies.Select(p => p.Id)).ToList();
        var psm = PackageSourceMapping.GetPackageSourceMapping(_Settings);
        var context = new GatherContext(psm)
        {
            TargetFramework = framework,
            PrimarySources = _SourceRepositories,
            AllSources = _SourceRepositories,
            // required, but empty: no local source repo used here, so we mock it
            PackagesFolderSource = new SourceRepository(
                new PackageSource("installed"),
                new List<Lazy<INuGetResourceProvider>>()),

            PrimaryTargetIds = targetNames,
            PrimaryTargets = directDependencies.ToList(),

            // skip/ignore any InstalledPackages
            InstalledPackages = new List<PackageIdentity>(),
            AllowDowngrades = false,
            ResolutionContext = resolutionContext
        };
        HashSet<SourcePackageDependencyInfo> gatheredDependencies = ResolverGather.GatherAsync(
            context,
            CancellationToken.None
        ).Result;

        if (Config.TRACE)
        {
            Console.WriteLine($"    all gathered dependencies: {gatheredDependencies.Count}");
            if (Config.TRACE_DEEP)
            {
                foreach (var spdi in gatheredDependencies)
                    Console.WriteLine($"        {spdi.Id}@{spdi.Version}");
            }
        }
        foreach (var spdi in gatheredDependencies)
        {
            PackageIdentity identity = new(spdi.Id, spdi.Version);
            _SpdiByIdentity[identity] = spdi;
        }

        return gatheredDependencies;
    }

    /// <summary>
    /// Resolve the primary direct_references against all available_dependencies to an
    /// effective minimal set of dependencies
    /// </summary>
    public HashSet<SourcePackageDependencyInfo> ResolveDependenciesForPackageConfig(
        IEnumerable<PackageReference> targetReferences,
        IEnumerable<SourcePackageDependencyInfo> availableDependencies)
    {
        var directDeps = targetReferences.Select(p => p.PackageIdentity);
        IEnumerable<string> targetNames = new HashSet<string>(directDeps.Select(p => p.Id));

        PackageResolverContext context = new (
            DependencyBehavior.Lowest,
            targetNames,
            targetNames,
            targetReferences,
            directDeps,
            availableDependencies,
            _SourceRepositories.Select(s => s.PackageSource),
            _NugetLogger);

        var resolver = new PackageResolver();
        resolver.Resolve(context, CancellationToken.None);

        IEnumerable<PackageIdentity> resolvedDepIdentities = resolver.Resolve(
            context,
            CancellationToken.None);

        if (Config.TRACE)
        {
            Console.WriteLine("    actual dependencies");
            foreach (var pid in resolvedDepIdentities)
                Console.WriteLine($"        {pid.Id}@{pid.Version}");
        }

        HashSet<SourcePackageDependencyInfo> effectiveDependencies = [];

        var samePackages = PackageIdentityComparer.Default;
        foreach (var depId in resolvedDepIdentities)
        {
            foreach (var possibleDepId in availableDependencies)
            {
                if (samePackages.Equals(depId, possibleDepId))
                {
                    effectiveDependencies.Add(possibleDepId);
                    break;
                }
            }
        }
        return effectiveDependencies;
    }

    /// <summary>
    /// Resolve the primary direct_references against all available_dependencies to an
    /// effective minimal set of dependencies using the PackageReference approach.
    /// </summary>
    public HashSet<SourcePackageDependencyInfo> ResolveDependenciesForPackageReference(
        IEnumerable<PackageReference> targetReferences)
    {
        var psm = PackageSourceMapping.GetPackageSourceMapping(_Settings);
        var walkContext = new RemoteWalkContext(
            _SourceCacheContext,
            psm,
            _NugetLogger){
                IsMsBuildBased = true
            };
        var packages = new List<PackageId>();
        foreach (var targetref in targetReferences)
        {
            try
            {
                packages.Add(PackageId.FromReference(targetref));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   FAILED: targetref: {targetref}");
                throw new Exception(targetref.ToString(), ex);
            }
        }

        walkContext.ProjectLibraryProviders.Add(new ProjectLibraryProvider(packages));

        foreach (var sourceRepo in _SourceRepositories)
        {
            var provider = new SourceRepositoryDependencyProvider(
                sourceRepo,
                _NugetLogger,
                _SourceCacheContext,
                true,
                true);

            walkContext.RemoteLibraryProviders.Add(provider);
        }

        // We need a fake root lib as there is only one allowed input to walk
        // the dependencies: This represents the project
        var rootlib = new LibraryRange(
            "root_project",
            VersionRange.Parse("1.0.0"),
            LibraryDependencyTarget.Project);

        var walker = new RemoteDependencyWalker(walkContext);

        var results = walker.WalkAsync(
            rootlib,
            _ProjectFramework,
            // TODO: add eventual support for runtime id
            // https://learn.microsoft.com/en-us/dotnet/core/rid-catalog
            null,
            null, //RuntimeGraph.Empty,
            true);
        GraphNode<RemoteResolveResult> resolvedGraph = results.Result;
        CheckGraphForErrors(resolvedGraph);

        var rg = RestoreTargetGraph.Create(
            new List<GraphNode<RemoteResolveResult>>() { resolvedGraph },
            walkContext,
            _NugetLogger,
            _ProjectFramework
        );

        var resolvedPackageInfoByPackageId = new Dictionary<PackageId, ResolvedPackageInfo>();

        if (Config.TRACE)
            Console.WriteLine("      RestoreTargetGraph");

        if (rg.Flattened != null)
        {
            var flats = new List<GraphItem<RemoteResolveResult>>(rg.Flattened);
            flats.Sort((x,y) => x.Data.Match.Library.CompareTo(y.Data.Match.Library));

            foreach (var item in flats)
            {
                var lib = item.Key;
                if (lib.Type != "package")
                    continue;


                var name = lib.Name;
                var version = lib.Version.ToNormalizedString();
                var isPrerelease = lib.Version.IsPrerelease;

                var remoteMatch= item.Data.Match;

                var deps = item.Data.Dependencies;
                var pid = new PackageId(name, version,  isPrerelease);
                var rpi = new ResolvedPackageInfo() {
                    PackageId= pid,
                    RemoteMatch= remoteMatch
                };
                resolvedPackageInfoByPackageId[pid] = rpi;
                if (!Config.TRACE)
                    continue;
                
                Console.WriteLine($"      {lib}");
                foreach (var dep in deps)
                {
                    Console.WriteLine($"           {lib.Type}/{dep.Name}@{dep.LibraryRange} autoref: {dep.AutoReferenced}");
                }
            }
        }

        // we iterate only inner nodes, because we have only one outer node: the "fake" root project 
        // foreach (GraphNode<RemoteResolveResult> inner in resolved_graph.InnerNodes)
        // {
        //     if (Config.TRACE_DEEP)
        //         Console.WriteLine($"\n    Resolved direct dependency: {inner.Item.Key.Name}@{inner.Item.Key.Version}");

        //     FlattenGraph(inner, resolved_package_info_by_package_id);
        // }

        HashSet<SourcePackageDependencyInfo> flatDependencies = [];
        foreach (var item in resolvedPackageInfoByPackageId)
        {
            var dependency = item.Key;
            if (Config.TRACE_DEEP)
            {
                var dpi = item.Value;
                var sourceRepo = dpi.RemoteMatch?.Provider?.Source;
                Console.WriteLine($"          flat_dependency: {dependency.Name} {dependency.Version} repo: {sourceRepo?.SourceUri}");
            }

            var spdi = new SourcePackageDependencyInfo(
                dependency.Name,
                new NuGetVersion(dependency.Version),
                new List<PackageDependency>(),
                true,
                null
            );
            flatDependencies.Add(spdi);
        }
        return flatDependencies;
    }

    /// <summary>
    /// Flatten the graph and populate the result mapping recursively
    /// </summary>
    public static void FlattenGraph(
        GraphNode<RemoteResolveResult> node,
        Dictionary<PackageId, ResolvedPackageInfo> resolvedPackageInfoByPackageId)
    {
        if (node.Key.TypeConstraint != LibraryDependencyTarget.Package
            && node.Key.TypeConstraint != LibraryDependencyTarget.PackageProjectExternal)
            throw new ArgumentException($"Package {node.Key.Name} cannot be resolved from the sources");

        try
        {
            var item = node.Item;
            if (item == null)
            {
                var message = $"      FlattenGraph: node Item is null '{node}'";
                if (Config.TRACE)
                {
                    Console.WriteLine($"        {message}");
                }
                throw new Exception(message);
            }
            var key = item.Key;
            var name = key.Name;
            var version = key.Version.ToNormalizedString();
            var isPrerelease = key.Version.IsPrerelease;

            var remoteMatch = item.Data.Match;

            if (Config.TRACE_DEEP)
            {
                Console.WriteLine($"\n     FlattenGraph: node.Item {node.Item} type: {node.GetType()} LibraryId: {key} LibraryIdType: {key.Type}");
                Console.WriteLine($"          remote_match: {remoteMatch} path: {remoteMatch.Path} type: {remoteMatch.Library}");
                foreach (var idd in item.Data.Dependencies)
                {
                    Console.WriteLine($"             Dependency: {idd} AutoReferenced: {idd.AutoReferenced} ");
                }
            }

            var pid = new PackageId(
                name,
                version,
                isPrerelease);

            var resolvedPackageInfo = new ResolvedPackageInfo
            {
                PackageId = pid,
                RemoteMatch = item.Data.Match
            };

            if (Config.TRACE_DEEP)
                Console.WriteLine($"          FlattenGraph: {pid} Library: {item.Data.Match.Library}");

            resolvedPackageInfoByPackageId.TryAdd(resolvedPackageInfo.PackageId, resolvedPackageInfo);

            foreach (var nd in node.InnerNodes)
            {
                FlattenGraph(nd, resolvedPackageInfoByPackageId);
            }
        }
        catch (Exception ex)
        {
            var message = $"Failed to resolve graph with: {node}";
            if (Config.TRACE)
                Console.WriteLine($"        FlattenGraph: {message}: {ex}");
            throw new Exception(message, ex);
        }
    }

    /// <summary>
    /// Check the dependency for errors, raise exceptions if these are found
    /// </summary>
    public static void CheckGraphForErrors(GraphNode<RemoteResolveResult> resolvedGraph)
    {
        var analysis = resolvedGraph.Analyze();
        const bool allowDowngrades = false;
        if (analysis.Downgrades.Count != 0)
        {
            if (Config.TRACE)
            {
                foreach (var item in analysis.Downgrades)
                    Console.WriteLine($"Downgrade from {item.DowngradedFrom.Key} to {item.DowngradedTo.Key}");
            }

            if (!allowDowngrades)
            {
                var name = analysis.Downgrades[0].DowngradedFrom.Item.Key.Name;
                throw new InvalidOperationException($"Downgrade not allowed: {name}");
            }
        }

        if (analysis.Cycles.Count != 0)
        {
            if (Config.TRACE)
            {
                foreach (var item in analysis.Cycles)
                    Console.WriteLine($"Cycle in dependencies: {item.Item.Key.Name},{item.Item.Key.Version.ToNormalizedString()}");
            }

            var name = analysis.Cycles[0].Key.Name;
            throw new InvalidOperationException($"One package has dependency cycle: {name}");
        }

        if (analysis.VersionConflicts.Count == 0)
            return;
        
        if (Config.TRACE)
        {
            foreach (var itm in analysis.VersionConflicts)
            {
                Console.WriteLine(
                    $"Conflict for {itm.Conflicting.Key.Name},{itm.Conflicting.Key.VersionRange?.ToNormalizedString()} resolved as "
                    + $"{itm.Selected.Item.Key.Name},{itm.Selected.Item.Key.Version.ToNormalizedString()}");
            }
        }
        var item2 = analysis.VersionConflicts[0];
        var requested = $"{item2.Conflicting.Key.Name},{item2.Conflicting.Key.VersionRange?.ToNormalizedString()}";
        var selected = $"{item2.Selected.Item.Key.Name},{item2.Selected.Item.Key.Version.ToNormalizedString()}";
        throw new InvalidOperationException($"One package has version conflict: requested: {requested}, selected: {selected}");
    }

    /// <summary>
    /// Return Nuspec data fetched and extracted from a .nupkg
    /// </summary>
    public NuspecReader? GetNuspecDetails(
        PackageIdentity identity,
        string downloadUrl,
        SourceRepository sourceRepo
        )
    {
        try
        {
            var httpSource = HttpSource.Create(sourceRepo);
            var downloader = new FindPackagesByIdNupkgDownloader(httpSource);
            var reader = downloader.GetNuspecReaderFromNupkgAsync(
                identity,
                downloadUrl,
                _SourceCacheContext,
                _NugetLogger,
                CancellationToken.None).Result;

            var copyright = reader.GetCopyright();
            if (Config.TRACE)
                Console.WriteLine($"    Nuspec copyright: {copyright}");

            var repoMetaData = reader.GetRepositoryMetadata();
            if (Config.TRACE)
            {
                Console.WriteLine($"    Nuspec repo.type: {repoMetaData.Type}");
                Console.WriteLine($"    Nuspec repo.url: {repoMetaData.Url}");
                Console.WriteLine($"    Nuspec repo.branch: {repoMetaData.Branch}");
                Console.WriteLine($"    Nuspec repo.commit: {repoMetaData.Commit}");
            }
            if (repoMetaData.Type == "git" && repoMetaData.Url.StartsWith("https://github.com"))
            {
                //<repository type="git" url="https://github.com/JamesNK/Newtonsoft.Json" commit="0a2e291c0d9c0c7675d445703e51750363a549ef"/>
            }
            return reader;
        }
        catch (Exception ex)
        {
            if (Config.TRACE)
                Console.WriteLine($"    Failed to fetch Nuspec: {ex}");
        }
        return null;
    }
}

public class ResolvedPackageInfo
{
    public PackageId? PackageId;

    /// <summary>
    /// The NuGet package resolution match.
    /// </summary>
    public RemoteMatch? RemoteMatch;
}

/// <summary>
/// A dependency provider that collects only the local package references
/// </summary>
internal class ProjectLibraryProvider : IDependencyProvider
{
    private readonly ICollection<PackageId> _PackageIds;

    public ProjectLibraryProvider(ICollection<PackageId> packageIds)
    {
        _PackageIds = packageIds;
    }

    public bool SupportsType(LibraryDependencyTarget libraryTypeFlag)
    {
        return libraryTypeFlag == LibraryDependencyTarget.Project;
    }

    public Library GetLibrary(LibraryRange libraryRange, NuGetFramework framework)
    {
        var dependencies = new List<LibraryDependency>();

        foreach (var package in _PackageIds)
        {
            var lib = new LibraryDependency
            {
                LibraryRange =
                    new LibraryRange(
                        package.Name,
                        VersionRange.Parse(package.Version),
                        LibraryDependencyTarget.Package)
            };

            dependencies.Add(lib);
        }

        var rootProject = new LibraryIdentity(
            libraryRange.Name,
            NuGetVersion.Parse("1.0.0"),
            LibraryType.Project);

        return new Library
        {
            LibraryRange = libraryRange,
            Identity = rootProject,
            Dependencies = dependencies,
            Resolved = true
        };
    }
}

/// <summary>
/// A package with name and version or version range
/// </summary>
public class PackageId
{
    public string Name { get; }

    /// <summary>
    /// Version or version range as a string
    /// </summary>
    public string Version { get; }

    public bool AllowPrereleaseVersions { get; }

    public PackageId(string id, string version, bool allowPrereleaseVersions = false)
    {
        Name = id;
        Version = version;
        AllowPrereleaseVersions = allowPrereleaseVersions;
    }

    public override string ToString()
    {
        return $"{Name}@{Version}";
    }

    public static PackageId FromReference(PackageReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        
        var av = reference.AllowedVersions;
        var allowPrerel = false;
        string? version = null;
        if (av != null)
        {
            var mv  = reference.AllowedVersions.MinVersion;
            if (mv != null)
                allowPrerel = mv.IsPrerelease;
            version = reference.AllowedVersions.ToNormalizedString();
        }
        if (version == null && reference.PackageIdentity.Version != null)
        {
            version = reference.PackageIdentity.Version.ToString();
        }

        return new PackageId(
            reference.PackageIdentity.Id,
            version ?? "",
            allowPrerel
        );
    }
}