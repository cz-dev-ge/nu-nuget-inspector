# nu-nuget-inspector

Fork of aboutcode-org's nuget-inspector.

Fixes some issues of the original one as my PRs where not merged but we need them.

## AI Contributions

Parts of the code have been co-programmed with so-called generative AI.

## Cross-platform support

nu-nuget-inspector is a cross-platform tool that runs on Linux and Windows.

### Cross-platform known-issues

Projects that have been built using Windows cannot be scanned under Linux and vice versa.
This is a limitation due to windows and linux style paths in the scanned project.assets.json
files. When using ORT, run the Analyzer step which uses nu-nuget-inspector on the same
platform as the project was built on.

## NuGet feed authentication

`nuget-inspector` uses `nuget.config` to discover package sources, the same as
the `dotnet`/`nuget` CLIs. If a private feed requires credentials, you don't
need to hardcode them in a checked-in `nuget.config`: NuGet.Client natively
expands `%ENV_VAR%` placeholders found in the `packageSourceCredentials`
section against environment variables at runtime. This works out of the box
on Linux, Windows and macOS, and lets different feeds use different
credentials, e.g.:

```xml
<configuration>
  <packageSources>
    <add key="FeedA" value="https://feedA.example.com/nuget/v3/index.json" />
    <add key="FeedB" value="https://feedB.example.com/nuget/v3/index.json" />
  </packageSources>
  <packageSourceCredentials>
    <FeedA>
      <add key="Username" value="%FEEDA_USER%" />
      <add key="ClearTextPassword" value="%FEEDA_PASS%" />
    </FeedA>
    <FeedB>
      <add key="Username" value="%FEEDB_USER%" />
      <add key="ClearTextPassword" value="%FEEDB_PASS%" />
    </FeedB>
  </packageSourceCredentials>
</configuration>
```

Set `FEEDA_USER`/`FEEDA_PASS` and `FEEDB_USER`/`FEEDB_PASS` in the environment
(e.g. as CI secrets) and each feed will be authenticated independently.


# Original README (but converted from RST to MD)

## nuget-inspector - inspect nuget and .NET projects packages dependencies

Homepage: [https://github.com/nexB/nuget-inspector](https://github.com/nexB/nuget-inspector) and [https://www.aboutcode.org/](https://www.aboutcode.org/)


`nuget-inspector` is a utility to:

- resolve .NET project nuget packages dependencies

- parse various project and package manifests and lockfiles such as .csproj files,
  and several related formats (including legacy formats)

- query NuGet.org APIs for package information to support dependency resolution

It grew out of the need to have a reliable way to analyze .NET code projects and
their dependencies independently of the availability of a dotnet SDK installed
on the machine that runs this analysis; and that could run on Linux, Windows and
macOS.

The goal of nuget-inspector is to be a comprehensive tool that can handle every
style of .NET and NuGet projects and package layouts, manifests and lockfiles.


    WARNING! this tool is under heavy development and its CLI options and output
    format are evolving quickly.


### Usage

- Install the dotnet SDK 6.x for your platform from Microsoft
  https://learn.microsoft.com/en-us/dotnet/core/install/

- Download and extract the pre-built binary release archive from the release page
  https://github.com/nexB/nuget-inspector for your operating system. (Linux-only
  for now)

- Run the command line utility with::

    nuget-inspector --help

For instance, you can fetch nuget-inspector own project file at::

    https://raw.githubusercontent.com/nexB/nuget-inspector/main/src/nuget-inspector/nuget-inspector.csproj

And then run::

    nuget-inspector --project-file nuget-inspector.csproj --json nuget-inspector.json

And review the ``nuget-inspector.json`` JSON output file with its resolved dependencies.
Note that the output data structure is evolving and not final.



### License

Copyright (c) 2026 Carl Zeiss AG and others.

Copyright (c) nexB Inc. and others.

Copyright (c) the .NET Foundation, Microsoft and others.

Portions Copyright (c) 2018 Black Duck Software, Inc.

Portions Copyright (c) Mario Rivis https://github.com/dxworks

Portions Copyright (c) 2016 Andrei Marukovich https://github.com/Dropcraft/Dropcraft

SPDX-License-Identifier: Apache-2.0 AND MIT


This project is based on, depends on or embeds several fine libraries and tools.
Here are credits for some of these key projects without which it would not exist:

- `NuGet.Client`, `MSBuild` and `upgrade-assistant` from the .NET
  + Foundation which are the core .NET tools and libraries to handled .NET and
  + NuGet projects.
  + https://github.com/NuGet/NuGet.Client/
  + https://github.com/dotnet/msbuild/
  + https://github.com/dotnet/upgrade-assistant

- `nuget-dotnet5-inspector` from Synopsys as forked by Mario Rivis
  + https://github.com/dxworks/nuget-dotnet5-inspector

- `audit.net` `NugetAuditor` and `DevAudit` from Sonatype
  + https://github.com/sonatype-nexus-community/DevAudit/
  + https://github.com/sonatype-nexus-community/audit.net

- `build-info` and `nuget-deps-tree` from JFrog
  + https://github.com/jfrog/build-info
  + https://github.com/jfrog/nuget-deps-tree/

- `Component Detection` and `OSSGadget` from Microsoft
  + https://github.com/microsoft/component-detection/
  + https://github.com/microsoft/OSSGadget

- `cyclonedx-dotnet` from the OWASP Foundation
  + https://github.com/CycloneDX/cyclonedx-dotnet

- `DependencyCheck` from Jeremy Long
  + https://github.com/jeremylong/DependencyCheck

- `DependencyChecker` from Fabrice Andréïs
  + https://github.com/chwebdude/DependencyChecker

- `dotnet-oudated` from Jerrie Pelser and contributors
  + https://github.com/dotnet-outdated/dotnet-outdated

- `NugetDefense` from Curtis Carter
  + https://github.com/digitalcoyote/NuGetDefense

- `snyk-nuget-plugin` and `dotnet-deps-parser` from Snyk
  + https://github.com/snyk/snyk-nuget-plugin
  + https://github.com/snyk/dotnet-deps-parser

- `verademo-dotnet` and `verademo-dotnetcore` and from Veracode
  + https://github.com/veracode/verademo-dotnet
  + https://github.com/veracode/verademo-dotnetcore

- `dropcraft` from Andrei Marukovich
  + https://github.com/Dropcraft/Dropcraft

These projects are used either in the built executables, at build time or for
testing (a large number are used for testing). The built executables are designed
to be self-contained exes that do not require additional libraries to run on the
target system, beyond a dotnet SDK.
