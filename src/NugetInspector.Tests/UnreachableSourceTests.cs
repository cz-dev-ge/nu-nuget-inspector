using NUnit.Framework;
using NuGet.Frameworks;

namespace NugetInspector.Tests;

/// <summary>
/// Verifies the fix for a real-world bug: nuget-inspector used to abort the entire scan
/// (throwing out of the NugetApi constructor) as soon as a single configured NuGet source
/// could not be reached or failed to authenticate (e.g. a private feed returning 401
/// Unauthorized). This meant that even packages available from a perfectly healthy source
/// (such as the public nuget.org) would never be resolved. NugetApi should instead log a
/// warning and skip the broken source, continuing with the remaining ones.
/// </summary>
[TestFixture]
[Category("Network")]
public class UnreachableSourceTests
{
    [Test]
    public void Constructor_DoesNotThrow_WhenOneSourceIsUnreachable()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nuget-inspector-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);

        // A source that is syntactically valid but will fail to connect (nothing listens on
        // this port), alongside a normal working nuget.org entry.
        var nugetConfigPath = Path.Combine(tempDir, "nuget.config");
        File.WriteAllText(nugetConfigPath, """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
                <add key="broken-feed" value="https://localhost:1/nuget/v3/index.json" />
              </packageSources>
            </configuration>
            """);

        try
        {
            var framework = NuGetFramework.ParseFolder("net10.0");

            NugetApi? nugetApi = null;
            Assert.DoesNotThrow(() =>
            {
                nugetApi = new NugetApi(
                    nugetConfigPath: nugetConfigPath,
                    projectRootPath: tempDir,
                    projectFramework: framework,
                    withNugetOrg: false);
            });

            // The healthy nuget.org source should still be usable despite the broken one.
            var package = new BasePackage("Newtonsoft.Json", ComponentType.NuGet, "13.0.3");
            try
            {
                package.Update(nugetApi!, withDetails: false);
            }
            catch (Exception ex)
            {
                Assert.Ignore($"Skipping: could not reach nuget.org: {ex.Message}");
            }

            Assert.That(package.HasUpdatedMetadata, Is.True);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
