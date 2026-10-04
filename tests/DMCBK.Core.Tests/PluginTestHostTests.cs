using DMCBK.Testing;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// The harness shipped in <c>DMCBK.Testing</c>, exercised the way a plugin author would: write a plugin, load it, run a session, read back what it wrote.
/// If this ever stops proving a REAL session, the package is worse than useless, because every plugin's tests would still pass.
/// </summary>
public sealed class PluginTestHostTests
{
    private const string SessionProbeSource = """
        using System.Threading.Tasks;
        using DMCBK.PluginSdk;

        public sealed class SessionProbePlugin : IPlugin
        {
            public void Configure(PluginDescriptor descriptor) => descriptor.Id = "session-probe";

            public Task ActivateAsync(PluginContext context)
            {
                context.SessionStarted += (_, e) =>
                {
                    context.Storage.Set("state", e.Session.State.ToString());
                    context.Storage.Set("detached", e.Session.Detached.IsCancellationRequested.ToString());
                    context.Storage.Save();
                };
                context.SessionEnded += (_, _) =>
                {
                    context.Storage.Set("ended", "1");
                    context.Storage.Save();
                };
                return Task.CompletedTask;
            }
        }
        """;

    [Fact]
    public async Task ASourcePluginGetsARealSessionAndItsStorageIsReadableBack()
    {
        await using PluginTestHost host = PluginTestHost.Create();
        host.AddSourcePlugin("session-probe", SessionProbeSource);

        Assert.True((await host.LoadAsync()).Success);
        Assert.True(host.Plugins.List().Single().Loaded);

        string storage = host.DataFile("session-probe", "storage.toml");
        await host.RunSessionAsync(async _ =>
            Assert.True(await host.WaitForAsync(() => File.Exists(storage))));

        string written = File.ReadAllText(storage);
        Assert.Contains("detached", written, StringComparison.Ordinal);
        Assert.Contains("False", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APluginWrittenDisabledIsNotLoaded()
    {
        await using PluginTestHost host = PluginTestHost.Create();
        host.AddSourcePlugin("session-probe", SessionProbeSource, enabled: false);

        await host.LoadAsync();

        Assert.False(host.Plugins.List().Single().Loaded);
    }
}
