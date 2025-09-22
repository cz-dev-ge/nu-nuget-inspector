using NuGet.LibraryModel;
using NuGet.ProjectModel;

namespace NugetInspector;

/// <summary>
/// Handles legacy project.json format
/// See https://learn.microsoft.com/en-us/nuget/archive/project-json
/// </summary>
internal class ProjectJsonProcessor : IDependencyProcessor
{
    public const string DatasourceId = "dotnet-project.json";
    private readonly string _ProjectJsonPath;
    private readonly string? _ProjectName;

    public ProjectJsonProcessor(string? projectName, string projectJsonPath)
    {
        _ProjectName = projectName;
        _ProjectJsonPath = projectJsonPath;
    }

    public DependencyResolution Resolve()
    {
        var resolution = new DependencyResolution();
        var model = JsonPackageSpecReader.GetPackageSpec(name: _ProjectName, packageSpecPath: _ProjectJsonPath);
        foreach (var package in (IList<LibraryDependency>)model.Dependencies)
        {
            var bpwd = new BasePackage(
                name: package.Name,
                version: package.LibraryRange.VersionRange.OriginalString
            );
            resolution.Dependencies.Add(item: bpwd);
        }
        return resolution;
    }
}