using System.Diagnostics;
using Microsoft.Build.Locator;
using Newtonsoft.Json;

namespace NugetInspector;

internal static class Program
{
    public static void Main(string[] args)
    {
        try
        {
            Log.Trace("Registering MSBuild defaults.");
            MSBuildLocator.RegisterDefaults();
        }
        catch (Exception e)
        {
            Log.Info($"Failed to register MSBuild defaults: {e}");
            Environment.Exit(-1);
        }

        var exitCode = 0;
        var options = ParseCliArgs(args);

        if (options.Success)
        {
            var execution = ExecuteInspector(options.Options);
            if (execution.ExitCode != 0)
                exitCode = execution.ExitCode;
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
    public static bool HasWarnings(OutputFormatJson output)
    {
        var hasTopLevel = output.ScanResult.Warnings.Count != 0;
        if (hasTopLevel)
            return true;
        var hasPackageLevel =  output.ScanResult.ProjectPackage.Warnings.Count != 0;
        if (hasPackageLevel)
            return true;
        var hasDepLevel = false;
        foreach (var dep in output.ScanOutput.Dependencies)
        {
            if (dep.Warnings.Count != 0)
                hasDepLevel = true;
            break;
        }
        return hasDepLevel;
    }

    /// <summary>
    /// Return True if there is an error in the results.
    /// </summary>
    public static bool HasErrors(OutputFormatJson output)
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
            if (dep.Errors.Count != 0)
                hasDepLevel = true;
            break;
        }
        return hasDepLevel;
    }

    private static ExecutionResult ExecuteInspector(Options options)
    {
        Log.Trace("\nnuget-inspector options:");
        options.LogTrace(4);

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
            
            Log.Info("## DEPENDENCIES ##");
            foreach( var dep in scanResult.ProjectPackage.Dependencies )
                Log.Info($"{dep.Name} {dep.Type} ");
            
            depsTimer.Stop();

            var metaTimer = Stopwatch.StartNew();
            scanner.FetchDependenciesMetadata(
                scanResult,
                options.WithDetails);
            metaTimer.Stop();

            scanTimer.Stop();

            if (frameworkWarning != null)
                scanResult.Warnings.Add(frameworkWarning);

            Log.Trace("Run summary:");
            Log.Trace($"    Dependencies resolved in: {depsTimer.ElapsedMilliseconds} ms.");
            Log.Trace($"    Metadata collected in:    {metaTimer.ElapsedMilliseconds} ms.");
            Log.Trace($"    Scan completed in:        {scanTimer.ElapsedMilliseconds} ms.");

            var outputFormatter = new OutputFormatJson(scanResult);
            outputFormatter.Write();
            // scanResult.Options.OutputFilePath = $"/home/<username>/tmp/{Guid.NewGuid()}.json";
            // outputFormatter = new OutputFormatJson(scanResult);
            // outputFormatter.Write();

            Log.Trace("\n=============JSON OUTPUT================");
            var output = JsonConvert.SerializeObject(
                outputFormatter.ScanOutput,
                Formatting.Indented);
            Log.Trace(output);
            Log.Trace("=======================================\n");
            
            var projectPackage = scanResult.ProjectPackage;

            var success = scanResult.Status == ScanResult.ResultStatus.Success;

            var withWarnings = HasWarnings(outputFormatter);
            var withErrors = HasErrors(outputFormatter);

            // also consider other errors
            if (success && withErrors)
                success = false;

            if (success)
            {
                Log.Info($"\nScan Result: success: JSON file created at: {scanResult.Options!.OutputFilePath}");
                if (withWarnings)
                    PrintWarnings(scanResult, projectPackage);

                return ExecutionResult.Succeeded();
            }

            Log.Info($"\nScan completed with Errors or Warnings: JSON file created at: {scanResult.Options!.OutputFilePath}");
            
            if (withWarnings)
                PrintWarnings(scanResult, projectPackage);
            if (withErrors)
                PrintErrors(scanResult, projectPackage);

            return ExecutionResult.Failed();
        }
        catch (Exception ex)
        {
            Log.Info($"\nERROR: scan failed:  {ex}");
            return ExecutionResult.Failed();
        }
    }

    private static void PrintErrors(ScanResult scanResult, BasePackage projectPackage)
    {
        if (scanResult.Errors.Count != 0)
            Log.Info("\nERROR: " + string.Join(", ", scanResult.Errors));

        if (projectPackage.Errors.Count != 0)
        {
            Log.Info("\nERRORS at the package level:");
            Log.Info($"    {projectPackage.Name}@{projectPackage.Version} with purl: {projectPackage.Purl}");
            Log.Info("    ERROR: " + string.Join(", ", projectPackage.Errors));
        }

        Log.Info("\nERRORS at the dependencies level:");
        foreach (var dep in projectPackage.GetFlatDependencies())
        {
            if (dep.Errors.Count == 0) 
                continue;
                
            Log.Info($"    ERRORS for dependency: {dep.Name}@{dep.Version} with purl: {dep.Purl}");
            Log.Info("    ERROR: " + string.Join(", ", dep.Errors));
        }
    }

    private static void PrintWarnings(ScanResult scanResult, BasePackage projectPackage)
    {
        if (scanResult.Warnings.Count != 0)
            Log.Info("    WARNING: " + string.Join(", ", scanResult.Warnings));
        if (scanResult.Errors.Count != 0)
            Log.Info("    ERROR: " + string.Join(", ", scanResult.Errors));

        Log.Info("\n    Errors or Warnings at the package level");
        Log.Info($"       {projectPackage.Name}@{projectPackage.Version} with purl: {projectPackage.Purl}");
        if (projectPackage.Warnings.Count != 0)
            Log.Info("        WARNING: " + string.Join(", ", projectPackage.Warnings));
        if (projectPackage.Errors.Count != 0)
            Log.Info("        ERROR: " + string.Join(", ", projectPackage.Errors));

        Log.Info("\n        Errors or Warnings at the dependencies level");
        foreach (var dep in projectPackage.GetFlatDependencies())
        {
            if (dep.Warnings.Count == 0 && dep.Errors.Count == 0)
                continue;
                
            Log.Info($"            {dep.Name}@{dep.Version} with purl: {dep.Purl}");
            if (dep.Warnings.Count != 0)
                Log.Info("            WARNING: " + string.Join(", ", dep.Warnings));
            if (dep.Errors.Count != 0)
                Log.Info("            ERROR: " + string.Join(", ", dep.Errors));
        }
    }

    private static ParsedOptions ParseCliArgs(string[] args)
    {
        try
        {
            var options = Options.ParseArguments(args);

            if (options == null)
                return ParsedOptions.Failed();

            if (string.IsNullOrWhiteSpace(options.ProjectFilePath))
            {
                Log.Trace("Failed to parse options: missing ProjectFilePath");
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

            Log.TraceArgs($"argument: with-details: {options.WithDetails}");

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