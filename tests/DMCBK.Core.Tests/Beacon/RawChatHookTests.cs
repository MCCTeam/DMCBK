using DMCBK.Core.Beacon;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Chat hooks: raw_chat fires with untouched fields and whisper carries its raw line.
/// </summary>
public sealed class RawChatHookTests
{

    [Fact]
    public void HookCatalog_KnowsRawChatAsNonSuppressible()
    {
        Assert.Contains("raw_chat", BeaconHookCatalog.KnownHooks);
        Assert.DoesNotContain("raw_chat", BeaconHookCatalog.SuppressibleHooks);
        Assert.False(BeaconHookCatalog.IsSuppressible("raw_chat"));
        Assert.True(BeaconHookCatalog.TryGetSchema("raw_chat", out BeaconHookSchema? schema));
        Assert.NotNull(schema);
        Assert.Contains(schema!.Fields, f => f.Name == "raw");
        Assert.Contains(schema.Fields, f => f.Name == "category");
        Assert.Contains(schema.Fields, f => f.Name == "sender");
        Assert.Contains(schema.Fields, f => f.Name == "sender_id");
        Assert.Contains(schema.Fields, f => f.Name == "chat_type");
        Assert.Contains(schema.Fields, f => f.Name == "target");
        Assert.Contains(schema.Fields, f => f.Name == "body");
        Assert.Contains(schema.Fields, f => f.Name == "translation_key");
        Assert.Contains(schema.Fields, f => f.Name == "verified");
    }

    [Fact]
    public void HookCatalog_WhisperSchema_HasRaw()
    {
        Assert.True(BeaconHookCatalog.TryGetSchema("whisper", out BeaconHookSchema? schema));
        Assert.NotNull(schema);
        Assert.Contains(schema!.Fields, f => f.Name == "raw");
    }

    [Fact]
    public async Task RawChat_FiresWithDocumentedFields()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "raw",
            "on raw_chat\nshow \"{raw}|{category}|{sender}|{body}|{verified}\"\nend on\n");
        Assert.True(run.Success, run.Error?.Message ?? "load failed");

        BeaconFireResult fire = await engine.FireEventAsync("raw_chat", BeaconEventFields.RawChat(
            "[Survival] <Nick> hi", "player", "Nick", Guid.NewGuid().ToString(), 1, null, "hi", null, verified: false));
        BeaconRunResult? result = Assert.Single(fire.Handlers).Result;
        Assert.NotNull(result);
        Assert.True(result!.Success);
        Assert.Contains("Nick", result.LocalOutput[0]);
        Assert.Contains("hi", result.LocalOutput[0]);
    }

    [Fact]
    public async Task RawChat_StopEvent_IsNoOp()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "raw",
            "on raw_chat\nstop event\nend on\n");
        Assert.True(run.Success);

        BeaconFireResult fire = await engine.FireEventAsync("raw_chat", BeaconEventFields.RawChat(
            "line", "system", null, null, -1, null, "line", null, verified: false));
        Assert.False(fire.Suppressed);
        Assert.NotEmpty(fire.SuppressionNotes);
    }

    [Fact]
    public async Task Whisper_CarriesFullTrimmedRaw()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "w",
            "on whisper\nshow \"{player}|{message}|{raw}\"\nend on\n");
        Assert.True(run.Success);

        BeaconFireResult fire = await engine.FireEventAsync(
            "whisper", BeaconEventFields.Whisper("Alex", "meet me", "[Lobby] Alex >> meet me"));
        BeaconRunResult? result = Assert.Single(fire.Handlers).Result;
        Assert.NotNull(result);
        Assert.True(result!.Success);
        Assert.Equal(["Alex|meet me|[Lobby] Alex >> meet me"], result.LocalOutput);
    }

    [Fact]
    public async Task Whisper_DefaultRaw_FallsBackToMessage()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "w",
            "on whisper\nshow raw\nend on\n");
        Assert.True(run.Success);

        BeaconFireResult fire = await engine.FireEventAsync(
            "whisper", BeaconEventFields.Whisper("Alex", "psst"));
        BeaconRunResult? result = Assert.Single(fire.Handlers).Result;
        Assert.Equal(["psst"], result!.LocalOutput);
    }

    [Fact]
    public async Task RawChat_NoneFields_RenderAsNone()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "raw",
            "on raw_chat\nshow (sender is not set)\nshow (chat_type is not set)\nshow (translation_key is not set)\nend on\n");
        Assert.True(run.Success);

        BeaconFireResult fire = await engine.FireEventAsync("raw_chat", BeaconEventFields.RawChat(
            "Server restarts soon", "system", null, null, -1, null, "Server restarts soon", null, verified: false));
        Assert.Equal(["yes", "yes", "yes"], fire.Handlers[0].Result!.LocalOutput);
    }
}
