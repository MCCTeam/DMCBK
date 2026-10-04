# Writing a plugin

A plugin targets DMCBK.PluginSdk API 1.0 and carries a schema-2 manifest. Plugin authors restore DMCBK and the UMPK components they use from NuGet. MCC source, submodules and host UI frameworks are not needed.

## Start with a template

DMCBK-Plugins provides source-only, compiled-only and mixed templates. In a running host, `plugins new my-plugin` creates a local source scaffold with settings, localization and a manual page. The scaffold starts disabled in host state; its immutable manifest has no enabled flag.

The source manifest is:

```toml
schema-version = 2
id = "hello"
version = "1.0.0"
kind = "source"
target = "any"
entry = "Hello.cs"
framework = "net10.0"
api-version = "1.0"
dmcbk = ">=0.1.0-preview.1 <0.2.0"
umpk = ">=0.9.0-beta.4 <0.10.0"
needs = ["commands"]
man = ["hello"]
```

Use a lowercase plugin ID that is one safe path segment. Versions use SemVer. `api-version` checks the contract major and minimum minor; `dmcbk` and `umpk` constrain the actual library versions. Add a `[hosts]` table only when application-specific behavior is required, for example `mcc = ">=2.0.0 <3.0.0"`. `needs` names capabilities actually provided by the host.

## Entry and lifecycle

```csharp
using System.Threading.Tasks;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging;

public sealed class Hello : IPlugin
{
    public void Configure(PluginDescriptor descriptor)
    {
        descriptor.Id = "hello";
        descriptor.Version = "1.0.0";
        descriptor.ApiVersion = PluginApiVersion.Major;
    }

    public Task ActivateAsync(PluginContext context)
    {
        context.SessionStarted += (_, args) =>
            context.Logger.LogInformation("{Message}", context.Strings.Get("joined"));
        return Task.CompletedTask;
    }
}
```

Place `joined = "Hello plugin joined a session."` in `lang/en.toml`. The logger receives localized text, and translations can provide the same key in another language file.

`Configure` declares identity and an optional settings type without I/O or game actions. The host loads and validates settings before `ActivateAsync`. Activation runs once for the plugin lifetime; it must not assume a live connection. `SessionCreated` exposes pre-play setup. `SessionStarted` supplies a fresh `ISessionScope` for each connection and reconnect. `SessionEnded` ends that scope. `BeforeConnect`, `ConfigurationReloaded` and `BeforeExit` cover host lifecycle hooks.

`DeactivateAsync(CancellationToken)` has a default implementation. Override it to stop resources created by your plugin, such as network clients, timers or background loops. Honor cancellation; the runtime bounds teardown time and keeps failures isolated. Session subscriptions, command registrations, channels, scheduler work and movement leases owned by host scopes are released on session end or plugin unload.

## Session work

Use `context.Game` for typed game reads and actions while connected. `context.CurrentSession` can be null; `context.Session` requires a live session. Never retain a session scope across reconnects.

The scope provides events, actions, tracked state, scheduling, commands, plugin channels, packet observation, negotiated action capabilities and a `Detached` cancellation token. Acquire exclusive movement through `TryAcquireMovement` and dispose the lease when done. Prefer scoped scheduler work over independent threads. Packet callbacks run on packet flow paths and must remain cheap; copy payloads before retaining them.

Register commands through `context.Commands` for plugin-lifetime commands or `session.Commands` for connection-lifetime commands. These scopes unregister their commands on disposal. Internal dispatch and completion continue to use Brigadier.

## Settings, storage and presentation

Declare typed settings with `descriptor.WithSettings<T>()`. `context.Settings.Load<T>()` reads them and `Save` persists user changes. Package `settings.toml` supplies defaults; updates overlay those defaults without overwriting user values.

`context.Storage` supplies the plugin's persistent data directory, relative path resolution and a small key/value store. Installed settings and data live under `userdata/<id>`, outside immutable version directories. Do not write inside the package or source compilation cache.

