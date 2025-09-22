using System.Reflection;
using Mono.Options;

namespace NugetInspector;

public class Options
{
    [CommandLineArg(
        "project-file",
        "Path to a .NET project file.")]
    public string ProjectFilePath = "";

    [CommandLineArg(
        "target-framework",
        "Optional .NET Target framework. Defaults to the first project target framework. " +
            "See https://learn.microsoft.com/en-us/dotnet/standard/frameworks for values")]
    public string TargetFramework = "";

    [CommandLineArg(
        "json",
        "JSON output file path.")]
    public string OutputFilePath = "";

    [CommandLineArg(
        "nuget-config",
        "Path to a nuget.config file to use, ignoring all other nuget.config.")]
    public string NugetConfigPath = "";

    // If True, return extra metadata details when available such as SHA512
    public bool WithDetails;
    public bool WithFallback;
    public bool WithNuGetOrg;
    public bool ShowHelp;
    public bool Verbose;
    public bool Debug;
    public bool ShowVersion;
    public bool ShowAbout;

    /// <summary>
    /// Print the values of this options object to the console.
    /// </summary>
    public void Print(int indent=0)
    {
        string margin = new (' ', indent);
        foreach (var opt in AsCliList())
            Console.WriteLine($"{margin}{opt}");
    }

    /// <summary>
    /// Return a list of command line-like option values to display in the output.
    /// </summary>
    public List<string> AsCliList()
    {
        List<string> options =
        [
            $"--project-file {ProjectFilePath}",
            $"--json {OutputFilePath}"
        ];

        if (!string.IsNullOrWhiteSpace(TargetFramework))
            options.Add($"--target-framework {TargetFramework}");

        if (NugetConfigPath != "")
            options.Add($"--nuget-config {NugetConfigPath}");

        if (Verbose)
            options.Add("--verbose");

        if (Debug)
            options.Add("--debug");

        if (WithDetails)
            options.Add("--with-details");

        if (WithFallback)
            options.Add("--with-fallback");

        if (WithNuGetOrg)
            options.Add("--with-nuget-org");

        return options;
    }

    public static Options? ParseArguments(string[] args)
    {
        var options = new Options();
        var commandOptions = new OptionSet();

        foreach (var field in typeof(Options).GetFields())
        {
            if (Config.TRACE_ARGS) Console.WriteLine($"ParseArguments.field: {field}");
            var attr = GetAttr<CommandLineArgAttribute>(field);
            if (attr != null)
            {
                commandOptions.Add(
                    $"{attr.Key}=",
                    attr.Description,
                    value => field.SetValue(options, value));
            }
        }

        commandOptions.Add("with-details", "Optionally include package metadata details (such as checksum and size) when available.",
            value => options.WithDetails = value != null);

        commandOptions.Add("with-fallback", "Optionally use a plain XML project file parser as fallback from failures.",
            value => options.WithDetails = value != null);

        commandOptions.Add("with-nuget-org", "Optionally use the official, public nuget.org API as a fallback in addition to nuget.config-configured API sources.",
            value => options.WithNuGetOrg = value != null);

        commandOptions.Add("h|help", "Show this message and exit.",
            value => options.ShowHelp = value != null);
        commandOptions.Add("v|verbose", "Display more verbose output.",
            value => options.Verbose = value != null);
        commandOptions.Add("debug", "Display very verbose debug output.",
            value => options.Debug = value != null);
        commandOptions.Add("version", "Display nuget-inspector version and exit.",
            value => options.ShowVersion = value != null);
        commandOptions.Add("about", "Display information about nuget-inspector and exit.",
            value => options.ShowAbout = value != null);

        try
        {
            commandOptions.Parse(args);
        }
        catch (OptionException)
        {
            ShowHelpMessage(
                "Error: Unexpected extra argument or option. Usage is: nuget-inspector [OPTIONS]",
                commandOptions);
            return null;
        }

        if (options.ShowHelp)
        {
            ShowHelpMessage(
                "Usage: nuget-inspector [OPTIONS]",
                commandOptions);
            return null;
        }

        if (options.ShowVersion)
        {
            Console.Error.WriteLine(Config.NUGET_INSPECTOR_VERSION);
                return null;
        }
        if (options.ShowAbout)
        {
            Console.Error.WriteLine(
                $"nuget-inspector v{Config.NUGET_INSPECTOR_VERSION}\n"
                + "Inspect .NET and NuGet projects and package manifests. Resolve NuGet dependencies.\n"
                + "SPDX-License-Identifier: Apache-2.0 AND MIT\n"
                + "Copyright (c) nexB Inc. and others.\n"
                + "https://github.com/nexB/nuget-inspector");
            return null;
        }

        if (string.IsNullOrWhiteSpace(options.ProjectFilePath))
        {
            ShowHelpMessage(
                "Error: missing required --project-file option. Usage: nuget-inspector [OPTIONS]",
                commandOptions);
            return null;
        }

        if (string.IsNullOrWhiteSpace(options.OutputFilePath))
        {
            ShowHelpMessage(
                "Error: missing required --json option. Usage: nuget-inspector [OPTIONS]",
                commandOptions);
            return null;
        }

        return options;
    }

    private static void ShowHelpMessage(string message, OptionSet optionSet)
    {
        Console.Error.WriteLine(message);
        optionSet.WriteOptionDescriptions(Console.Error);
    }

    private static T? GetAttr<T>(FieldInfo field) where T : class
    {
        var attrs = field.GetCustomAttributes(typeof(T), false);
        if (attrs.Length > 0) return attrs[0] as T;
        return null;
    }
}

internal class CommandLineArgAttribute(string key, string description = "") : Attribute
{
    public readonly string Description = description;
    public readonly string Key = key;
}