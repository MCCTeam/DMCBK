using DMCBK.Core;
using DMCBK.Testing;

if (args.Length is < 1 or > 2)
    throw new ArgumentException("Supply a SessionJournal package folder.");

int requestedIncrement = args.Length == 2 ? int.Parse(args[1]) : 1;
int increment = Math.Clamp(requestedIncrement, 1, 100);
string root = Path.Combine(Path.GetTempPath(), "dmcbk-journal-" + Guid.NewGuid().ToString("N"));
try
{
    if (args.Length == 2)
    {
        string user = Path.Combine(root, "userdata", "session-journal");
        Directory.CreateDirectory(user);
        File.WriteAllText(Path.Combine(user, "settings.toml"), $"Increment = {requestedIncrement}\n");
    }
    for (int iteration = 0; iteration < 2; iteration++)
    {
        await using PluginTestHost host = PluginTestHost.Create(new()
        {
            PluginsRoot = root,
            Budget = TimeSpan.FromSeconds(60)
        });
        host.Client.Scripts.SetMuted(false);
        host.AddPluginFolder(Path.GetFullPath(args[0]));
        var loaded = await host.LoadAsync(host.Token);
        if (!loaded.Success || !host.Plugins.List().Single().Loaded)
            throw new InvalidOperationException("Plugin activation failed: " + loaded.Message);

        int previous = iteration * increment;
        await CheckCountAsync(host, previous);
        int expected = previous + increment;
        await host.RunSessionAsync(async _ =>
        {
            if (!await host.WaitForAsync(() =>
                host.Client.Variables.Get("session_journal_sessions") == expected.ToString()))
                throw new InvalidOperationException("Session counter did not change.");
            await CheckCountAsync(host, expected);
        }, host.Token);

        if (!File.Exists(host.DataFile("session-journal", "storage.toml")))
            throw new InvalidOperationException("Storage was not saved.");
        var reloaded = await host.Plugins.ReloadAsync("session-journal", host.Token);
        if (!reloaded.Success || !host.Plugins.List().Single().Loaded)
            throw new InvalidOperationException("Reload failed.");
        await CheckCountAsync(host, expected);

        string script = """
# beacon 1
# needs: journal.read
extern journal_count from "session-journal"
assert(journal_count() is EXPECTED, "saved count")
show journal_count()
""".Replace("EXPECTED", expected.ToString());
        var run = await host.Client.Scripts.RunAsync("read-count", script, ct: host.Token);
        if (!run.Success || !run.LocalOutput.Contains(expected.ToString()))
            throw new InvalidOperationException("Beacon bridge failed.");
        host.Client.Scripts.Stop("read-count");

        var unloaded = await host.Plugins.UnloadAsync("session-journal", host.Token);
        if (!unloaded.Success) throw new InvalidOperationException("Unload failed.");
        var missing = await host.Client.Commands.DispatchAsync("journal-count", host.Token);
        if (missing.IsSuccess) throw new InvalidOperationException("Command remained after unload.");
    }
    Console.WriteLine("PASS: load, two fresh sessions, storage, reload, Beacon and command cleanup.");
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}

static async Task CheckCountAsync(PluginTestHost host, int expected)
{
    var result = await host.Client.Commands.DispatchAsync("journal-count", host.Token);
    if (!result.IsSuccess || result.Message != $"Sessions: {expected}")
        throw new InvalidOperationException($"Expected Sessions: {expected}, got {result.Message}.");
}
