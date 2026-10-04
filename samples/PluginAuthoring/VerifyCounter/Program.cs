using DMCBK.Testing;

if (args.Length != 1) throw new ArgumentException("Supply the SessionCounter folder.");
await using PluginTestHost host = PluginTestHost.Create(new() { Budget = TimeSpan.FromSeconds(60) });
host.AddPluginFolder(Path.GetFullPath(args[0]));
var result = await host.LoadAsync(host.Token);
if (!result.Success || !host.Plugins.List().Single().Loaded)
    throw new InvalidOperationException("SessionCounter did not load.");
await host.RunSessionAsync(async _ =>
{
    if (!await host.WaitForAsync(() => host.Client.Variables.Get("session_counter_sessions") == "1"))
        throw new InvalidOperationException("SessionCounter callback did not run.");
    if (!File.Exists(host.DataFile("session-counter", "storage.toml")))
        throw new InvalidOperationException("The counter was not saved.");
}, host.Token);
Console.WriteLine("PASS: SessionCounter loaded, received a session and saved its count.");