Route user text through `context.Strings.Get` or `Format`, with keys in `lang/<language>.toml`. Manuals live under `man/en/<topic>.md` and are declared in `man`. Manual registrations belong to the current client and leave when the plugin unloads. `context.Client.Manuals` can read that client's catalogue.

Host presentation is optional. Use generic notification, image, inventory, book and dialog hooks when appropriate. Map processing and RGB image export can run in a plugin; terminal dimensions, ANSI drawing and UI controls belong to the host. A headless client can decline a presentation request.

## Communication and Beacon

`context.Messenger` provides plugin messaging, and `context.Services` publishes and consumes typed services. Declare required providers with ranges:

```toml
[requires]
shared-tools = "^2.1.0"

[optional]
alerts = "^3.0.0"

[exports]
assemblies = ["Hello.Contracts.dll"]
```

Required dependencies must be installed and compatible. Optional dependencies are not fetched automatically, and incompatible installed versions are exposed as unavailable. Required dependency cycles are rejected. Exported assemblies carry public contract types shared through the provider's load context; private dependencies stay isolated.

`context.Beacon` offers functions, variables, custom events and calls to exported script functions. Declare the Beacon capability when it is required. Runtime registrations are withdrawn on unload or reload. The host crash budget covers plugin callbacks, commands and scheduled work so repeated failures can disable a plugin without terminating other plugins.

`context.Host` exposes `ApiVersion`, `DmcbkVersion`, `UmpkVersion`, `ApplicationId`, `ApplicationVersion`, `RuntimeTarget` and `AvailableCapabilities`. These describe the client process. Server protocol capabilities are separate session information.

Source compilation defines `DMCBK_API_1_0` and `DMCBK_API_1_0_OR_GREATER`, with symbols for supported minor versions. Compiled plugins must be rebuilt against the new DMCBK SDK. Roslyn belongs to the runtime package, not the author contract package.

## Compiled and native packages

A compiled manifest uses `kind = "compiled"`, an assembly entry and its target. Pure managed plugins normally use `target = "any"`; native plugins publish matching Windows, Linux/glibc, Linux/musl or macOS assets. Architecture detection follows the running process, including Windows x86.

List private managed assembly paths in `deps`. Include the compiled entry's `.deps.json` and native dependencies when using native loading. Do not include DMCBK, UMPK or host framework assemblies: those identities are shared explicitly. Exported provider contracts are also shared explicitly. Other private dependencies can use different versions in different plugins.

Runtime source installation compiles one entry `.cs` file with declared helper assemblies and explicit references. It never restores arbitrary projects or NuGet dependencies. Build complex projects in the author repository and publish compiled assets.

## Marketplace releases

A marketplace index names each plugin and its release catalogue. The catalogue keeps historical versions and one or more immutable assets per release, with target, kind, URL and SHA-256. The archive manifest must agree with its catalogue release and selected asset.

Declare source, compiled or both assets in `release.toml` in DMCBK-Plugins. `PluginPack` uses shared DMCBK models to generate deterministic archives, checksums and catalogue fragments. Publish archives first, verify their public hashes, then merge the catalogue update. Use a new version whenever a published payload or compatibility declaration changes. Yank bad releases rather than deleting their history.

Installation selects one asset per changed plugin in the resolved graph. Exact compiled targets are preferred, then portable managed assets. Source-only releases compile automatically; a mixed release requires explicit source fallback permission when its compiled assets do not match.

## Testing

Reference DMCBK.Testing from a test project. `PluginTestHost` loads real plugins and creates an in-memory protocol session through published UMPK transport APIs. Exercise session events, reconnect cleanup, commands, settings, dependencies and unload. `ScriptTestHost` supplies deterministic script reads and action capture.

For native plugins, execution tests must run on matching OS and process architecture. Cross-build validation alone cannot prove that a native library loads. Run the official repository's CI and packaging checks before creating an annotated plugin release tag.
