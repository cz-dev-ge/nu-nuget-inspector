using Microsoft.Build.Evaluation;
using NuGet.Common;
using NuGet.Frameworks;
using NuGet.LibraryModel;
using NuGet.PackageManagement;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using System.Text;
using System.Xml;

namespace NugetInspector;

/// <summary>
/// See https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files
/// This handler reads a *.*proj file using MSBuild readers and calls the NuGet API for resolution.
/// </summary>
internal class ProjectFileProcessor : IDependencyProcessor
{
    public const string DatasourceId = "dotnet-project-reference";
    public readonly NuGetFramework? ProjectFramework;
    public readonly NugetApi NugetApi;
    public readonly string ProjectPath;

    public ProjectFileProcessor(
        string projectPath,
        NugetApi nugetApi,
        NuGetFramework? projectFramework)
    {
        ProjectPath = projectPath;
        this.NugetApi = nugetApi;
        ProjectFramework = projectFramework;
    }

    public List<Dependency> GetDependenciesFromReferences(List<PackageReference> references)
    {
        var dependencies = new List<Dependency>();
        foreach (var reference in references)
        {
            var rpid = reference.PackageIdentity;
            var dep = new Dependency(
                name: rpid.Id,
                versionRange: reference.AllowedVersions ?? new VersionRange(rpid.Version),
                framework: ProjectFramework,
                isDirect: true);
            dependencies.Add(item: dep);
        }

        return dependencies;
    }

    /// <summary>
    /// Return a deduplicated list of PackageReference, selecting the first of each
    /// duplicated package names in the original order. This is the dotnet behaviour.
    /// </summary>
    public static List<PackageReference> DeduplicateReferences(List<PackageReference> references)
    {
        var byName = new Dictionary<string, List<PackageReference>>();

        foreach (var reference in references)
        {
            var pid = reference.PackageIdentity;
            List<PackageReference> refs;
            if (byName.ContainsKey(pid.Id))
            {
                refs = byName[pid.Id];
            }
            else
            {
                refs = [];
                byName[pid.Id] = refs;
            }
            refs.Add(reference);
        }

        var deduped = new List<PackageReference>();
        foreach(var dupes in byName.Values)
        {
            if (Config.TRACE)
            {
                if (dupes.Count != 1)
                {
                    var duplicated = string.Join("; ", dupes.Select(d => string.Join(", ", $"{d.PackageIdentity}")));

                    Console.WriteLine(
                        "DeduplicateReferences: Remove the duplicate items to ensure a consistent dotnet restore behavior. "
                        + $"The duplicate 'PackageReference' items are: {duplicated}");
                }
            }
            deduped.Add(dupes[0]);
        }
        return deduped;
    }

    /// <summary>
    /// Copied from NuGet.Client/src/NuGet.Core/NuGet.Build.Tasks.Console/MSBuildStaticGraphRestore.cs
    /// Copyright (c) .NET Foundation. All rights reserved.
    /// Licensed under the Apache License, Version 2.0.
    /// Gets the <see cref="LibraryIncludeFlags" /> for the specified value.
    /// </summary>
    /// <param name="value">A semicolon delimited list of include flags.</param>
    /// <param name="defaultValue">The default value ot return if the value contains no flags.</param>
    /// <returns>The <see cref="LibraryIncludeFlags" /> for the specified value, otherwise the <paramref name="defaultValue" />.</returns>
    private static LibraryIncludeFlags GetLibraryIncludeFlags(string value, LibraryIncludeFlags defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        var parts = MSBuildStringUtility.Split(value);

        return parts.Length > 0 ? LibraryIncludeFlagUtils.GetFlags(parts) : defaultValue;
    }

