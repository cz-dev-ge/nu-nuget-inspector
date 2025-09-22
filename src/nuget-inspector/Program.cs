using System.Diagnostics;
using Microsoft.Build.Locator;
using Newtonsoft.Json;
using NuGet.Frameworks;

namespace NugetInspector;

internal static class Program
{
    public static void Main(string[] args)
    {
        try
        {
            if (Config.TRACE) Console.WriteLine("Registering MSBuild defaults.");
            MSBuildLocator.RegisterDefaults();
        }
        catch (Exception e)
        {
            Console.WriteLine($"Failed to register MSBuild defaults: {e}");
            Environment.Exit(-1);
        }

        var exitCode = 0;
        var options = ParseCliArgs(args);

        if (options.Success)
        {
            var execution = ExecuteInspector(options.Options);
            if (execution.ExitCode != 0) exitCode = execution.ExitCode;
        }
        else
        {
            exitCode = options.ExitCode;
        }

        Environment.Exit(exitCode);
    }

    /// <summary>
    /// Return True if there is an warning in the results.
    /// </summary>
    public static bool Has_warnings(OutputFormatJson output)
    {
        var hasTopLevel = output.ScanResult.Warnings.Any();
        if (hasTopLevel)
            return true;
        var hasPackageLevel =  output.ScanResult.ProjectPackage.Warnings.Any();
        if (hasPackageLevel)
            return true;
        var hasDepLevel = false;
        foreach (var dep in output.ScanOutput.Dependencies)
        {
            if (dep.Warnings.Any())
                hasDepLevel = true;
                break;
        }
        return hasDepLevel;
    }

    /// <summary>
    /// Return True if there is an error in the results.
    /// </summary>
    public static bool Has_errors(OutputFormatJson output)
    {
        var hasTopLevel = output.ScanResult.Errors.Count != 0;
        if (hasTopLevel)
            return true;
        var hasPackageLevel =  output.ScanResult.ProjectPackage.Errors.Count != 0;
        if (hasPackageLevel)
            return true;
        var hasDepLevel = false;
        foreach (var dep in output.ScanOutput.Dependencies)
        {
            if (dep.Errors.Any())
                hasDepLevel = true;
                break;
        }
        return hasDepLevel;
    }

