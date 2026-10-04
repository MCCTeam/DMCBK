# Installation

DMCBK targets .NET 10. The repository pins SDK `10.0.401` in `global.json`. UMPK components come from NuGet at version `0.9.0-beta.4`.

## Install packages

1. Create a .NET console project.
2. Add the DMCBK package.
3. Replace `Program.cs` with the [first client example](first-client.md).

```bash
dotnet new console --framework net10.0 --name MyClient
cd MyClient
dotnet add package DMCBK --version 0.1.0-preview.1
```

`DMCBK` includes Core, Commands and Configuration. Beacon, plugin loading and marketplace installation need their own packages. See the [package reference](../reference/packages.md).

## Build the repository

1. Clone the repository.
2. Open the repository directory.
3. Restore dependencies.
4. Build the solution.
5. Run unit tests.

```bash
git clone https://github.com/MCCTeam/DMCBK.git
cd DMCBK
dotnet restore DMCBK.slnx
dotnet build DMCBK.slnx -c Release --no-restore
dotnet test DMCBK.slnx -c Release --no-build
```

Repository access currently requires permission because DMCBK is private. The build does not require UMPK or MCC source checkouts.

## Use local packages

Use this procedure before the preview reaches NuGet.org.

1. Build the DMCBK package set.
2. Copy the `.nupkg` files into the consumer's `artifacts/packages` directory.
3. Set a new `NUGET_PACKAGES` directory when you replace the same preview version.
4. Restore the consumer with `NuGet.Local.Config`.
5. Build the consumer without another restore.

```bash
# In the DMCBK repository:
dotnet pack DMCBK.slnx -c Release -o artifacts/packages

# In a consumer repository with NuGet.Local.Config:
dotnet restore --configfile NuGet.Local.Config
dotnet build -c Release --no-restore
```

For the included samples, keep the package files in DMCBK's `artifacts/packages` directory.

```bash
dotnet restore samples/HeadlessClient --configfile NuGet.Local.Config
dotnet run --project samples/HeadlessClient --no-restore -- TestBot localhost:25565 auto
```

The default `NuGet.Config` uses NuGet.org. The separate local configuration adds `artifacts/packages`. NuGet can reuse an older package from its cache even when you replace the archive. A new cache avoids that problem during preview development.
