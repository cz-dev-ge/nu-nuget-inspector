using System.Net;
using System.Net.Cache;
using Newtonsoft.Json.Linq;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
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

    private readonly Dictionary<string, JObject?> _CatalogEntryByCatalogUrl = new();
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

        if (_SourceRepositories.Count == 0 && seen.Count > 0)
            Console.Error.WriteLine(
                "WARNING: None of the configured NuGet sources could be reached. " +
                "Package metadata and dependency resolution will likely be incomplete.");
    }

    /// <summary>
    /// Add package_source (e.g., a NuGet repo API URL, aka. PackageSource) to the list of known NuGet APIs.
    /// Also keep track of SourceRepository in source_repositories.
    /// A source that cannot be reached or fails to authenticate (e.g. an unauthorized private
    /// feed, or a network error) is skipped with a warning rather than aborting the whole scan:
    /// other sources (such as the public nuget.org) may still resolve packages just fine.
    /// </summary>
    /// <param name="sourceRepo">package_source</param>
    private void AddSourceRepo(SourceRepository sourceRepo)
    {
        if (Config.TRACE)
            Console.WriteLine($"    AddSourceRepo: adding new {sourceRepo.PackageSource.SourceUri}");

        try
        {
            var packageMetadataEndpoint = sourceRepo.GetResource<PackageMetadataResource>();
            var dependencyInfoEndpoint = sourceRepo.GetResource<DependencyInfoResource>();

            _SourceRepositories.Add(sourceRepo);
            _MetadataResources.Add(packageMetadataEndpoint);
            _DependencyInfoResources.Add(dependencyInfoEndpoint);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(
                $"WARNING: Skipping NuGet source '{sourceRepo.PackageSource.Source}': " +
                $"unable to load NuGet API resources ({e.Message}). " +
                "Packages that are only available on this source may be missing from the results. " +
                "If this source requires authentication, check its credentials in nuget.config.");
            if (Config.TRACE && e.InnerException != null)
                Console.Error.WriteLine($"    {e.InnerException.Message}");
        }
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
                using var httpClient = new HttpClient();
                var response = httpClient.GetAsync(packageCatalogUrl).Result;
                response.EnsureSuccessStatusCode();
                var catalog = response.Content.ReadAsStringAsync().Result;
                catalogEntry = JObject.Parse(catalog);
                // note: this is caching across calls in a run 
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