    private static ExecutionResult ExecuteInspector(Options options)
    {
        if (Config.TRACE)
        {
            Console.WriteLine("\nnuget-inspector options:");
            options.Print(4);
        }

        try
        {
            var projectOptions = new ProjectScannerOptions(options);

            var (frameworkWarning, projectFramework) = FrameworkFinder.GetFramework(
                options.TargetFramework,
                options.ProjectFilePath);

            projectOptions.ProjectFramework = projectFramework.GetShortFolderName();

            var nugetApiService = new NugetApi(
                options.NugetConfigPath,
                projectOptions.ProjectDirectory,
                projectFramework,
                options.WithNuGetOrg);

            var scanner = new ProjectScanner(
                projectOptions,
                nugetApiService,
                projectFramework);

            var scanTimer = Stopwatch.StartNew();

            var depsTimer = Stopwatch.StartNew();
            var scanResult = scanner.RunScan();
            depsTimer.Stop();

            var metaTimer = Stopwatch.StartNew();
            scanner.FetchDependenciesMetadata(
                scanResult,
                options.WithDetails);
            metaTimer.Stop();

            scanTimer.Stop();

            if (frameworkWarning != null)
                scanResult.Warnings.Add(frameworkWarning);

            if (Config.TRACE)
            {
                Console.WriteLine("Run summary:");
                Console.WriteLine($"    Dependencies resolved in: {depsTimer.ElapsedMilliseconds} ms.");
                Console.WriteLine($"    Metadata collected in:    {metaTimer.ElapsedMilliseconds} ms.");
                Console.WriteLine($"    Scan completed in:        {scanTimer.ElapsedMilliseconds} ms.");
            }

            var outputFormatter = new OutputFormatJson(scanResult);
            outputFormatter.Write();

            if (Config.TRACE_OUTPUT)
            {
                Console.WriteLine("\n=============JSON OUTPUT================");
                var output = JsonConvert.SerializeObject(
                    outputFormatter.ScanOutput,
                    Formatting.Indented);
                Console.WriteLine(output);

                Console.WriteLine("=======================================\n");
            }

            var projectPackage = scanResult.ProjectPackage;

            var success = scanResult.Status == ScanResult.ResultStatus.Success;

            var withWarnings = Has_warnings(outputFormatter);
            var withErrors = Has_errors(outputFormatter);

            // also consider other errors
            if (success && withErrors)
                success = false;

            if (success)
            {
                Console.WriteLine($"\nScan Result: success: JSON file created at: {scanResult.Options!.OutputFilePath}");
                if (withWarnings)
                    PrintWarnings(scanResult, projectPackage);

                return ExecutionResult.Succeeded();
            }
            else
            {
                Console.WriteLine($"\nScan completed with Errors or Warnings: JSON file created at: {scanResult.Options!.OutputFilePath}");
                if (withWarnings)
                    PrintWarnings(scanResult, projectPackage);
                if (withErrors)
                    PrintErrors(scanResult, projectPackage);

                return ExecutionResult.Failed();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nERROR: scan failed:  {ex}");
            return ExecutionResult.Failed();
        }

        static void PrintWarnings(ScanResult scanResult, BasePackage projectPackage)
        {
            if (scanResult.Warnings.Any())
                Console.WriteLine("    WARNING: " + string.Join(", ", scanResult.Warnings));
            if (scanResult.Errors.Any())
                Console.WriteLine("    ERROR: " + string.Join(", ", scanResult.Errors));

            Console.WriteLine("\n    Errors or Warnings at the package level");
            Console.WriteLine($"       {projectPackage.Name}@{projectPackage.Version} with purl: {projectPackage.Purl}");
            if (projectPackage.Warnings.Any())
                Console.WriteLine("        WARNING: " + string.Join(", ", projectPackage.Warnings));
            if (projectPackage.Errors.Any())
                Console.WriteLine("        ERROR: " + string.Join(", ", projectPackage.Errors));

            Console.WriteLine("\n        Errors or Warnings at the dependencies level");
            foreach (var dep in projectPackage.GetFlatDependencies())
            {
                if (dep.Warnings.Count == 0 && dep.Errors.Count == 0)
                    continue;
                
                Console.WriteLine($"            {dep.Name}@{dep.Version} with purl: {dep.Purl}");
                if (dep.Warnings.Count != 0)
                    Console.WriteLine("            WARNING: " + string.Join(", ", dep.Warnings));
                if (dep.Errors.Count != 0)
                    Console.WriteLine("            ERROR: " + string.Join(", ", dep.Errors));
            }
        }

        static void PrintErrors(ScanResult scanResult, BasePackage projectPackage)
        {
            if (scanResult.Errors.Count != 0)
                Console.WriteLine("\nERROR: " + string.Join(", ", scanResult.Errors));

            if (projectPackage.Errors.Count != 0)
            {
                Console.WriteLine("\nERRORS at the package level:");
                Console.WriteLine($"    {projectPackage.Name}@{projectPackage.Version} with purl: {projectPackage.Purl}");
                Console.WriteLine("    ERROR: " + string.Join(", ", projectPackage.Errors));
            }

            Console.WriteLine("\nERRORS at the dependencies level:");
            foreach (var dep in projectPackage.GetFlatDependencies())
            {
                if (dep.Errors.Count == 0) 
                    continue;
                
                Console.WriteLine($"    ERRORS for dependency: {dep.Name}@{dep.Version} with purl: {dep.Purl}");
                Console.WriteLine("    ERROR: " + string.Join(", ", dep.Errors));
            }
        }
    }

    private static ParsedOptions ParseCliArgs(string[] args)
    {
        try
        {
            var options = Options.ParseArguments(args);

            if (options == null)
            {
                return ParsedOptions.Failed();
            }

            if (string.IsNullOrWhiteSpace(options.ProjectFilePath))
            {
                if (Config.TRACE)
                {
                    Console.WriteLine("Failed to parse options: missing ProjectFilePath");
                }

                return ParsedOptions.Failed();
            }

            if (options.Verbose)
            {
                Config.TRACE = true;
            }
            if (options.Debug)
            {
                Config.TRACE = true;
                Config.TRACE_DEEP = true;
                Config.TRACE_META = true;
                Config.TRACE_NET = true;
                Config.TRACE_OUTPUT = true;
            }

            if (Config.TraceArgs)
                Console.WriteLine($"argument: with-details: {options.WithDetails}");

            return ParsedOptions.Succeeded(options);
        }
        catch (Exception e)
        {
            if (!Config.TRACE )
                return ParsedOptions.Failed();
            
            Console.WriteLine("Failed to parse options.");
            Console.WriteLine(e.Message);
            Console.WriteLine(e.StackTrace);

            return ParsedOptions.Failed();
        }
    }

    private class ParsedOptions
    {
        public int ExitCode;
        public Options Options = null!;
        public bool Success;

        public static ParsedOptions Failed(int exitCode = -1)
        {
            return new ParsedOptions
            {
                ExitCode = exitCode,
                Options = new Options(),
                Success = false
            };
        }

        public static ParsedOptions Succeeded(Options options)
        {
            return new ParsedOptions
            {
                Options = options,
                Success = true
            };
        }
    }

    private class ExecutionResult
    {
        public int ExitCode;
        public bool Success;

        public static ExecutionResult Failed(int exitCode = -1)
        {
            return new ExecutionResult
            {
                Success = false,
                ExitCode = exitCode
            };
        }

        public static ExecutionResult Succeeded()
        {
            return new ExecutionResult
            {
                Success = true
            };
        }
    }
}