    /// <summary>
    /// Return a list of PackageReference extracted from the project file
    /// using a project model.
    /// </summary>
    public virtual List<PackageReference> GetPackageReferences()
    {
        if (Config.TRACE)
            Console.WriteLine($"ProjectFileProcessor.GetPackageReferences: ProjectPath {ProjectPath}");

        List<PackageReference> references = [];

        // TODO: consider reading global.json if present?
        Dictionary<string, string> properties = new();
        if (ProjectFramework != null)
            properties["TargetFramework"] = ProjectFramework.GetShortFolderName();

        var project = new Project(
            projectFile: ProjectPath,
            globalProperties: properties,
            toolsVersion: null);

        foreach (var reference in project.GetItems(itemType: "PackageReference"))
        {
            var name = reference.EvaluatedInclude;

            if (Config.TRACE_DEEP)
            {
                Console.WriteLine($"    Project reference: {reference}");
                Console.WriteLine($"    Project reference: name: {name}");
                foreach (var meta in reference.Metadata)
                    Console.WriteLine($"        Metadata: name: '{meta.Name}' value: '{meta.EvaluatedValue}'");
            }

            // Skip implicit references
            var isImplicit = false;
            foreach (var meta in reference.Metadata)
            {
                if  (meta.Name == "IsImplicitlyDefined" && meta.EvaluatedValue=="true")
                    isImplicit = true;
            }
            if (isImplicit)
            {
                if (Config.TRACE)
                    Console.WriteLine($"    Skipping implicit package reference for {name}");
                continue;
            }

            // Compute the include and exclude flags to skip private assets
            var effectiveIncludesFlag = LibraryIncludeFlags.All;
            var privateAssets = LibraryIncludeFlags.None;

            foreach (var meta in reference.Metadata)
            {
                if (meta.Name == "IncludeAssets")
                    effectiveIncludesFlag &= GetLibraryIncludeFlags(meta.EvaluatedValue, LibraryIncludeFlags.All);
                if (meta.Name == "ExcludeAssets")
                    effectiveIncludesFlag &= ~GetLibraryIncludeFlags(meta.EvaluatedValue, LibraryIncludeFlags.None);
                // Private assets is treated as an exclude
                if (meta.Name == "PrivateAssets")
                    privateAssets = GetLibraryIncludeFlags(meta.EvaluatedValue, LibraryIncludeFlagUtils.DefaultSuppressParent);
            }
            // Skip fully private assets for package references
            effectiveIncludesFlag &= ~privateAssets;
            if (effectiveIncludesFlag == LibraryIncludeFlags.None || privateAssets == LibraryIncludeFlags.All)
            {
                if (Config.TRACE)
                    Console.WriteLine($"    Skipping private or excluded asset reference for {name}");
                continue;
            }

            var versionMetadata = reference.Metadata.FirstOrDefault(predicate: meta => meta.Name == "Version");
            VersionRange? versionRange;
            if (versionMetadata is not null)
            {
                _ = VersionRange.TryParse(
                    value: versionMetadata.EvaluatedValue,
                    allowFloating: true,
                    versionRange: out versionRange);
            }
            else
            {
                if (Config.TRACE)
                    Console.WriteLine($"    Project reference without version: {name}");
                versionRange = VersionRange.All;
                // // find the minimum version in the range
                // var psmr = nugetApi.FindPackageVersion(name: name, version_range: version_range);
                // if (psmr != null)
                // {
                //     version_range = new VersionRange(new NuGetVersion(psmr.Version));
                // }
                // else
                // {
                //     continue;
                // }
            }

            PackageReference packref;

            if (versionRange == null)
            {
                if (Config.TRACE)
                    Console.WriteLine($"    Project reference without version range: {name}");

                packref = new PackageReference(
                    identity: new PackageIdentity(id: name, version: null),
                    targetFramework: ProjectFramework,
                    userInstalled: false,
                    developmentDependency: false,
                    requireReinstallation: false,
                    allowedVersions: VersionRange.All);
            }
            else
            {
                packref = new PackageReference(
                    identity: new PackageIdentity(id: name, version: null),//(NuGetVersion?)version_range.MinVersion),
                    targetFramework: ProjectFramework,
                    userInstalled: false,
                    developmentDependency: false,
                    requireReinstallation: false,
                    allowedVersions: versionRange);
            }
            references.Add(item: packref);

            if (Config.TRACE)
            {
                Console.WriteLine(
                    $"    Add Direct dependency from PackageReference: id: {packref.PackageIdentity} "
                    + $"version_range: {packref.AllowedVersions}");
            }
        }

        // Also fetch "legacy" versioned references
        foreach (var reference in project.GetItems(itemType: "Reference"))
        {
            if (reference.Xml == null || string.IsNullOrWhiteSpace(value: reference.Xml.Include) ||
                !reference.Xml.Include.Contains("Version=")) continue;
            
            var packageInfo = reference.Xml.Include;

            var commaPos = packageInfo.IndexOf(',');
            var artifact = packageInfo[..commaPos];

            const string versionKey = "Version=";
            var versionKeyIndex = packageInfo.IndexOf(value: versionKey, comparisonType: StringComparison.Ordinal);
            var versionStartIndex = versionKeyIndex + versionKey.Length;
            var packageInfoAfterVersionKey = packageInfo[versionStartIndex..];

            string version;
            if (packageInfoAfterVersionKey.Contains(','))
            {
                var firstSep =
                    packageInfoAfterVersionKey.IndexOf(",", comparisonType: StringComparison.Ordinal);
                version = packageInfoAfterVersionKey[..firstSep];
            }
            else
            {
                version = packageInfoAfterVersionKey;
            }

            VersionRange? versionRange = null;
            NuGetVersion? vers = null;

            if (!string.IsNullOrWhiteSpace(version))
            {
                _ = VersionRange.TryParse(
                    value: version,
                    allowFloating: true,
                    versionRange: out versionRange);

                if (versionRange != null)
                    vers = versionRange.MinVersion;
            }

            PackageReference plainref = new (
                identity: new PackageIdentity(id: artifact, version: vers),
                targetFramework: ProjectFramework,
                userInstalled: false,
                developmentDependency: false,
                requireReinstallation: false,
                allowedVersions: versionRange);

            references.Add(plainref);

            if (Config.TRACE)
            {
                Console.WriteLine(
                    $"    Add Direct dependency from plain Reference: id: {plainref.PackageIdentity} "
                    + $"version_range: {plainref.AllowedVersions}");
            }
        }
        ProjectCollection.GlobalProjectCollection.UnloadProject(project: project);
        return references;
    }

