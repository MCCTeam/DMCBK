# Install DMCBK

DMCBK targets .NET 10. This repository pins SDK `10.0.401`. Its UMPK packages use `0.9.0-beta.6`.

A package contains compiled library code. NuGet restores that code and its dependencies. You need the .NET SDK to compile your application.

## Create a consumer project

1. Install the .NET 10 SDK.
2. Check the SDK with `dotnet --version`.
3. Create a console project.
4. Add the DMCBK package.

```bash
dotnet new console --framework net10.0 --name MyClient
cd MyClient
dotnet add package DMCBK --version 0.1.0-preview.4
```

The convenience package includes Core, Commands and Configuration. Beacon, plugin activation and marketplace installation require separate packages.

Installing a package does not attach its optional service. Your `ClientBuilder` attaches modules with `UseCommands`, `UseBeacon` and related extensions.

Use the [chaptered client guide](../client/index.md) for the complete procedure. The [first client](first-client.md) gives a shorter connection example. See [package responsibilities](../reference/packages.md) before adding more dependencies.

## Build this repository

1. Clone the repository.
2. Open its directory.
3. Restore dependencies.
4. Build the solution.
5. Run unit tests.

```bash
git clone --recurse-submodules https://github.com/MCCTeam/DMCBK.git
cd DMCBK
dotnet restore DMCBK.slnx
dotnet build DMCBK.slnx -c Release --no-restore
dotnet test DMCBK.slnx -c Release --no-build
```

The clone includes MCC Skills for agent guidance. Library dependencies restore through NuGet. In an existing checkout, run `git submodule update --init MCC-Skills` to prepare the shared skills. See [agent skills](../agent-skills.md) for symbolic links and Windows setup.

The final command uses `--no-build` because the previous command already built the test assembly. Do not use it after changing source without another build.

## Use local packages

Use a local feed before a preview is available from your normal NuGet source. A feed can be an ordinary directory containing `.nupkg` files.

1. Pack the repository.
2. Choose the package directory.
3. Add that directory to the consumer's NuGet sources.
4. Restore the consumer.
5. Build the consumer.

From the DMCBK repository:

```bash
dotnet pack DMCBK.slnx -c Release -o artifacts/packages
```

For the included samples:

```bash
dotnet restore samples/ClientGuide --configfile NuGet.Local.Config
dotnet run --project samples/ClientGuide --no-restore -- --self-test
```

`NuGet.Local.Config` adds `artifacts/packages` beside the repository's regular NuGet.org source. The regular source provides UMPK and other public dependencies.

An independent consumer can use a file like this:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local-dmcbk" value="/absolute/path/to/DMCBK/artifacts/packages" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
```

Replace the local path with your actual package directory. Windows paths can use the drive and directory path. Relative paths resolve from the NuGet configuration file.

Save the file as `NuGet.Local.Config` in the consumer. Then run:

```bash
dotnet restore --configfile NuGet.Local.Config
dotnet build -c Release --no-restore
```

## Avoid stale preview packages

NuGet caches packages by ID and version. Replacing a local `0.1.0-preview.4` archive does not guarantee that a restore reads the new archive.

Use a fresh cache for a local validation run. Choose a new directory for each replaced package set.

POSIX shell:

```bash
export NUGET_PACKAGES=/tmp/dmcbk-consumer-cache-new
dotnet restore --configfile NuGet.Local.Config
```

PowerShell:

```powershell
$env:NUGET_PACKAGES = Join-Path $env:TEMP "dmcbk-consumer-cache-new"
dotnet restore --configfile NuGet.Local.Config
```

A release should use a new package version instead of replacing published archives.

## Check common failures

| Failure | Likely cause | Action |
| --- | --- | --- |
| Package not found | The selected feed lacks the preview. | Use the local feed or check the published version. |
| SDK cannot target .NET 10 | The computer has only an older SDK or runtime. | Install the .NET 10 SDK. |
| Sample uses an old API | The same preview version exists in a package cache. | Restore with a fresh cache. |
| Shared skill links do not open | The submodule is uninitialized or Git did not create symbolic links. | Initialize MCC Skills and follow the [skill setup](../agent-skills.md#use-skills-in-this-checkout). |
| UMPK restore fails | Public NuGet access is unavailable. | Check sources and network access. |
