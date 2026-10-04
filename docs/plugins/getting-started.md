# Make your first plugin

This plugin counts successful session starts. It stores the count outside the package and exposes the current value through the client's variable store.

## Create the author project

1. Install the .NET 10 SDK.
2. Create a class library named `SessionCounter`.
3. Add `DMCBK.PluginSdk` version `0.1.0-preview.1`.
4. Replace the generated class with this file.

```csharp
using System.Globalization;
using System.Threading.Tasks;
using DMCBK.PluginSdk;

public sealed class SessionCounter : IPlugin
{
    public void Configure(PluginDescriptor descriptor)
    {
        descriptor.Id = "session-counter";
        descriptor.Version = "1.0.0";
        descriptor.ApiVersion = PluginApiVersion.Major;
    }

    public Task ActivateAsync(PluginContext context)
    {
        int count = 0;
        if (context.Storage.TryGet("sessions", out string? saved))
            int.TryParse(saved, NumberStyles.Integer, CultureInfo.InvariantCulture, out count);

        context.SessionStarted += (_, _) =>
        {
            count++;
            string value = count.ToString(CultureInfo.InvariantCulture);
            context.Storage.Set("sessions", value);
            context.Storage.Save();
            context.Variables.Set("session-counter.sessions", value);
        };
        return Task.CompletedTask;
    }
}
```

`Configure` declares identity. `ActivateAsync` attaches callbacks once. Each reconnect produces another `SessionStarted` event.

The callback writes a small counter. For frequent events, batch disk writes instead of saving on every callback.

## Create a source package

1. Create a directory named `session-counter`.
2. Copy `SessionCounter.cs` into that directory.
3. Add this `plugin.toml` file.

```toml
schema-version = 2
id = "session-counter"
version = "1.0.0"
kind = "source"
target = "any"
entry = "SessionCounter.cs"
framework = "net10.0"
api-version = "1.0"
dmcbk = ">=0.1.0-preview.1 <0.2.0"
umpk = ">=0.9.0-beta.4 <0.10.0"
needs = ["commands"]
```

The archive root contains `plugin.toml` and the entry file. Do not add an extra wrapper directory inside the archive.

Runtime source compilation reads one entry file. It does not restore your author project's NuGet dependencies or build its `.csproj`.

A development project can use implicit usings. A runtime source entry must include the namespaces that it uses explicitly.

## Load it in a host

1. Add `DMCBK.Commands` and `DMCBK.Plugins` to the host.
2. Compose Commands before Plugins.
3. Supply explicit package and data paths.
4. Load plugins before starting the client.

Use the complete [local loading example](../guides/plugins.md#load-a-local-plugin).

Check each plugin's `Loaded` state after loading. An aggregate operation result does not prove that every individual plugin activated.

Installed package files are immutable. User settings and data belong in `userdata/<id>` under the plugin root.

The [downloadable source example](../../samples/PluginAuthoring/SessionCounter/SessionCounter.cs) and [manifest](../../samples/PluginAuthoring/SessionCounter/plugin.toml) match this tutorial.

Next: [Lifecycle and commands](lifecycle-and-commands.md).
