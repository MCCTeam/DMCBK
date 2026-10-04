using DMCBK.Testing;

if (args.Length != 2) throw new ArgumentException("Supply a stage folder and number (1-4).");
int stage = int.Parse(args[1]);
await using PluginTestHost host = PluginTestHost.Create(new() { Budget = TimeSpan.FromSeconds(60) });
host.AddPluginFolder(Path.GetFullPath(args[0]));
var result = await host.LoadAsync(host.Token);
if (!result.Success || !host.Plugins.List().Single().Loaded)
    throw new InvalidOperationException("Stage did not load.");
await host.RunSessionAsync(async _ =>
{
    if (stage >= 2 && !await host.WaitForAsync(
        () => host.Client.Variables.Get("session_journal_sessions") == "1"))
        throw new InvalidOperationException("Session callback did not run.");
    if (stage >= 3 && !File.Exists(host.DataFile("session-journal", "storage.toml")))
        throw new InvalidOperationException("Storage was not saved.");
    if (stage >= 4)
    {
        var command = await host.Client.Commands.DispatchAsync("journal-count", host.Token);
        if (!command.IsSuccess || command.Message != "Sessions: 1")
            throw new InvalidOperationException("Command returned the wrong result.");
    }
}, host.Token);
Console.WriteLine($"PASS: tutorial stage {stage}.");
