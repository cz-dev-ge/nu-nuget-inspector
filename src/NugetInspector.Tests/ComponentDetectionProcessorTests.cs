using NUnit.Framework;

namespace NugetInspector.Tests;

[TestFixture]
public class ComponentDetectionProcessorTests
{
    private string _TempRoot = "";

    [SetUp]
    public void SetUp()
    {
        _TempRoot = Path.Combine(Path.GetTempPath(), "nuget-inspector-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_TempRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_TempRoot))
            Directory.Delete(_TempRoot, recursive: true);
    }

    [Test]
    public void Resolve_FindsTopLevelPackage_FromProjectAssetsJson()
    {
        var projectDir = ProjectAssetsFixture.CreateProjectWithSinglePackage(
            _TempRoot,
            projectName: "ProjectA",
            topLevelPackageId: "Newtonsoft.Json",
            topLevelPackageVersion: "13.0.3");

        var resolution = new ComponentDetectionProcessor(projectDir).Resolve();

        Assert.That(resolution.Success, Is.True);
        Assert.That(resolution.Dependencies, Has.Exactly(1).Matches<BasePackage>(
            p => p is { Name: "Newtonsoft.Json", Version: "13.0.3" }));
    }

    [Test]
    public void Resolve_BuildsNestedDependencyTree_FromProjectAssetsJson()
    {
        var projectDir = ProjectAssetsFixture.CreateProjectWithSinglePackage(
            _TempRoot,
            projectName: "ProjectWithTransitive",
            topLevelPackageId: "Top.Package",
            topLevelPackageVersion: "1.0.0",
            transitiveDependency: ("Transitive.Package", "2.0.0"));

        var resolution = new ComponentDetectionProcessor(projectDir).Resolve();

        Assert.That(resolution.Success, Is.True);
        var topLevel = resolution.Dependencies.Single(p => p.Name == "Top.Package");
        Assert.That(topLevel.Dependencies, Has.Exactly(1).Matches<BasePackage>(
            p => p is { Name: "Transitive.Package", Version: "2.0.0" }));

        // The transitive package should not itself be listed as top-level.
        Assert.That(resolution.Dependencies.Any(p => p.Name == "Transitive.Package"), Is.False);
    }

    [Test]
    public void Resolve_SetsPurlAndDatasourceId()
    {
        var projectDir = ProjectAssetsFixture.CreateProjectWithSinglePackage(
            _TempRoot,
            projectName: "ProjectB",
            topLevelPackageId: "Some.Package",
            topLevelPackageVersion: "9.9.9");

        var resolution = new ComponentDetectionProcessor(projectDir).Resolve();

        var package = resolution.Dependencies.Single();
        Assert.That(package.Purl, Does.Contain("pkg:nuget/Some.Package@9.9.9"));
        Assert.That(package.DatasourceId, Is.EqualTo(ComponentDetectionProcessor.DatasourceId));
    }

    [Test]
    public void Resolve_DoesNotCrossContaminate_SiblingProjectsUnderSameSourceDirectory()
    {
        // Regression test for the risk that Microsoft.ComponentDetection's detectors
        // recursively scan the whole SourceDirectory tree for manifest files (unlike the
        // old exact-path parsers), which could otherwise pick up an unrelated nested
        // project's dependencies when scanning a monorepo-style directory layout.
        // ComponentDetectionProcessor filters components/graphs down to those owned by
        // the target project's own directory to prevent this.
        var parentDir = ProjectAssetsFixture.CreateProjectWithSinglePackage(
            _TempRoot,
            projectName: "ParentProject",
            topLevelPackageId: "Parent.Package",
            topLevelPackageVersion: "1.0.0");

        ProjectAssetsFixture.CreateProjectWithSinglePackage(
            parentDir,
            projectName: "NestedProject",
            topLevelPackageId: "Nested.Package",
            topLevelPackageVersion: "2.0.0");

        var resolution = new ComponentDetectionProcessor(parentDir).Resolve();

        var foundNames = resolution.Dependencies.Select(p => p.Name).ToList();

        Assert.That(foundNames, Does.Contain("Parent.Package"),
            "Expected the target project's own package to be found.");
        Assert.That(foundNames, Does.Not.Contain("Nested.Package"),
            "A nested sibling project's packages must not leak into the parent project's results.");
    }
}
