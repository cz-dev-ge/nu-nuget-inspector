using NuGet.LibraryModel;
using NuGet.ProjectModel;

namespace NugetInspector;

/// <summary>
/// Handles legacy project.json format
/// See https://learn.microsoft.com/en-us/nuget/archive/project-json
/// </summary>
internal class ProjectJsonProcessor(string? projectName, string projectJsonPath) : IDependencyProcessor
{
    public const string DatasourceId = "dotnet-project.json";

    public DependencyResolution Resolve()
    {
        var resolution = new DependencyResolution();
        var model = JsonPackageSpecReader.GetPackageSpec(projectName, projectJsonPath);
        foreach (var package in (IList<LibraryDependency>)model.Dependencies)
        {
            var bpwd = new BasePackage(
                package.Name,
                package.LibraryRange.VersionRange?.OriginalString
            );
            resolution.Dependencies.Add(bpwd);
        }
        return resolution;
    }
}