# Host a client

Your application is the host. It chooses the UI, storage paths, authentication interaction and optional modules. DMCBK provides the client services. UMPK provides the Minecraft protocol and game engine.

For a complete application, follow [Build a client](client/index.md). This page summarizes the decisions a host must make.

## Choose a host type

| Host | Responsibilities |
| --- | --- |
| Console or worker | Process lifetime, logs, command output and account interaction |
| Desktop UI | UI dispatch, windows, dialogs and application shutdown |
| Web backend | HTTP authorization, per-user ownership and the Minecraft connection |
| Mobile UI | Device storage, suspend/resume and platform-compatible modules |

A host can use Core without Commands, Beacon or Plugins. Do not add UI framework dependencies to Core.

## Compose modules explicitly

Module factories execute in registration order. Commands must precede Beacon and Plugins. Marketplace integration needs the plugin installation host.

See [Chapter 5](client/05-modules.md#add-plugins-and-a-marketplace) for a complete composition example. Package installation and module composition are separate steps.

Each client owns its module instances. Module disposal runs in reverse registration order. A supplied logger factory or HTTP client remains host-owned.

`HostApplication` records application identity, version and host capabilities. These values let plugin compatibility checks distinguish your application from other DMCBK hosts.

## Own presentation

`IHostInterface` supplies optional version prompts, authentication interaction, command output, resource-pack prompts and rich UI services.

The default `NullHostInterface` has no interaction. Offline clients can still connect. An interactive online login needs a host that implements `IAuthInteraction`.

Commands return a `CmdResult` and can write longer body text to `ICommandOutput`. Display both. Use `DispatchCapturedAsync` for a sequential request/response operation.

A host can implement `IHostUi` for images, inventory views, books, dialogs and manuals. Report unsupported views through their documented return values. Keep framework objects within your application.

See [configuration and host interaction](client/04-host.md) for practical examples.

## Select storage

1. Choose an application data root.
2. Keep account and token files private.
3. Select a configuration directory explicitly.
4. Select plugin and marketplace paths explicitly.
5. Keep user data outside immutable package directories.

Core accepts typed options without generating files. Configuration loading can generate defaults only when requested. Inspect loader warnings before connecting.

A separate client should normally use a separate storage root. Sharing a plugin installation root requires the marketplace's installation locking rules.

## Treat sessions as replaceable

`Client` owns connection management. A session owns one Minecraft connection. A reconnect replaces that connection.

Subscribe to stable client events before connecting. Refresh session event subscriptions after reconnect. Cancel work bound to an old session.

Use tokens for waits and game actions. Call `StopAsync` during host shutdown. Dispose the client after application work ends.

See [Chapter 6](client/06-lifecycle.md) for reconnect policies and shutdown order.

## Logging and errors

The default logger factory discards diagnostics. Supply a factory through `UseLoggerFactory` when your application needs logs.

Report failures using the actual exception, command result or action outcome. Do not display success just because a request did not throw.

Do not block a packet callback with slow network or disk work. Move that work to an application-owned queue. Dispose subscriptions and workers when their owner ends.

## Web, desktop and mobile limits

The [web backend](../samples/WebBackend/README.md) owns the Minecraft TCP connection. A browser communicates with that backend. DMCBK does not include a browser transport relay.

The backend sample is for local development. It has no HTTP caller authentication. A public deployment needs authorization and isolated ownership of each client's operations.

Desktop and mobile hosts implement presentation and authentication for their selected framework. UI updates must use that framework's dispatcher.

Dynamic assembly loading and Roslyn compilation depend on the target platform. Current plugin loading does not support mandatory Native AOT unchanged. Test modules on the actual device.

See [test and distribute a client](client/07-test-and-deploy.md) for publish commands and validation limits.
