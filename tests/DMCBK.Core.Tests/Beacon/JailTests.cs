using System.Text;
using DMCBK.Core.Beacon;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Jail tests: the fs jail (<c>&lt;scripts-dir&gt;/data/</c>, symlink-escape refused, 1 MB caps), the net gate (beacon.toml allowlist, HTTPS-only, 5 s timeout, 1 MB cap, refusals print the exact <c>beacon.toml</c> lines), manifest enforcement (<c># needs:</c> exact-cover refuses with the paste line, <c># wants:</c> warns-loads), and read bounds (<c>online_players</c> paging, <c>chat_history</c> cap, single block reads).
/// </summary>
public sealed class JailTests : IDisposable
{
    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (string root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class JailHost : IBeaconHostServices
    {
        public List<string> HistoryValue { get; set; } = [];
        public List<string> PlayersValue { get; set; } = ["Alice"];
        public int? CountOverride { get; set; }
        public Dictionary<(int X, int Y, int Z), BeaconBlockInfo> Blocks { get; } = new();

        public Task SayAsync(string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task WhisperAsync(string player, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> SendServerAsync(string commandLine, CancellationToken ct = default) => Task.FromResult(string.Empty);
        public Task<string> RunMccAsync(string commandLine, CancellationToken ct = default) => Task.FromResult(string.Empty);
        public string? SelfName => "Tester";
        public double? ServerTps => null;
        public IReadOnlyList<string> OnlinePlayers(int limit) => PlayersValue.Take(limit).ToList(); public int OnlinePlayerCount => CountOverride ?? PlayersValue.Count;
        public IReadOnlyList<string> ChatHistory(int limit) => HistoryValue.TakeLast(Math.Min(limit, HistoryValue.Count)).ToList();
        public BeaconBlockInfo? GetBlock(int x, int y, int z)
            => Blocks.TryGetValue((x, y, z), out BeaconBlockInfo? info) ? info : null;
        public Task SaveAsync(string scriptId, string key, string value, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> LoadAsync(string scriptId, string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "mcc-beacon-jail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }

    private static BeaconScript MustParse(string fileName, string source)
    {
        BeaconLexResult lexed = BeaconLexer.Lex(fileName, source);
        BeaconParseResult parsed = BeaconParser.Parse(fileName, lexed.Tokens);
        Assert.NotNull(parsed.Script);
        return BeaconDesugar.Desugar(parsed.Script!);
    }

    private static async Task<IReadOnlyList<string>> RunShowingAsync(
        JailHost host, string body, Action<BeaconInterpreter>? configure = null)
    {
        var interp = new BeaconInterpreter(
            "j", "j.bcn", host, new VirtualClock(), new SeededRng(1), new FuelBudget());
        configure?.Invoke(interp);
        BeaconScript script = MustParse("j.bcn", body);
        BeaconRunResult result = await interp.RunTopLevelAsync(script, null, CancellationToken.None, seed: 1)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Success, result.Error?.Message);
        return result.LocalOutput;
    }

    #region fs jail

    [Fact]
    public void FsNumbers_Pin1MBCap()
    {
        Assert.Equal(1_048_576L, BeaconFileJail.MaxFileBytes);
    }

    [Fact]
    public void FileJail_WriteRead_RoundTrip()
    {
        var jail = new BeaconFileJail(Path.Combine(NewRoot(), "data"));
        var span = new SourceSpan("j.bcn", 1, 1, 0);

        jail.WriteText("notes.txt", "hello", span);

        Assert.Equal("hello", jail.ReadText("notes.txt", span));
    }

    [Fact]
    public void FileJail_Escape_Refused()
    {
        var jail = new BeaconFileJail(Path.Combine(NewRoot(), "data"));
        var span = new SourceSpan("j.bcn", 2, 1, 0);

        // Backslash is a separator only on Windows; on Linux it names a legal inner file, so only truly-escaping shapes are refused here.
        foreach (string evil in new[] { "../evil.txt", "/abs.txt", "sub/../../evil.txt", ".." })
        {
            BeaconRuntimeException ex = Assert.Throws<BeaconRuntimeException>(() => jail.Resolve(evil, span));
            Assert.Equal(BeaconDiagnosticCodes.FileJail, ex.Code);
            Assert.Contains("data", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void FileJail_SymlinkEscape_Refused()
    {
        string root = NewRoot();
        string outside = Path.Combine(root, "outside.txt");
        File.WriteAllText(outside, "secret");
        var jail = new BeaconFileJail(Path.Combine(root, "data"));
        Directory.CreateDirectory(Path.Combine(root, "data"));
        File.CreateSymbolicLink(Path.Combine(root, "data", "link.txt"), outside);
        var span = new SourceSpan("j.bcn", 1, 1, 0);

        BeaconRuntimeException ex = Assert.Throws<BeaconRuntimeException>(() => jail.ReadText("link.txt", span));
        Assert.Equal(BeaconDiagnosticCodes.FileJail, ex.Code);
        Assert.Contains("symbolic link", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FileJail_WriteCap_Refused()
    {
        var jail = new BeaconFileJail(Path.Combine(NewRoot(), "data"));
        var span = new SourceSpan("j.bcn", 1, 1, 0);

        BeaconRuntimeException ex = Assert.Throws<BeaconRuntimeException>(
            () => jail.WriteText("big.txt", new string('x', (int)BeaconFileJail.MaxFileBytes + 1), span));
        Assert.Equal(BeaconDiagnosticCodes.FileJail, ex.Code);
        Assert.Contains("1048576", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FileJail_ReadCap_Refused()
    {
        string data = Path.Combine(NewRoot(), "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "big.txt"), new string('x', (int)BeaconFileJail.MaxFileBytes + 1));
        var jail = new BeaconFileJail(data);
        var span = new SourceSpan("j.bcn", 1, 1, 0);

        BeaconRuntimeException ex = Assert.Throws<BeaconRuntimeException>(() => jail.ReadText("big.txt", span));
        Assert.Equal(BeaconDiagnosticCodes.FileJail, ex.Code);
    }

    [Fact]
    public async Task ScriptFileReadWrite_RoundTripsInsideJail()
    {
        var host = new JailHost();
        string data = Path.Combine(NewRoot(), "data");
        IReadOnlyList<string> output = await RunShowingAsync(host,
            "set ok to file_write(\"notes.txt\", \"hi\")\nshow ok\nshow file_read(\"notes.txt\")\n",
            interp => interp.FileJail = new BeaconFileJail(data));

        Assert.Contains("yes", output);
        Assert.Contains("hi", output);
    }

    [Fact]
    public async Task ScriptFileRead_Escape_IsCatchableB4009()
    {
        var host = new JailHost();
        string data = Path.Combine(NewRoot(), "data");
        var interp = new BeaconInterpreter(
            "j", "j.bcn", host, new VirtualClock(), new SeededRng(1), new FuelBudget())
        {
            FileJail = new BeaconFileJail(data),
        };
        BeaconScript script = MustParse("j.bcn",
            "try\nshow file_read(\"../evil.txt\")\ncatch err\nshow err.code\nshow \"caught\"\nend try\n");
        BeaconRunResult result = await interp.RunTopLevelAsync(script, null, CancellationToken.None, seed: 1)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Success);
        Assert.Contains(BeaconDiagnosticCodes.FileJail, result.LocalOutput);
        Assert.Contains("caught", result.LocalOutput);
    }

    #endregion
    #region net gate

    [Fact]
    public void NetNumbers_Pin5sTimeoutAnd1MBCap()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), BeaconNetGate.DefaultTimeout);
        Assert.Equal(1_048_576L, BeaconNetGate.MaxResponseBytes);
    }

    private static BeaconNetGate AllowlistedGate(
        Func<Uri, string, string?, CancellationToken, Task<byte[]>>? fetcher = null,
        TimeSpan? timeout = null,
        params string[] hosts)
    {
        return new BeaconNetGate(new BeaconNetConfig(hosts), fetcher, timeout);
    }

    [Fact]
    public async Task HttpGet_AllowlistedHttps_Succeeds()
    {
        var host = new JailHost();
        var gate = AllowlistedGate(
            (_, _, _, _) => Task.FromResult(Encoding.UTF8.GetBytes("{\"ok\":1}")),
            null, "hooks.example.com");
        IReadOnlyList<string> output = await RunShowingAsync(host,
            "show http_get(\"https://hooks.example.com/mcc-deals\")\n",
            interp => interp.NetGate = gate);

        Assert.Contains("{\"ok\":1}", output);
    }

    [Fact]
    public async Task HttpGet_PlainHttp_Refused()
    {
        var host = new JailHost();
        var gate = AllowlistedGate(
            (_, _, _, _) => Task.FromResult(Encoding.UTF8.GetBytes("x")),
            null, "hooks.example.com");
        var interp = new BeaconInterpreter(
            "j", "j.bcn", host, new VirtualClock(), new SeededRng(1), new FuelBudget())
        {
            NetGate = gate,
        };
        BeaconScript script = MustParse("j.bcn",
            "try\nshow http_get(\"http://hooks.example.com/x\")\ncatch err\nshow err.code\nshow err.message\nend try\n");
        BeaconRunResult result = await interp.RunTopLevelAsync(script, null, CancellationToken.None, seed: 1)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Success);
        Assert.Contains(BeaconDiagnosticCodes.NetGate, result.LocalOutput);
        Assert.Contains("HTTPS", string.Join("\n", result.LocalOutput), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpGet_NonAllowlisted_RefusalPrintsExactTomlLines()
    {
        var gate = AllowlistedGate(
            (_, _, _, _) => Task.FromResult(Encoding.UTF8.GetBytes("x")),
            null, "already.example.com");
        var span = new SourceSpan("deal.bcn", 7, 3, 10);

        BeaconRuntimeException ex = await Assert.ThrowsAsync<BeaconRuntimeException>(
            () => gate.GetAsync("https://hooks.example.com/mcc-deals", span));

        Assert.Equal(BeaconDiagnosticCodes.NetGate, ex.Code);
        Assert.Contains("hooks.example.com", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(ex.Suggestion);
        Assert.Contains("[Net]", ex.Suggestion, StringComparison.Ordinal);
        Assert.Contains("AllowedHosts", ex.Suggestion, StringComparison.Ordinal);
        Assert.Contains("already.example.com", ex.Suggestion, StringComparison.Ordinal);
        Assert.Contains("hooks.example.com", ex.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpGet_ResponseCap_Refused()
    {
        var gate = AllowlistedGate(
            (_, _, _, _) => Task.FromResult(new byte[BeaconNetGate.MaxResponseBytes + 1]),
            null, "h.example.com");
        var span = new SourceSpan("deal.bcn", 1, 1, 0);

        BeaconRuntimeException ex = await Assert.ThrowsAsync<BeaconRuntimeException>(
            () => gate.GetAsync("https://h.example.com/big", span));

        Assert.Equal(BeaconDiagnosticCodes.NetGate, ex.Code);
        Assert.Contains("1048576", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpGet_Timeout_AbortsCatchably()
    {
        var gate = AllowlistedGate(
            (_, _, _, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct).ContinueWith(_ => Array.Empty<byte>(), ct),
            TimeSpan.FromMilliseconds(50), "h.example.com");
        var span = new SourceSpan("deal.bcn", 1, 1, 0);

        BeaconRuntimeException ex = await Assert.ThrowsAsync<BeaconRuntimeException>(
            () => gate.GetAsync("https://h.example.com/slow", span));

        Assert.Equal(BeaconDiagnosticCodes.NetGate, ex.Code);
        Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HttpPost_SendsBody_ReturnsText()
    {
        string? seenMethod = null;
        string? seenBody = null;
        var gate = AllowlistedGate(
            (uri, method, body, _) =>
            {
                seenMethod = method;
                seenBody = body;
                return Task.FromResult(Encoding.UTF8.GetBytes("stored"));
            },
            null, "hooks.example.com");
        var host = new JailHost();
        IReadOnlyList<string> output = await RunShowingAsync(host,
            "show http_post(\"https://hooks.example.com/mcc-deals\", \"n=1\")\n",
            interp => interp.NetGate = gate);

        Assert.Contains("stored", output);
        Assert.Equal("POST", seenMethod);
        Assert.Contains("n=1", seenBody, StringComparison.Ordinal);
    }

    #endregion
    #region manifest enforcement

    private static BeaconEngine LintEngine()
    {
        var engine = new BeaconEngine(
            new JailHost(), new VirtualClock(), new SeededRng(11), new FuelBudget());
        return engine;
    }

    [Fact]
    public void Manifest_NeedsExactCover_RefusesWithPasteLine()
    {
        var engine = LintEngine();
        engine.LoadSource("deal", "deal.bcn", "# beacon 1\n# needs: chat.send\nsay \"hi\"\nserver \"/home\"\n");

        IReadOnlyList<BeaconDiagnostic> errors = engine.Lint("deal")
            .Where(d => d.Severity == BeaconSeverity.Error).ToList();

        BeaconDiagnostic refusal = Assert.Single(
            errors, d => string.Equals(d.Code, BeaconDiagnosticCodes.ManifestNeedsMismatch, StringComparison.Ordinal));
        Assert.Contains("server.send", refusal.Message, StringComparison.Ordinal);
        Assert.NotNull(refusal.Suggestion);
        Assert.Contains("# needs: chat.send server.send", refusal.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Manifest_Refusal_BlocksLoad()
    {
        var host = new JailHost();
        var engine = new BeaconEngine(host, new VirtualClock(), new SeededRng(11), new FuelBudget());
        BeaconRunResult result = await engine.RunScriptAsync(
            "deal", "# beacon 1\n# needs: chat.send\nsay \"hi\"\nserver \"/home\"\n");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(BeaconDiagnosticCodes.ManifestNeedsMismatch, result.Error!.Code);
    }

    [Fact]
    public void Manifest_Covered_LoadsClean()
    {
        var engine = LintEngine();
        engine.LoadSource("ok", "ok.bcn", "# beacon 1\n# needs: chat.send server.send\nsay \"hi\"\nserver \"/home\"\n");

        Assert.DoesNotContain(engine.Lint("ok"), d => d.Severity == BeaconSeverity.Error);
    }

    [Fact]
    public void Manifest_Superset_LoadsClean_DocumentedLeniency()
    {
        // Declared-but-unused entries are allowed: only missing coverage refuses.
        var engine = LintEngine();
        engine.LoadSource("extra", "extra.bcn", "# beacon 1\n# needs: chat.send inventory.read\nsay \"hi\"\n");

        Assert.DoesNotContain(engine.Lint("extra"), d => d.Severity == BeaconSeverity.Error);
    }

    [Fact]
    public async Task Manifest_WantsMissing_WarnsAndLoads()
    {
        var host = new JailHost();
        var engine = new BeaconEngine(host, new VirtualClock(), new SeededRng(11), new FuelBudget());
        BeaconRunResult result = await engine.RunScriptAsync(
            "grace", "# beacon 1\n# wants: frobnicate.read\nsay \"graceful without the bridge\"\n");

        Assert.True(result.Success);
        BeaconDiagnostic warning = Assert.Single(
            result.Diagnostics, d => string.Equals(d.Code, BeaconDiagnosticCodes.ManifestWantsUnavailable, StringComparison.Ordinal));
        Assert.Equal(BeaconSeverity.Warning, warning.Severity);
        Assert.Contains("frobnicate.read", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_Undeclared_ZeroConfig_LoadsClean()
    {
        // Zero-config start: scripts that declare no manifest load unenforced.
        var engine = LintEngine();
        engine.LoadSource("plain", "plain.bcn", "# beacon 1\nsay \"hi\"\n");

        Assert.DoesNotContain(engine.Lint("plain"), d => d.Severity == BeaconSeverity.Error);
    }

    #endregion
    #region read bounds

    [Fact]
    public void ReadNumbers_PinPages()
    {
        Assert.Equal(50, BeaconReadBounds.OnlinePlayersDefaultPage);
        Assert.Equal(100, BeaconReadBounds.OnlinePlayersMaxPage);
        Assert.Equal(200, BeaconReadBounds.ChatHistoryMax);
    }

    [Fact]
    public async Task OnlinePlayers_DefaultCapped()
    {
        var host = new JailHost
        {
            PlayersValue = Enumerable.Range(1, 500).Select(i => $"P{i:000}").ToList(),
        };

        IReadOnlyList<string> output = await RunShowingAsync(host,
            "show len(online_players)\nshow len(online_players())\n");

        Assert.Equal(new[] { "50", "50" }, output);
    }

    [Fact]
    public async Task OnlinePlayers_ExplicitClampedToMax()
    {
        var host = new JailHost
        {
            PlayersValue = Enumerable.Range(1, 500).Select(i => $"P{i:000}").ToList(),
        };

        IReadOnlyList<string> output = await RunShowingAsync(host,
            "show len(online_players(1000))\nshow len(online_players(7))\nshow len(online_players(0))\n");

        Assert.Equal(new[] { "100", "7", "0" }, output);
    }

    [Fact]
    public async Task OnlineCount_StaysExactThroughCountSeam()
    {
        var host = new JailHost
        {
            PlayersValue = ["Alice", "Bob", "Zed"],
            CountOverride = 500,
        };

        IReadOnlyList<string> output = await RunShowingAsync(host, "show server.online_count\nshow online_count\n");

        Assert.Equal(new[] { "500", "500" }, output);
    }

    [Fact]
    public async Task ChatHistory_CappedAt200()
    {
        var host = new JailHost
        {
            HistoryValue = Enumerable.Range(1, 500).Select(i => $"line {i}").ToList(),
        };

        IReadOnlyList<string> output = await RunShowingAsync(host,
            "show len(chat_history(500))\nshow len(chat_history())\nshow len(chat_history(10))\n");

        Assert.Equal(new[] { "200", "200", "10" }, output);
    }

    [Fact]
    public async Task BlockAt_OutOfRange_RaisesCatchably()
    {
        var host = new JailHost();
        var interp = new BeaconInterpreter(
            "j", "j.bcn", host, new VirtualClock(), new SeededRng(1), new FuelBudget());
        BeaconScript script = MustParse("j.bcn",
            "try\nshow world.block_at(0, 10000, 0)\ncatch err\nshow err.code\nshow \"caught\"\nend try\n" +
            "try\nshow world.block_at(0.5, 64, 0)\ncatch err\nshow err.code\nshow \"caught-frac\"\nend try\n");
        BeaconRunResult result = await interp.RunTopLevelAsync(script, null, CancellationToken.None, seed: 1)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Success);
        Assert.Equal(BeaconDiagnosticCodes.ReadBounds, result.LocalOutput[0]);
        Assert.Contains("caught", result.LocalOutput);
        Assert.Equal(BeaconDiagnosticCodes.ReadBounds, result.LocalOutput[2]);
        Assert.Contains("caught-frac", result.LocalOutput);
    }

    [Fact]
    public async Task BlockAt_InRange_ReturnsMap()
    {
        var host = new JailHost();
        host.Blocks[(1, 64, -2)] = new BeaconBlockInfo("grass_block", 2);

        IReadOnlyList<string> output = await RunShowingAsync(host,
            "set b to world.block_at(1, 64, -2)\nshow b.name\nshow b.id\n");

        Assert.Equal(new[] { "grass_block", "2" }, output);
    }

    [Fact]
    public async Task BlockAt_Unloaded_RaisesCatchably()
    {
        var host = new JailHost();
        var interp = new BeaconInterpreter(
            "j", "j.bcn", host, new VirtualClock(), new SeededRng(1), new FuelBudget());
        BeaconScript script = MustParse("j.bcn",
            "try\nshow world.block_at(10, 64, 10)\ncatch err\nshow err.code\nshow \"unloaded\"\nend try\n");
        BeaconRunResult result = await interp.RunTopLevelAsync(script, null, CancellationToken.None, seed: 1)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Success);
        Assert.Contains(BeaconDiagnosticCodes.ReadBounds, result.LocalOutput);
        Assert.Contains("unloaded", result.LocalOutput);
    }
    #endregion
}
