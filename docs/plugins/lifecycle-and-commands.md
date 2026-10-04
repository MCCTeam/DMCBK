# Lifecycle, sessions and commands

A plugin lifetime can contain many connection sessions. Resources need an owner at the same lifetime as their use.

## Lifecycle order

| Hook | When to use it |
| --- | --- |
| `Configure` | Declare identity and a settings type without I/O |
| `ActivateAsync` | Register plugin-lifetime behavior, even when disconnected |
| `BeforeConnect` | Inspect, redirect or veto the connection plan |
| `SessionCreated` | Observe handshake, login and configuration before play |
| `SessionStarted` | Attach work to the new play session |
| `SessionEnded` | Forget session-specific state |
| `ConfigurationReloaded` | Read the supplied configuration snapshot |
| `BeforeExit` | Perform bounded shutdown work while the session may still exist |
| `DeactivateAsync` | Release resources created by the plugin |

A plugin loaded during an active session can receive a session-start notification. It cannot assume it observed that session's creation.

`context.CurrentSession` can be null. `context.Session` requires a session. Do not cache either across reconnects.

Configuration reload supplies a new snapshot. It does not mutate the client's original immutable configuration object.

## Session scope

An `ISessionScope` provides the UMPK client, state, events, actions, scheduler, commands, channels and cancellation.

| Resource | Ownership rule |
| --- | --- |
| Plugin command | Register through `context.Commands` |
| Session command | Register through `session.Commands` |
| Packet observer | Register through `session.ObservePackets` |
| Plugin channel | Register through the session's channel APIs |
| Movement | Acquire through `TryAcquireMovement`, then dispose the lease |
| Session work | Honor `session.Detached` |
| Plugin-created task or socket | Stop and dispose it in `DeactivateAsync` |

Host scopes withdraw their owned registrations on session end or plugin unload. Arbitrary event subscriptions and resources outside those scopes remain the author's responsibility.

1. Keep packet callbacks short.
2. Copy packet payloads before retaining them.
3. Use scoped scheduling for session work.
4. Stop plugin-created background work during deactivation.
5. Honor the deactivation cancellation token.

`context.Cron` has a plugin lifetime and can run while disconnected. A callback must check session availability before using game actions.

## Register a command

This plugin registers `plugin-ping`. Its response comes from the plugin's translation file.

```csharp
using System.Threading.Tasks;
using DMCBK.Core.Commands;
using DMCBK.PluginSdk;
using Umpk.Commands;

public sealed class PingPlugin : IPlugin
{
    public void Configure(PluginDescriptor descriptor) => descriptor.Id = "plugin-ping";

    public Task ActivateAsync(PluginContext context)
    {
        context.Commands.Register(new PingCommand(context.Strings));
        return Task.CompletedTask;
    }

    private sealed class PingCommand(IPluginLocalization strings) : CommandBase
    {
        public override string CmdName => "plugin-ping";
        public override string CmdDesc => strings.Get("ping.description");
        public override string CmdUsage => "plugin-ping";

        public override void Register(CommandBuilder<CommandContext> builder)
        {
            builder.Literal(CmdName, command => command.Executes(
                call => call.Source.Result.Ok(strings.Get("ping.reply"))));
        }
    }
}
```

Add these keys to `lang/en.toml`:

```toml
[ping]
description = "Check that the plugin command is available."
reply = "Plugin is ready."
```

The command returns local output through `CommandContext.Result`. It does not send server chat.

`CmdUsage` is a grammar, such as `plugin-ping`, rather than an instruction sentence. The host supplies help rendering and command prefixes.

For typed arguments, extend the Brigadier command tree. Do not add a separate string parser that bypasses completion and scoped registration.

## Failure handling and presentation

Repeated callback failures count against the plugin's crash budget. A failing plugin can be disabled while other plugins remain active.

Cancellation and teardown do not guarantee rollback of external effects. A sent message or completed HTTP request cannot be undone by unloading an assembly.

Host presentation is optional. Use generic presentation requests for images, inventory, books and dialogs. Terminal dimensions, ANSI drawing and menu navigation belong to the host.

Next: [Settings and resources](settings-and-resources.md).
