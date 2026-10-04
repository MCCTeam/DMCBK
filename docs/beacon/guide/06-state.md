# Chapter 6: Remember settings and state

A variable disappears when the script reloads. Saved state survives a restart when the host supplies a configuration folder. Settings are values that the user chooses.

## Declare a setting

```beacon
# beacon 1
# setting prefix = "Hello" ; Greeting text
show settings.prefix
```

The expected default output is `Hello`. The comment describes the setting. The host can create or edit an overlay for the script ID.

Do not place passwords in script settings. Settings files are ordinary local files. Beacon configuration does not provide a general secret store.

## Save a counter

1. Create `saved-counter.bcn` with this complete script.

```beacon
# beacon 1
set count to saved("count") or 0
set count to count + 1
save "count" to count
show "Run count: {count}"
```

2. Run it through an engine that has a configuration folder.
3. Stop and load it again with the same script ID and folder.

The first output is `Run count: 1`. The second output is `Run count: 2`. The offline runner does not supply persistent configuration. Use the complete engine test below.

```csharp
using DMCBK.Core.Beacon;
using DMCBK.Testing;

string path = Path.GetFullPath("saved-counter.bcn");
string source = await File.ReadAllTextAsync(path);
string folder = Path.Combine(Path.GetTempPath(), "beacon-state-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    for (int expected = 1; expected <= 2; expected++)
    {
        var engine = new BeaconEngine(new ScriptTestHost());
        try
        {
            var result = await engine.RunScriptAsync(
                "saved-counter", source, configFolder: folder, fileName: path);
            if (!result.Success || !result.LocalOutput.Contains($"Run count: {expected}"))
                throw new InvalidOperationException("The saved count is incorrect.");
        }
        finally
        {
            engine.RemoveScript("saved-counter");
        }
    }
    Console.WriteLine("Saved-state checks passed.");
}
finally
{
    Directory.Delete(folder, recursive: true);
}
```

This test creates a temporary folder and a fresh engine for each load. It proves a disk-backed round trip. It removes the test folder afterward.

## Choose the right lifetime

| Value | Visible to | Survives script reload | Survives process restart |
| --- | --- | --- | --- |
| Function local | One function call | No | No |
| Script global | One script instance | No | No |
| `shared` value | Scripts in one engine | Yes, while the engine exists | No |
| Saved value | The same script ID and configuration root | Yes | Yes |
| Setting | The script that declares it | Yes, through its overlay | Yes |

Use `lock shared` for a shared read-modify-write operation. A lock prevents two handlers from changing the same value at the same time.

```beacon
# beacon 1
lock shared
  set shared["guide.loads"] to (shared["guide.loads"] or 0) + 1
end lock
set current to shared["guide.loads"]
show "Shared loads: {current}"
```

Keep the shared key specific to your script or feature. Avoid slow network operations inside a shared lock.

Next: [Chapter 7: Add commands and integrations](07-integrations.md).
