using System.Text.Json;

namespace NugetInspector.Tests;

/// <summary>
/// Helper to build minimal, self-contained project.assets.json fixtures on disk so that
/// Microsoft.ComponentDetection's NuGetProjectCentric detector can be exercised without
/// requiring an actual `dotnet restore` or network access.
/// </summary>
internal static class ProjectAssetsFixture
{
    /// <summary>
    /// Creates a temp directory containing a fake "&lt;projectName&gt;.csproj" and a matching
    /// "obj/project.assets.json" declaring a single top-level package (with an optional
    /// transitive dependency), which is enough for the NuGet detector to pick up.
    /// </summary>
    public static string CreateProjectWithSinglePackage(
        string rootDirectory,
        string projectName,
        string topLevelPackageId,
        string topLevelPackageVersion,
        (string Id, string Version)? transitiveDependency = null)
    {
        var projectDir = Path.Combine(rootDirectory, projectName);
        var objDir = Path.Combine(projectDir, "obj");
        Directory.CreateDirectory(objDir);

        var projectPath = Path.Combine(projectDir, $"{projectName}.csproj");
        File.WriteAllText(projectPath, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="{topLevelPackageId}" Version="{topLevelPackageVersion}" />
              </ItemGroup>
            </Project>
            """);

        var dependencies = new Dictionary<string, object>();
        if (transitiveDependency is { } dep)
            dependencies[dep.Id] = dep.Version;

        var targetEntry = new Dictionary<string, object>
        {
            [$"{topLevelPackageId}/{topLevelPackageVersion}"] = new Dictionary<string, object>
            {
                ["type"] = "package",
                ["dependencies"] = dependencies,
            },
        };
        var libraries = new Dictionary<string, object>
        {
            [$"{topLevelPackageId}/{topLevelPackageVersion}"] = new Dictionary<string, object>
            {
                ["sha512"] = "AAAA",
                ["type"] = "package",
                ["path"] = $"{topLevelPackageId.ToLowerInvariant()}/{topLevelPackageVersion}",
                ["files"] = new[] { $"{topLevelPackageId.ToLowerInvariant()}.nuspec" },
            },
        };

        if (transitiveDependency is { } transitive)
        {
            targetEntry[$"{transitive.Id}/{transitive.Version}"] = new Dictionary<string, object>
            {
                ["type"] = "package",
            };
            libraries[$"{transitive.Id}/{transitive.Version}"] = new Dictionary<string, object>
            {
                ["sha512"] = "BBBB",
                ["type"] = "package",
                ["path"] = $"{transitive.Id.ToLowerInvariant()}/{transitive.Version}",
                ["files"] = new[] { $"{transitive.Id.ToLowerInvariant()}.nuspec" },
            };
        }

        var assets = new Dictionary<string, object>
        {
            ["version"] = 3,
            ["targets"] = new Dictionary<string, object> { ["net10.0"] = targetEntry },
            ["libraries"] = libraries,
            ["projectFileDependencyGroups"] = new Dictionary<string, object>
            {
                ["net10.0"] = new[] { $"{topLevelPackageId} >= {topLevelPackageVersion}" },
            },
            ["project"] = new Dictionary<string, object>
            {
                ["version"] = "1.0.0",
                ["restore"] = new Dictionary<string, object>
                {
                    ["projectUniqueName"] = projectPath,
                    ["projectName"] = projectName,
                    ["projectPath"] = projectPath,
                    ["outputPath"] = objDir,
                    ["projectStyle"] = "PackageReference",
                    ["originalTargetFrameworks"] = new[] { "net10.0" },
                    ["frameworks"] = new Dictionary<string, object>
                    {
                        ["net10.0"] = new Dictionary<string, object> { ["projectReferences"] = new Dictionary<string, object>() },
                    },
                },
                ["frameworks"] = new Dictionary<string, object>
                {
                    ["net10.0"] = new Dictionary<string, object>
                    {
                        ["dependencies"] = new Dictionary<string, object>
                        {
                            [topLevelPackageId] = new Dictionary<string, object>
                            {
                                ["target"] = "Package",
                                ["version"] = $"[{topLevelPackageVersion}, )",
                            },
                        },
                    },
                },
            },
        };

        File.WriteAllText(
            Path.Combine(objDir, "project.assets.json"),
            JsonSerializer.Serialize(assets, new JsonSerializerOptions { WriteIndented = true }));

        return projectDir;
    }
}
