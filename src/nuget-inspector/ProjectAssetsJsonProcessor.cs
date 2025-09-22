using NuGet.ProjectModel;

namespace NugetInspector;

internal class ProjectAssetsJsonProcessor(string projectAssetsJsonPath) : IDependencyProcessor
{
    public const string DatasourceId = "dotnet-project.assets.json";

    public DependencyResolution Resolve()
    {
        var lockFile = LockFileUtilities.GetLockFile(lockFilePath: projectAssetsJsonPath, logger: null);
        var resolver = new LockFileHelper(lockfile: lockFile);
        return resolver.Process();
    }
}