    /// <summary>
    /// Resolve the dependencies resolving all direct dependencies at once.
    /// </summary>
    public DependencyResolution Resolve()
    {
        return ResolveUsingLib();
    }

    /// <summary>
    /// Resolve the dependencies resolving all direct dependencies at once.
    /// </summary>
    public DependencyResolution ResolveUseGather()
    {
        if (Config.TRACE)
            Console.WriteLine("\nProjectFileProcssor.Resolve: starting resolution");

        var references = GetPackageReferences();
        if (references.Count == 0)
        {
            return new DependencyResolution(success: true);
        }
        else if (Config.TRACE)
        {
            foreach (var reference in references)
                Console.WriteLine($"    reference: {reference}");
        }

        references = DeduplicateReferences(references);
        var dependencies = GetDependenciesFromReferences(references);

        // FIXME: was using CollectDirectDeps(dependencies);
        var directDependencyPids = references.ConvertAll(r => r.PackageIdentity);

        // Use the gather approach to gather all possible deps
        var availableDependencies  = NugetApi.GatherPotentialDependencies(
            directDependencies: directDependencyPids,
            framework: ProjectFramework!
        );

        if (Config.TRACE_DEEP)
        {
            foreach (var spdi in availableDependencies)
                Console.WriteLine($"    available_dependencies: {spdi.Id}@{spdi.Version} prerel:{spdi.Version.IsPrerelease}");
        }

        IEnumerable<SourcePackageDependencyInfo> prunedDependencies = availableDependencies.ToList();

        // Prune the potential dependencies from prereleases
        prunedDependencies = PrunePackageTree.PrunePreleaseForStableTargets(
            packages: prunedDependencies,
            targets: directDependencyPids,
            packagesToInstall: directDependencyPids
        );
        if (Config.TRACE_DEEP)
        {
            foreach (var spdi in prunedDependencies)
                Console.WriteLine($"    After PrunePreleaseForStableTargets: {spdi.Id}@{spdi.Version} IsPrerelease: {spdi.Version.IsPrerelease}");
        }

        // prune prerelease versions
        prunedDependencies = PrunePackageTree.PrunePrereleaseExceptAllowed(
            packages: prunedDependencies,
            installedPackages: directDependencyPids,
            isUpdateAll: false).ToList();
        if (Config.TRACE_DEEP)
            foreach (var spdi in prunedDependencies) Console.WriteLine($"    After PrunePrereleaseExceptAllowed: {spdi.Id}@{spdi.Version}");

        // prune versions that do not match version range constraints
        prunedDependencies = PrunePackageTree.PruneDisallowedVersions(
            packages: prunedDependencies,
            packageReferences: references);
        if (Config.TRACE_DEEP)
            foreach (var spdi in prunedDependencies) Console.WriteLine($"    After PruneDisallowedVersions: {spdi.Id}@{spdi.Version}");

        // prune downgrades as we always targetted min versions and no downgrade is OK
        prunedDependencies = PrunePackageTree.PruneDowngrades(prunedDependencies, references).ToList();
        if (Config.TRACE_DEEP)
            foreach (var spdi in prunedDependencies) Console.WriteLine($"    PruneDowngrades: {spdi.Id}@{spdi.Version}");

        prunedDependencies = prunedDependencies.ToList();
        if (Config.TRACE)
            Console.WriteLine($"    Resolving: {references.Count} references with {prunedDependencies.Count()} dependencies");

        var resolvedDeps = NugetApi.ResolveDependenciesForPackageConfig(
        targetReferences: references,
        availableDependencies: prunedDependencies);

        DependencyResolution resolution = new(success: true);
        foreach (var resolvedDep in resolvedDeps)
        {
            if (Config.TRACE_DEEP)
            {
                Console.WriteLine($"     resolved: {resolvedDep.Id}@{resolvedDep.Version}");
                foreach (var subdep in resolvedDep.Dependencies)
                    Console.WriteLine($"        subdep: {subdep.Id}@{subdep.VersionRange}");
            }
            BasePackage dep = new(
                name: resolvedDep.Id,
                version: resolvedDep.Version.ToString(),
                framework: ProjectFramework!.GetShortFolderName());

            resolution.Dependencies.Add(dep);
        }

        return resolution;
    }
    /// <summary>
    /// Resolve the dependencies resolving all direct dependencies at once using a new resolver
    /// </summary>
    public DependencyResolution ResolveUsingLib()
    {
        if (Config.TRACE)
            Console.WriteLine("\nProjectFileProcessor.ResolveUsingLib: starting resolution");

        var references = GetPackageReferences();
        if (references.Count == 0)
        {
            if (Config.TRACE)
                Console.WriteLine("      No references found.");

            return new DependencyResolution(success: true);
        }
        else if (Config.TRACE)
        {
            Console.WriteLine($"    Found #{references.Count} references");
        }

        references = DeduplicateReferences(references);
        if (Config.TRACE_DEEP)
        {
            foreach (var reference in references)
                Console.WriteLine($"      Deduped reference: {reference}");
        }
        var resolvedDeps = NugetApi.ResolveDependenciesForPackageReference(targetReferences: references);

        DependencyResolution resolution = new(success: true);
        foreach (var resolvedDep in resolvedDeps)
        {
            if (Config.TRACE_DEEP)
            {
                Console.WriteLine($"      resolved: {resolvedDep.Id}@{resolvedDep.Version}");
                foreach (var subdep in resolvedDep.Dependencies)
                    Console.WriteLine($"        subdep: {subdep.Id}@{subdep.VersionRange}");
            }
            BasePackage dep = new(
                name: resolvedDep.Id,
                version: resolvedDep.Version.ToString(),
                framework: ProjectFramework!.GetShortFolderName());

            resolution.Dependencies.Add(dep);
        }

        return resolution;
    }
}

