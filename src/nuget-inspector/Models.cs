using Newtonsoft.Json;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace NugetInspector
{
    #pragma warning disable IDE1006
    public class Dependency
    {
        public readonly string? Name;
        public readonly NuGetFramework? Framework;
        public readonly VersionRange? VersionRange;
        public bool IsDirect;
        public string Type;

        //True only for legacy packages.config-based projects only when set there
        public bool IsDevelopmentDependency;

        public Dependency(
            string name,
            string type,
            VersionRange? versionRange,
            NuGetFramework? framework = null,
            bool isDirect = false,
            bool isDevelopmentDependency = false)
        {
            Framework = framework;
            Name = name;
            Type = type;
            VersionRange = versionRange;
            IsDirect = isDirect;
            IsDevelopmentDependency = isDevelopmentDependency;
        }
        /// <summary>
        /// Return a new empty BasePackageWithDeps using this package.
        /// </summary>
        /// <returns></returns>
        public BasePackage CreateEmptyBasePackage()
        {
            return new BasePackage(
                Name!,
                Type,
                VersionRange?.MinVersion?.ToNormalizedString(),
                Framework?.ToString()
            );
        }
    }

    public class PackageTree
    {
        private readonly Dictionary<BasePackage, BasePackage> _BasePackageDepsByBasePackage = new();
        private readonly Dictionary<BasePackage, VersionPair> _VersionsPairByBasePackage = new();

        public bool DoesPackageExist(BasePackage package)
        {
            return _BasePackageDepsByBasePackage.ContainsKey(package);
        }

        public BasePackage GetOrCreateBasePackage(BasePackage package)
        {
            if (_BasePackageDepsByBasePackage.TryGetValue(package, out var packageWithDeps))
            {
                return packageWithDeps;
            }

            packageWithDeps = BasePackage.FromPackage(package, []);
            _BasePackageDepsByBasePackage[package] = packageWithDeps;

            if ( package.Version!= null &&  NuGetVersion.TryParse(package.Version, out var version))
                _VersionsPairByBasePackage[package] = new VersionPair(package.Version, version);

            return packageWithDeps;
        }

        /// <summary>
        /// Add BasePackage to the packageSets
        /// </summary>
        /// <param name="id"></param>
        public void AddOrUpdatePackage(BasePackage id)
        {
            GetOrCreateBasePackage(id);
        }

        /// <summary>
        /// Add BasePackage to the packageSets, and dependency as a dependency.
        /// </summary>
        /// <param name="id"></param>
        /// <param name="dependency"></param>
        public void AddOrUpdatePackage(BasePackage id, BasePackage dependency)
        {
            var packageSet = GetOrCreateBasePackage(id);
            packageSet.Dependencies.Add(dependency);
        }

        /// <summary>
        /// Add BasePackage base_package to the packageSets, and dependencies to dependencies.
        /// </summary>
        /// <param name="basePackage"></param>
        /// <param name="dependencies"></param>
        public void AddOrUpdatePackage(BasePackage basePackage, List<BasePackage?> dependencies)
        {
            var packageWithDeps = GetOrCreateBasePackage(basePackage);
            
            foreach (var dep in dependencies.OfType<BasePackage>())
                packageWithDeps.Dependencies.Add(dep);
            
            packageWithDeps.Dependencies = packageWithDeps.Dependencies.Distinct().ToList();
        }

        public List<BasePackage> GetPackageList()
        {
            return _BasePackageDepsByBasePackage.Values.ToList();
        }

        public string? GetResolvedVersion(string name, VersionRange range)
        {
            var allVersions = _VersionsPairByBasePackage.Keys.Where(key => key.Name == name)
                .Select(key => _VersionsPairByBasePackage[key]);
            var best = range.FindBestMatch(allVersions.Select(ver => ver.Version));
            foreach (var pair in _VersionsPairByBasePackage)
            {
                if (pair.Key.Name == name && pair.Value.Version == best)
                    return pair.Key.Version;
            }

            return null;
        }

        private class VersionPair
        {
            public string RawVersion;
            public readonly NuGetVersion Version;

            public VersionPair(string rawVersion, NuGetVersion version)
            {
                RawVersion = rawVersion;
                Version = version;
            }
        }
    }
    
    public static class ComponentType
    {
        public const string NuGet = "nuget";
        public const string Project = "project";
    }

    /// <summary>
    /// Package data object using purl as identifying attributes as
    /// specified here https://github.com/package-url/purl-spec
    /// This model is essentially derived from ScanCode Toolkit Package/PackageData.
    /// This is used to represent the top-level project.
    /// </summary>
    public class BasePackage : IEquatable<BasePackage>, IComparable<BasePackage>
    {
        public string Type { get; set; } = "nuget";
        public string Namespace { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Version { get; set; } = "";
        public string Qualifiers { get; set; } = "";
        public string Subpath { get; set; } = "";
        public string Purl { get; set; } = "";
        public string PrimaryLanguage { get; set; } = "C#";
        public string Description { get; set; } = "";
        public string ReleaseDate { get; set; } = "";
        public List<Party> Parties { get; set; } = [];
        public List<string> Keywords { get; set; } = [];
        public string HomepageUrl { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public int Size { get; set; }
        public string Sha1 { get; set; } = "";
        public string Md5 { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public string Sha512 { get; set; } = "";
        public string BugTrackingUrl { get; set; } = "";
        public string CodeViewUrl { get; set; } = "";
        public string VcsUrl { get; set; } = "";
        public string Copyright { get; set; } = "";
        public string LicenseExpression { get; set; } = "";
        public string DeclaredLicense { get; set; } = "";
        public string NoticeText { get; set; } = "";
        public List<string> SourcePackages { get; set; } = [];
        public Dictionary<string, string> ExtraData { get; set; } = new();
        public string RepositoryHomepageUrl { get; set; } = "";
        public string RepositoryDownloadUrl { get; set; } = "";
        public string ApiDataUrl { get; set; } = "";
        public string DatasourceId { get; set; } = "";
        public string DatafilePath { get; set; } = "";
        public List<BasePackage> Dependencies { get; set; } = [];
        public List<string> Warnings { get; set; } = [];
        public List<string> Errors { get; set; } = [];

        // Track if we updated this package metadata
        [JsonIgnore]
        public bool HasUpdatedMetadata;

       public BasePackage(){}

        public BasePackage(string name, string type, string? version, string? framework = "", string? datafilePath = "")
        {
            Name = name;
            Type = type;
            Version = version;
            if (!string.IsNullOrWhiteSpace(framework))
                Version = version;
            if (!string.IsNullOrWhiteSpace(datafilePath))
                DatafilePath = datafilePath;
            if (!string.IsNullOrWhiteSpace(framework))
                ExtraData["framework"] = framework;
        }

        public static BasePackage FromPackage(BasePackage package, List<BasePackage> dependencies)
        {
            return new BasePackage(package.Name, package.Type, package.Version)
            {
                ExtraData = package.ExtraData,
                Dependencies = dependencies
            };
        }

        ///<summary>
        /// Return a deep clone of this package. Optionally clone dependencies.
        ///</summary>
        public BasePackage Clone(bool withDeps = false)
        {
            var deps = withDeps ? Dependencies : [];

            return new BasePackage(
                Name,
                Type,
                version:Version,
                datafilePath: DatafilePath
            )
            {
                Type = Type,
                Namespace = Namespace,

                Qualifiers = Qualifiers,
                Subpath = Subpath,
                Purl = Purl,
                PrimaryLanguage = PrimaryLanguage,
                Description = Description,
                ReleaseDate = ReleaseDate,
                Parties = [..Parties.Select(p => p.Clone())],
                Keywords = [..Keywords],
                HomepageUrl = HomepageUrl,
                DownloadUrl = DownloadUrl,
                Size = Size,
                Sha1 = Sha1,
                Md5 = Md5,
                Sha256 = Sha256,
                Sha512 = Sha512,
                BugTrackingUrl = BugTrackingUrl,
                CodeViewUrl = CodeViewUrl,
                VcsUrl = VcsUrl,
                Copyright = Copyright,
                LicenseExpression = LicenseExpression,
                DeclaredLicense = DeclaredLicense,
                NoticeText = NoticeText,
                SourcePackages = [..SourcePackages],
                RepositoryHomepageUrl = RepositoryHomepageUrl,
                RepositoryDownloadUrl = RepositoryDownloadUrl,
                ApiDataUrl = ApiDataUrl,
                DatasourceId = DatasourceId,
                DatafilePath = DatafilePath,
                Warnings = Warnings,
                Errors = Errors,
                Dependencies = deps,
                ExtraData = new Dictionary<string, string>(ExtraData),
                HasUpdatedMetadata = HasUpdatedMetadata
            };
        }

        protected bool Equals(BasePackage other)
        {
            return
                Type == other.Type
                && Namespace == other.Namespace
                && Name == other.Name
                && Version == other.Version
                && Qualifiers == other.Qualifiers
                && Subpath == other.Subpath;
        }

        public override bool Equals(object? obj)
        {
            if (obj is null) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((BasePackage)obj);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Type, Namespace, Name, Version, Qualifiers, Subpath);
        }

        public PackageIdentity GetPackageIdentity()
        {
            if (!string.IsNullOrWhiteSpace(Version))
                return new PackageIdentity(Name, new NuGetVersion(Version));
            else
                return new PackageIdentity(Name, null);
        }

        /// <summary>
        /// Update this Package instance using the NuGet API to fetch extra metadata
        /// and also update all its dependencies recursively.
        /// </summary>
        public void Update(NugetApi nugetApi, bool withDetails = false)
        {
            if (HasUpdatedMetadata)
                return;

            if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Version))
            {
                Errors.Add("ERROR: Cannot fetch remote metadata: Name or version cannot be empty");
                return;
            }

            try
            {
                UpdateWithRemoteMetadata(nugetApi, withDetails);
            }
            catch (Exception ex)
            {
                var message = $"Failed to get remote metadata for name: '{Name}' version: '{Version}'. ";
                if (Config.TRACE) Console.WriteLine($"        {message}");
                Warnings.Add(message + ex.ToString());
            }
            HasUpdatedMetadata = true;

            foreach (var dep in Dependencies)
                dep.Update(nugetApi, withDetails);
        }

        /// <summary>
        /// Update this Package instance using the NuGet API to fetch metadata
        /// </summary>
        public void UpdateWithRemoteMetadata(NugetApi nugetApi, bool withDetails = false)
        {
            {
                var pid = GetPackageIdentity();
                var psmr = nugetApi.FindPackageVersion(pid);

                // TODO: need to add an error to errors
                if (psmr == null)
                    return;

                // Also fetch download URL and package hash
                var download = nugetApi.GetPackageDownload(pid, withDetails);
                var spdi = nugetApi.GetResolvedSourcePackageDependencyInfo(pid, null);
                NuspecReader? nuspec = null;
                if (spdi != null && download != null && withDetails)
                {
                    nuspec = nugetApi.GetNuspecDetails(
                        pid,
                        download.DownloadUrl,
                        spdi.Source);
                }

                UpdateAttributes(
                    psmr,
                    download,
                    spdi,
                    nuspec);
            }
        }

        /// <summary>
        /// Update this Package instance
        /// </summary>
        public void UpdateAttributes(
            PackageSearchMetadataRegistration? metadata,
            PackageDownload? download,
            SourcePackageDependencyInfo? spdi,
            NuspecReader? nuspec)
        {
            string? syntheticApiDataUrl = null;

            if (metadata != null)
            {
                // set the purl
                var metaName = metadata.Identity.Id;
                var metaVersion = metadata.Identity.Version.ToString();
                if (string.IsNullOrWhiteSpace(metaVersion))
                    Purl = $"pkg:nuget/{metaName}";
                else
                    Purl = $"pkg:nuget/{metaName}@{metaVersion}";

                // Update the declared license
                List<string> metaDeclaredLicenses = [];
                var licenseUrl = metadata.LicenseUrl;
                if (licenseUrl != null && !string.IsNullOrWhiteSpace(licenseUrl.ToString()))
                    metaDeclaredLicenses.Add($"LicenseUrl: {licenseUrl}");

                var licenseMeta = metadata.LicenseMetadata;
                if (licenseMeta != null)
                {
                    metaDeclaredLicenses.Add($"LicenseType: {licenseMeta.Type}");
                    if (!string.IsNullOrWhiteSpace(licenseMeta.License))
                        metaDeclaredLicenses.Add($"License: {licenseMeta.License}");
                    var expression = licenseMeta.LicenseExpression;
                    if (expression != null)
                        metaDeclaredLicenses.Add($"LicenseExpression: {licenseMeta.LicenseExpression}");
                }

                DeclaredLicense = string.Join("\n", metaDeclaredLicenses);

                // Update the parties
                var authors = metadata.Authors;
                if (!string.IsNullOrWhiteSpace(authors) && !Parties.Any(p => p.Name == authors && p.Role == "author"))
                {
                    Party item = new() { Type = "organization", Role = "author", Name = authors };
                    Parties.Add(item);
                }

                var owners = metadata.Owners;
                if (!string.IsNullOrWhiteSpace(owners) && !Parties.Any(p => p.Name == owners && p.Role == "owner"))
                {
                    Party item = new() { Type = "organization", Role = "owner", Name = owners };
                    Parties.Add(item);
                }

                // Update misc and URL fields
                PrimaryLanguage = "C#";
                Description = metadata.Description;

                var tags = metadata.Tags;
                if (!string.IsNullOrWhiteSpace(tags))
                {
                    tags = tags.Trim();
                    Keywords = tags.Split(", ", StringSplitOptions.RemoveEmptyEntries).ToList();
                }

                if (metadata.ProjectUrl != null)
                    HomepageUrl = metadata.ProjectUrl.ToString();

                var nameLower = metaName.ToLower();
                var versionLower = metaVersion.ToLower();

                if (metadata.PackageDetailsUrl != null)
                    RepositoryHomepageUrl = metadata.PackageDetailsUrl.ToString();

                syntheticApiDataUrl = $"https://api.nuget.org/v3/registration5-gz-semver2/{nameLower}/{versionLower}.json";
            }
            if (nuspec != null)
            {
                // vcs package details
                var repo = nuspec.GetRepositoryMetadata();
                var vcsTool = repo.Type ?? "";
                var vcsRepository = repo.Url;
                var vcsCommit = repo.Commit ?? "";

                if (!string.IsNullOrWhiteSpace(vcsRepository))
                {
                    if (!string.IsNullOrWhiteSpace(vcsTool))
                    {
                        VcsUrl = $"{vcsTool}+{vcsRepository}";
                    }
                    else
                    {
                        VcsUrl = vcsRepository ?? "";
                    }

                    if (!string.IsNullOrWhiteSpace(vcsCommit))
                    {
                        VcsUrl = $"{VcsUrl}@{vcsCommit}";
                    }
                }
                Copyright = nuspec.GetCopyright();
            }

            if (download == null)
                return;
            
            // Download data
            if (string.IsNullOrWhiteSpace(Sha512))
                Sha512 = download.Hash;

            if (Size == 0 && download.Size is > 0)
                Size = (int)download.Size;

            if (!string.IsNullOrWhiteSpace(download.DownloadUrl))
            {
                DownloadUrl = download.DownloadUrl;
                RepositoryDownloadUrl = DownloadUrl;
            }

            if (Config.TRACE_NET) Console.WriteLine($"        download_url:{DownloadUrl}");

            // other URLs

            if (
                string.IsNullOrWhiteSpace(ApiDataUrl)
                && DownloadUrl.StartsWith("https://api.nuget.org/")
                && !string.IsNullOrWhiteSpace(syntheticApiDataUrl))
            {
                ApiDataUrl = syntheticApiDataUrl;
            }
            else
            {
                try
                {
                    if (spdi != null && metadata != null)
                        ApiDataUrl = GetApiDataUrl(metadata.Identity, spdi);
                }
                catch (Exception ex)
                {
                    Warnings.Add(ex.ToString());
                }
            }
            if (Config.TRACE_NET) Console.WriteLine($"         api_data_url:{ApiDataUrl}");

            // TODO consider also: https://api.nuget.org/v3-flatcontainer/{name_lower}/{version_lower}/{name_lower}.nuspec
        }

        public static string GetApiDataUrl(PackageIdentity pid, SourcePackageDependencyInfo? spdi)
        {
            var rrv3 = spdi?.Source.GetResource<RegistrationResourceV3>(CancellationToken.None);
            return rrv3 == null 
                ? "" 
                : rrv3.GetUri(pid).ToString();
        }

        /// <summary>
        /// Sort recursively the dependencies of this package.
        /// </summary>
        public void Sort() {
            Dependencies.Sort();
            foreach (var dep in Dependencies)
                dep.Sort();
        }

        bool IEquatable<BasePackage>.Equals(BasePackage? other)
        {
            return other != null && Equals(other);
        }

        public (string, string, string, string, string, string) AsTuple()
        {
            return ValueTuple.Create(
                Type.ToLowerInvariant(),
                Namespace.ToLowerInvariant(),
                Name.ToLowerInvariant(),
                (Version ?? "").ToLowerInvariant(),
                Qualifiers.ToLowerInvariant(),
                Subpath.ToLowerInvariant());
        }

        public int CompareTo(BasePackage? other)
        {
            return other == null
                ? 1 
                : AsTuple().CompareTo(other.AsTuple());
        }

        /// <summary>
        /// Return a flat list of dependencies collected from a list of top-level packages.
        /// </summary>
        public List<BasePackage> GetFlatDependencies()
        {
            var flatDeps = FlattenDeps(Dependencies);
            flatDeps.Sort();
            return flatDeps;
        }

        /// <summary>
        /// Flatten recursively a tree of dependencies. Remove sub-dependencies as the flattening goes.
        /// </summary>
        public static List<BasePackage> FlattenDeps(List<BasePackage> dependencies)
        {
            List<BasePackage> flattened = [];
            foreach (var dependency in dependencies)
            {
                var transitiveDependencies = dependency.Dependencies;
                flattened.Add(dependency.Clone());
                flattened.AddRange(FlattenDeps(transitiveDependencies));
            }
            return flattened;
        }
    }

    /// <summary>
    /// A party is a person, project or organization related to a package.
    /// </summary>
    public class Party
    {
        //One of  'person', 'project' or 'organization'
        public string Type { get; set; } = "";
        public string Role { get; set; } = "";
        public string? Name { get; set; } = "";
        public string? Email { get; set; } = "";
        public string? Url { get; set; } = "";

        public Party Clone()
        {
            return new Party(){
                Type=Type,
                Role=Role,
                Name=Name,
                Email=Email,
                Url=Url
            };
        }
    }

    /// <summary>
    /// A PackageDownload has a URL and checksum
    /// </summary>
    public class PackageDownload
    {
        public string DownloadUrl { get; set; } = "";
        public string Hash { get; set; } = "";
        public string HashAlgorithm { get; set; } = "";
        public int? Size { get; set; } = 0;
        public bool IsEnhanced(){
            return !string.IsNullOrWhiteSpace(DownloadUrl) && !string.IsNullOrWhiteSpace(Hash);
        }

        public static PackageDownload FromSpdi(SourcePackageDependencyInfo spdi)
        {
            PackageDownload download = new(){ DownloadUrl = spdi.DownloadUri.ToString() };
            /// Note that this hash is unlikely there per https://github.com/NuGet/NuGetGallery/issues/9433
            if (string.IsNullOrEmpty(spdi.PackageHash))
                return download;
            
            download.Hash = spdi.PackageHash;
            download.HashAlgorithm = "SHA512";
            return download;
        }

        public override string ToString()
        {
            return $"{DownloadUrl} hash: {Hash} hash_algorithm: {HashAlgorithm} size: {Size}";
        }
    }

    public class ScannedFile
    {
        public string Path { get; set; } = "";
        // file or directory
        public string Type { get; set; } = "file";
        public string Name { get; set; } = "";
        public string BaseName { get; set; } = "";
        public string Extension { get; set; } = "";
        public int? Size { get; set; } = 0;
    }
}