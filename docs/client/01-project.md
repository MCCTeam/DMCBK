# Chapter 1: Prepare the project

By the end of this chapter, you have a console project with fixed package versions. You can build it without a checkout of MCC or UMPK.

## Choose the application type

A console project is a useful first host. It exposes each operation directly. You can move the same client code into a worker, desktop application or web backend later.

The host owns the UI. DMCBK does not open a terminal, window or login screen by itself.

## Install the SDK

1. Install the .NET 10 SDK.
2. Open a terminal.
3. Run the following command.

```bash
dotnet --version
```

The DMCBK repository uses SDK `10.0.401`. A runtime installation alone cannot compile this guide.

## Create the project

1. Select a directory for your project.
2. Run the following commands.

```bash
dotnet new console --framework net10.0 --name GuideClient
cd GuideClient
dotnet add package DMCBK.Core --version 0.1.0-preview.7
```

The generated `GuideClient.csproj` describes the application. `Program.cs` contains its entry point. NuGet restores DMCBK.Core and its UMPK dependencies.

If the preview package is not available from your configured feed, follow [local package installation](../getting-started/installation.md#use-local-packages). Repository access and package access are separate permissions.

## Select only the packages you need

| Package | Add it when |
| --- | --- |
| DMCBK.Core | You need connection management and game APIs. |
| DMCBK.Commands | You need local commands or completion. |
| DMCBK.Configuration | You need the TOML loader and persistence. |
| DMCBK.Beacon | You need Beacon scripts. |
| DMCBK.Plugins | You need plugin discovery and activation. |
| DMCBK.Marketplace | You need catalogue resolution and installation. |
| DMCBK.Testing | You need the in-memory test host. |

The `DMCBK` convenience package includes Core, Commands and Configuration. Installing a package does not attach its module to a client. The builder performs that step.

## Understand project files

A minimal project outside this repository looks like this:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="DMCBK.Core" Version="0.1.0-preview.7" />
  </ItemGroup>
</Project>
```

Examples in this repository omit package versions from each project. The repository declares them in `Directory.Packages.props`. An independent project must declare versions itself or provide its own central version file.

## Check the project

1. Run the build command.
2. Check that the build succeeds.

```bash
dotnet build
```

A missing-package error usually means the feed does not contain the selected preview. A target-framework error usually means the installed SDK is too old.

Continue to [Chapter 2](02-connect.md).
