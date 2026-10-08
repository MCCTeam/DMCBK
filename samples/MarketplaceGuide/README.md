# Marketplace verification sample

This program packs a real source plugin, resolves an exact version and installs it through the transaction engine. It runs the plugin in an in-memory Minecraft protocol session. It also checks pinning and uninstall.

The custom HTTP handler serves the generated archive from disk. The example URL never receives a network request. This sample checks the installer and runtime. It does not check a public marketplace or a live Minecraft server.

## Run the sample

1. Restore DMCBK `0.1.0-preview.2` from your configured NuGet sources.
2. Run the program from the repository root.

```bash
dotnet run --project samples/MarketplaceGuide -c Release
```

The program prints one `PASS` line. A failed assertion exits with an exception. The program creates temporary package files and removes them before it exits.

For unpublished packages, use the local feed procedure in [installation](../../docs/getting-started/installation.md). Use a fresh package cache when you replace packages at the same preview version.
