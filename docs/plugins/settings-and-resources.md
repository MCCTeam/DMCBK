# Settings, storage and resources

Packages describe defaults. User data survives package replacement. Keep those responsibilities separate when designing a plugin.

## Declare typed settings

```csharp
using System;
using System.Threading.Tasks;
using DMCBK.PluginSdk;

public sealed class CounterSettings : IValidatablePluginSettings
{
    public int IntervalSeconds { get; set; } = 30;
    public void Validate() => IntervalSeconds = Math.Clamp(IntervalSeconds, 1, 3600);
}

public sealed class SettingsPlugin : IPlugin
{
    private CounterSettings? _settings;

    public void Configure(PluginDescriptor descriptor)
    {
        descriptor.Id = "settings-example";
        descriptor.WithSettings<CounterSettings>();
    }

    public Task ActivateAsync(PluginContext context)
    {
        _settings = context.Settings.Load<CounterSettings>();
        context.ConfigurationReloaded += (_, _) =>
            _settings = context.Settings.Load<CounterSettings>();
        return Task.CompletedTask;
    }
}
```

The settings type needs a public parameterless constructor. `Validate()` normalizes values after loading. Runtime counters and connection objects do not belong in settings.

`context.Settings.Save(settings)` persists user changes. Save does not replace the need to normalize values before accepting them from an editor.

A package can supply `settings.toml` defaults. User values overlay package defaults. Updates do not overwrite existing user choices.

A malformed user file produces a warning and validated defaults. It does not make successful activation proof that the user's file parsed correctly.

## Persist data

| API | Behavior |
| --- | --- |
| `Storage.DataDirectory` | Assigned persistent data root |
| `Storage.GetPath(relativePath)` | Resolve a path inside that root and create parent directories |
| `Storage.TryGet(key, out value)` | Read a stored string |
| `Storage.Set(key, value)` | Change the in-memory key/value store |
| `Storage.Remove(key)` | Remove a key |
| `Storage.Save()` | Persist the key/value store |

1. Write user data through the assigned storage root.
2. Call `Save()` after changing the key/value store.
3. Serialize concurrent counter updates in your plugin.
4. Keep files out of immutable package and source-cache directories.

The key/value file is `data/storage.toml`. Changing the in-memory store without saving does not persist it.

## Translate user-facing text

1. Create `lang/en.toml` in the package.
2. Give each message a stable key.
3. Use `context.Strings.Get(key)` for plain text.
4. Use `context.Strings.Format(key, args)` for parameterized text.

```toml
[counter]
joined = "Session {0} started."
interval_help = "Seconds between counter reports."
```

Nested TOML keys become dotted lookup keys, such as `counter.joined`.

Lookup follows the selected culture, its parents and English. A missing key appears as the key itself. A partial translation can fall back per message.

Language files load when the plugin loads. Reload the plugin after editing a translation file.

Tomlet settings comments can use `$counter.interval_help$` placeholders. The settings writer resolves them through the plugin's own language table.

## Supply a manual

1. Create `man/en/session-counter.md`.
2. Explain purpose, commands, settings and limitations.
3. Add `man = ["session-counter"]` to the manifest.
4. Add translated topics under matching language directories when needed.

Manual registration belongs to the current client. Unloading the plugin removes its contributions.

## Handle sensitive configuration

User settings are ordinary files. They are not a secret vault. Avoid logging access tokens or including credentials in release archives.

A host can supply its own secure storage workflow. A plugin should document which settings contain credentials and how its host supplies them.

Next: [Dependencies and loading](dependencies-and-loading.md).
