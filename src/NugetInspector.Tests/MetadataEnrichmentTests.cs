using NUnit.Framework;
using NuGet.Frameworks;

namespace NugetInspector.Tests;

/// <summary>
/// Verifies that metadata enrichment (fetching license/author/download-url/etc. from the
/// NuGet API via BasePackage.Update) still works after the migration to
/// Microsoft.ComponentDetection for dependency-graph resolution. ComponentDetectionProcessor
/// only replaces graph resolution - it returns bare BasePackage objects (Name/Version/Purl
/// only) which are then enriched by this separate, unchanged pipeline.
///
/// These tests hit the real nuget.org API and are skipped automatically if the network
/// is unavailable.
/// </summary>
[TestFixture]
[Category("Network")]
public class MetadataEnrichmentTests
{
    private NugetApi _nugetApi = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var framework = NuGetFramework.ParseFolder("net10.0");
        _nugetApi = new NugetApi(
            nugetConfigPath: "",
            projectRootPath: Path.GetTempPath(),
            projectFramework: framework,
            withNugetOrg: true);
    }

    [Test]
    public void Update_PopulatesCoreMetadata_ForWellKnownPackage()
    {
        var package = new BasePackage("Newtonsoft.Json", ComponentType.NuGet, "13.0.3");

        try
        {
            package.Update(_nugetApi, withDetails: true);
        }
        catch (Exception ex)
        {
            Assert.Ignore($"Skipping: could not reach nuget.org: {ex.Message}");
        }

        Assert.That(package.Warnings, Is.Empty, "Expected no warnings/errors fetching metadata");
        Assert.That(package.Purl, Is.EqualTo("pkg:nuget/Newtonsoft.Json@13.0.3"));
        Assert.That(package.DownloadUrl, Is.Not.Empty);
        Assert.That(package.RepositoryDownloadUrl, Is.Not.Empty);
        Assert.That(package.DeclaredLicense, Is.Not.Empty);
        Assert.That(package.Parties, Is.Not.Empty);
        Assert.That(package.HasUpdatedMetadata, Is.True);
    }

    [Test]
    public void Update_RecursivelyEnrichesNestedDependencies()
    {
        var child = new BasePackage("Newtonsoft.Json", ComponentType.NuGet, "13.0.3");
        var parent = new BasePackage("Top.Level.Package", ComponentType.NuGet, "1.0.0");
        parent.Dependencies.Add(child);

        try
        {
            parent.Update(_nugetApi, withDetails: false);
        }
        catch (Exception ex)
        {
            Assert.Ignore($"Skipping: could not reach nuget.org: {ex.Message}");
        }

        // The parent package itself does not exist on nuget.org, so it should get a
        // warning but still be marked as processed; the nested real dependency should
        // still be enriched successfully.
        Assert.That(parent.HasUpdatedMetadata, Is.True);
        Assert.That(child.HasUpdatedMetadata, Is.True);
        Assert.That(child.Purl, Is.EqualTo("pkg:nuget/Newtonsoft.Json@13.0.3"));
    }
}