/// <summary>
/// Read the .*proj file directly as XML to extract PackageReference as a last resort
/// This handler reads a *.*proj file as plain XML and calls the NuGet API for resolution.
/// </summary>
internal class ProjectXmlFileProcessor : ProjectFileProcessor
{
    public new const string DatasourceId = "dotnet-project-xml";

    public ProjectXmlFileProcessor(
        string projectPath,
        NugetApi nugetApi,
        NuGetFramework? projectFramework) : base(projectPath, nugetApi, projectFramework)
    {
    }

    /// <summary>
    /// Return a list of PackageReference extracted from the raw XML of a project file.
    /// Note that this is used only as a fallback and does not handle the same
    /// breadth of attributes as with an MSBuild-based parsing. In particular
    /// this does not handle frameworks and conditions.    /// using a project model.
    /// </summary>
    public override List<PackageReference> GetPackageReferences()
    {
        if (Config.TRACE)
            Console.WriteLine($"ProjectXmlFileProcessor.GetPackageReferences: ProjectPath {ProjectPath}");

        var references = new List<PackageReference>();

        Encoding.RegisterProvider(provider: CodePagesEncodingProvider.Instance);
        var doc = new XmlDocument();
        doc.Load(filename: ProjectPath);

        var packagesNodes = doc.GetElementsByTagName(name: "PackageReference");
        foreach (XmlNode package in packagesNodes)
        {
            var attributes = package.Attributes;
            string? versionValue = null;

            if (attributes == null)
                continue;

            if (Config.TRACE)
                Console.WriteLine($"    attributes {attributes.ToJson()}");

            var include = attributes[name: "Include"];
            if (include == null)
                continue;

            var version = attributes[name: "Version"];
            if (version != null)
            {
                versionValue = version.Value;
            }
            else
            {
                // XML is beautfiful: let's try nested element instead of direct attribute  
                foreach (XmlElement versionNode in package.ChildNodes)
                {
                    if (versionNode.Name == "Version")
                    {
                        if (Config.TRACE)
                            Console.WriteLine($"    no version attribute, using Version tag: {versionNode.InnerText}");
                        versionValue = versionNode.InnerText;
                    }
                }
            }

            if (Config.TRACE_DEEP)
                Console.WriteLine($"        version_value: {versionValue}");

            PackageReference packref;
            var name = include.Value;

            VersionRange? versionRange = null;
            if (versionValue != null)
                versionRange = VersionRange.Parse(value: versionValue);

            PackageIdentity identity = new(id: name, version: null);

            if (versionRange == null)
            {
                packref = new PackageReference(
                    identity: identity,
                    targetFramework: ProjectFramework);
            } else {
                packref = new PackageReference(
                    identity: identity,
                    targetFramework: ProjectFramework,
                    userInstalled: false,
                    developmentDependency: false,
                    requireReinstallation: false,
                    allowedVersions: versionRange);
            }
            references.Add(packref);
        }
        return references;
    }
}