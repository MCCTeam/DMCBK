using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DMCBK.Core;
using DMCBK.Testing;
using Umpk;

string root = args.Length == 1 ? Path.GetFullPath(args[0])
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
string staging = Path.Combine(Path.GetTempPath(), "dmcbk-advanced-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(staging);
try
{
    string Stage(string project, params string[] assemblies)
    {
        string folder = Path.Combine(staging, project);
        Directory.CreateDirectory(folder);
        File.Copy(Path.Combine(root, project, "plugin.toml"), Path.Combine(folder, "plugin.toml"));
        Directory.CreateDirectory(Path.Combine(folder, "lang"));
        File.Copy(Path.Combine(root, project, "lang/en.toml"), Path.Combine(folder, "lang/en.toml"));
        foreach (string assembly in assemblies)
            File.Copy(Path.Combine(root, project, "bin/Release/net10.0", assembly), Path.Combine(folder, assembly));
        return folder;
    }
    await using PluginTestHost host = PluginTestHost.Create();
    host.Client.Scripts.SetMuted(true);
    host.AddPluginFolder(Stage("Provider", "Guide.Provider.dll", "Guide.Contracts.dll"));
    // Deliberately no Contracts DLL in this consumer package.
    host.AddPluginFolder(Stage("Consumer", "Guide.Consumer.dll"));
    host.AddPluginFolder(Stage("BridgeObserver", "Guide.BridgeObserver.dll"));
    var loaded = await host.LoadAsync();
    if (!loaded.Success || host.Plugins.List().Any(plugin => !plugin.Loaded))
        throw new InvalidOperationException("An advanced plugin did not activate: " + loaded.Message
            + " " + string.Join(" | ", host.Plugins.List().Select(plugin => plugin.LastError)));
    Check(host.Client.Variables.Get("guide_order_service") == "12", "typed service");
    Check(host.Client.Variables.Get("guide_order_response") == "12", "typed request response");
    Check(host.Client.Variables.Get("guide_order_notification") == "12", "typed notification");
    Check(host.Client.Variables.Get("guide_order_exported_context") == "yes", "provider-owned contract context");
    Console.WriteLine("PASS exported contracts, services, request/response and notifications");

    string path = Path.Combine(root, "workflow.bcn");
    var script = await host.Client.Scripts.RunAsync("workflow", await File.ReadAllTextAsync(path), path);
    Check(script.Success, "workflow script load");
    var (command, output) = await host.Client.Commands.DispatchCapturedAsync("guide-workflow");
    Check(command.IsSuccess && command.Message?.Contains("12", StringComparison.Ordinal) == true, "plugin calls script export");
    var snapshot = await host.Client.Scripts.RunAsync("snapshot", """
        # beacon 1
        # needs: workflow.read
        assert(workflow.total is 12, "snapshot")
        """, "snapshot.bcn");
    Check(snapshot.Success, "variable snapshot");
    Console.WriteLine("PASS script export, custom event and variable snapshot");
    await host.RunSessionAsync(async session =>
    {
        Check(await host.WaitForAsync(() => host.Client.Variables.Get("guide_bridge_delayed") == "yes"), "session delay");
        Check(await host.WaitForAsync(() => int.TryParse(host.Client.Variables.Get("guide_bridge_ticks"), out int value) && value >= 2), "session ticks");
        await session.SendPluginMessageAsync(new Identifier("guide", "observation"), [1, 2, 3]);
        Check(await host.WaitForAsync(() => int.TryParse(host.Client.Variables.Get("guide_bridge_packet_bytes"), out int bytes) && bytes > 0), "raw packet copy");
    });
    Check(await host.WaitForAsync(() => host.Client.Variables.Get("guide_bridge_canceled") == "yes"), "session cancellation");
    var unloaded = await host.Plugins.UnloadAsync("guide-bridge");
    Check(unloaded.Success, "bridge unload");
    Check(!(await host.Client.Commands.DispatchAsync("guide-workflow")).IsSuccess, "command withdrawal");
    Console.WriteLine("PASS session scheduler, packet observer, cancellation and command cleanup");
}
finally
{
    try
    {
        Directory.Delete(staging, recursive: true);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        // A loaded DLL can remain locked until its collectible context releases it.
        Console.Error.WriteLine($"Temporary plugin files remain at {staging}: {exception.Message}");
    }
}
static void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("Check failed: " + name);
}
