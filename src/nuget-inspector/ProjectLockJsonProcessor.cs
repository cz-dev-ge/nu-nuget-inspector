using NuGet.ProjectModel;

namespace NugetInspector;

/// <summary>
/// See https://learn.microsoft.com/en-us/nuget/archive/project-json#projectlockjson
/// </summary>
internal class ProjectLockJsonProcessor(string projectLockJsonPath) : IDependencyProcessor
{
    public const string DatasourceId = "dotnet-project.lock.json";

    public DependencyResolution Resolve()
    {
        var lockFile = LockFileUtilities.GetLockFile(lockFilePath: projectLockJsonPath, logger: new NugetLogger());
        if (lockFile == null)
            throw new Exception($"Failed to get parse lockfile at path: {projectLockJsonPath}");
        var resolver = new LockFileHelper(lockfile: lockFile);
        if (Config.TRACE) Console.WriteLine($"resolver: {resolver}");
        return resolver.Process();
    }
}