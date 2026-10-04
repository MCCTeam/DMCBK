using DMCBK.Core.Beacon;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// State: TOML-backed per-script <c>saved</c> store (atomic tmp-plus-rename writes, per-script isolation, path jail) plus the RAM-only namespaced <c>shared</c> store (quotas, <c>LockShared</c> serialization, share-report for the linter).
/// These are standalone classes; the engine wiring connects them afterwards.
/// </summary>
public sealed class StateTests : IDisposable
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
                // Best effort: temp cleanup must never fail a test.
            }
        }
    }

    private string NewConfigurationsFolder()
    {
        string root = Path.Combine(Path.GetTempPath(), "mcc-beacon-p7-" + Guid.NewGuid().ToString("N"));
        string folder = Path.Combine(root, "configurations");
        Directory.CreateDirectory(folder);
        _roots.Add(root);
        return folder;
    }

    private static BeaconMapValue SeenMap(params (string Player, double Count)[] entries)
    {
        var dict = new Dictionary<string, BeaconValue>(StringComparer.Ordinal);
        foreach ((string player, double count) in entries)
            dict[player] = BeaconValue.Number(count);

        return BeaconValue.Map(dict);
    }

    #region saved: round trips

    [Fact]
    public void GreeterRemember_RoundTripAcrossInstances()
    {
        string folder = NewConfigurationsFolder();
        var first = new BeaconSavedState(folder);
        first.Set("greeter", "seen", SeenMap(("Steve", 1)));
        first.Save("greeter");

        // A fresh instance (a restart) reloads from disk.
        var second = new BeaconSavedState(folder);
        second.Load("greeter");

        Assert.True(second.TryGet("greeter", "seen", out BeaconValue? seen));
        var map = Assert.IsType<BeaconMapValue>(seen);
        Assert.Equal(1, ((BeaconNumberValue)map.Entries["Steve"]).Value);
    }

    [Fact]
    public void Ledger_AccumulatesAcrossReloads()
    {
        string folder = NewConfigurationsFolder();
        for (int day = 1; day <= 3; day++)
        {
            var state = new BeaconSavedState(folder);
            state.Load("shopkeeper");
            BeaconMapValue ledger = state.TryGet("shopkeeper", "ledger", out BeaconValue? found) && found is BeaconMapValue m
                ? m
                : BeaconValue.Map(new Dictionary<string, BeaconValue>(StringComparer.Ordinal));
            var grown = new Dictionary<string, BeaconValue>(ledger.Entries, StringComparer.Ordinal)
            {
                [$"day{day}"] = BeaconValue.Number(day * 100),
            };
            state.Set("shopkeeper", "ledger", BeaconValue.Map(grown));
            state.Save("shopkeeper");
        }

        var final = new BeaconSavedState(folder);
        final.Load("shopkeeper");
        Assert.True(final.TryGet("shopkeeper", "ledger", out BeaconValue? ledgerValue));
        var finalMap = Assert.IsType<BeaconMapValue>(ledgerValue);
        Assert.Equal(3, finalMap.Entries.Count);
        Assert.Equal(300, ((BeaconNumberValue)finalMap.Entries["day3"]).Value);
    }

    [Fact]
    public void MissingKey_ReadsAsNone_SoOrDefaultIdiomWorks()
    {
        var state = new BeaconSavedState(NewConfigurationsFolder());
        Assert.False(state.TryGet("greeter", "seen", out _));
        Assert.Equal(BeaconValue.None, state.GetOrNone("greeter", "unseen"));
    }

    [Fact]
    public void MissingFile_LoadsEmptyWithoutWriting()
    {
        string folder = NewConfigurationsFolder();
        var state = new BeaconSavedState(folder);
        state.Load("fresh");
        Assert.Empty(state.Snapshot("fresh"));
        Assert.False(File.Exists(Path.Combine(folder, "beacon", "fresh.toml")));
    }

    [Fact]
    public void PlainLoad_NeverRewrites()
    {
        string folder = NewConfigurationsFolder();
        var state = new BeaconSavedState(folder);
        state.Set("greeter", "seen", SeenMap(("Steve", 2)));
        state.Save("greeter");
        string path = state.ResolveStatePath("greeter");
        byte[] before = File.ReadAllBytes(path);

        state.Load("greeter");

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void CorruptFile_LoadThrowsNamingFile()
    {
        string folder = NewConfigurationsFolder();
        var state = new BeaconSavedState(folder);
        string path = state.ResolveStatePath("greeter");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "seen = [unclosed\n");

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => state.Load("greeter"));
        Assert.Contains(path, ex.Message, StringComparison.Ordinal);
    }

    #endregion
    #region saved: atomicity

    [Fact]
    public void CrashMidSave_NeverTearsFile()
    {
        string folder = NewConfigurationsFolder();
        var state = new BeaconSavedState(folder);
        state.Set("greeter", "seen", SeenMap(("Steve", 7)));
        state.Save("greeter");
        string path = state.ResolveStatePath("greeter");
        byte[] intact = File.ReadAllBytes(path);

        // Fault injection: die after the tmp file is fully flushed, before the rename.
        state.BeforeRenameForTests = _ => throw new IOException("simulated crash mid-save");
        for (int i = 0; i < 10; i++)
        {
            state.Set("greeter", "seen", SeenMap(("Steve", 100 + i)));
            Assert.Throws<IOException>(() => state.Save("greeter"));
            Assert.Equal(intact, File.ReadAllBytes(path));
        }

        // The survivor still parses with the pre-crash values.
        state.BeforeRenameForTests = null;
        var reloaded = new BeaconSavedState(folder);
        reloaded.Load("greeter");
        Assert.True(reloaded.TryGet("greeter", "seen", out BeaconValue? seen));
        Assert.Equal(7, ((BeaconNumberValue)((BeaconMapValue)seen!).Entries["Steve"]).Value);
    }

    [Fact]
    public void PerScript_FilesIsolated()
    {
        string folder = NewConfigurationsFolder();
        var state = new BeaconSavedState(folder);
        state.Set("alpha", "warns", BeaconValue.Number(1));
        state.Set("beta", "warns", BeaconValue.Number(2));
        state.Save("alpha");
        state.Save("beta");

        Assert.NotEqual(state.ResolveStatePath("alpha"), state.ResolveStatePath("beta"));
        var reloaded = new BeaconSavedState(folder);
        reloaded.Load("alpha");
        reloaded.Load("beta");
        Assert.Equal(1, ((BeaconNumberValue)reloaded.GetOrNone("alpha", "warns")).Value);
        Assert.Equal(2, ((BeaconNumberValue)reloaded.GetOrNone("beta", "warns")).Value);
    }

    #endregion
    #region saved: jail / secrets proof

    public static TheoryData<string> EscapeIds => new()
    {
        "../../accounts.toml",
        "..\\accounts.toml",
        "/etc/passwd",
        "a/b",
        ".",
        "..",
    };

    [Theory]
    [MemberData(nameof(EscapeIds))]
    public void EscapeAttempts_RefusedWithActionableMessage(string scriptId)
    {
        string folder = NewConfigurationsFolder();
        string accounts = Path.Combine(folder, "accounts.toml");
        byte[] sentinel = "sentinel-bytes-do-not-touch"u8.ToArray();
        File.WriteAllBytes(accounts, sentinel);
        var state = new BeaconSavedState(folder);

        BeaconStateEscapeException ex = Assert.Throws<BeaconStateEscapeException>(
            () => state.ResolveStatePath(scriptId));
        Assert.Contains(state.BeaconDirectory, ex.Message, StringComparison.Ordinal);
        Assert.Throws<BeaconStateEscapeException>(() => state.Load(scriptId));
        Assert.Throws<BeaconStateEscapeException>(
            () => state.Set(scriptId, "k", BeaconValue.Number(1)));

        // Byte-proof: the escape never wrote outside the jail.
        Assert.Equal(sentinel, File.ReadAllBytes(accounts));
    }

    [Fact]
    public void SymlinkStateFile_Refused_AccountsUntouched()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        string folder = NewConfigurationsFolder();
        string accounts = Path.Combine(folder, "accounts.toml");
        byte[] sentinel = "symlink-sentinel"u8.ToArray();
        File.WriteAllBytes(accounts, sentinel);
        var state = new BeaconSavedState(folder);
        Directory.CreateDirectory(state.BeaconDirectory);
        File.CreateSymbolicLink(Path.Combine(state.BeaconDirectory, "evil.toml"), accounts);

        BeaconStateEscapeException ex = Assert.Throws<BeaconStateEscapeException>(
            () => state.ResolveStatePath("evil"));
        Assert.Contains("symbolic link", ex.Message, StringComparison.OrdinalIgnoreCase);
        var writer = new BeaconSavedState(folder);
        writer.Set("evil", "seen", SeenMap(("Steve", 1)));
        Assert.Throws<BeaconStateEscapeException>(() => writer.Save("evil"));

        Assert.Equal(sentinel, File.ReadAllBytes(accounts));
    }

    [Fact]
    public void ResolvedPaths_NeverLeaveJail()
    {
        string folder = NewConfigurationsFolder();
        string accounts = Path.Combine(folder, "accounts.toml");
        byte[] sentinel = "jail-sentinel"u8.ToArray();
        File.WriteAllBytes(accounts, sentinel);
        var state = new BeaconSavedState(folder);
        string root = state.BeaconDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        foreach (string id in new[] { "greeter", "shop-keeper_v2", "a.b", "UPPER9" })
        {
            string resolved = state.ResolveStatePath(id);
            Assert.StartsWith(root, resolved, StringComparison.Ordinal);
        }

        foreach (string id in new[] { "../../accounts.toml", "..", ".", "x/y", "x\\y" })
            Assert.Throws<BeaconStateEscapeException>(() => state.ResolveStatePath(id));

        Assert.Equal(sentinel, File.ReadAllBytes(accounts));
    }

    [Fact]
    public void ScriptId_NameLimits()
    {
        var state = new BeaconSavedState(NewConfigurationsFolder());
        ArgumentException tooLong = Assert.Throws<ArgumentException>(
            () => state.ResolveStatePath(new string('a', 65)));
        Assert.Contains("64", tooLong.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => state.ResolveStatePath(string.Empty));
        Assert.Throws<ArgumentException>(() => state.ResolveStatePath("has space"));
    }

    #endregion
    #region saved: quotas

    [Fact]
    public void Saved_KeyCountQuota_RefusedNamingLimit()
    {
        var state = new BeaconSavedState(NewConfigurationsFolder());
        for (int i = 0; i < BeaconSavedState.MaxKeys; i++)
            state.Set("ledger", $"k{i}", BeaconValue.Number(i));

        // Updating an existing key at the cap is fine.
        state.Set("ledger", "k0", BeaconValue.Number(-1));

        BeaconQuotaExceededException ex = Assert.Throws<BeaconQuotaExceededException>(
            () => state.Set("ledger", "one-too-many", BeaconValue.Number(0)));
        Assert.Contains(BeaconSavedState.MaxKeys.ToString(), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Saved_ValueSizeQuota_RefusedNamingLimit()
    {
        var state = new BeaconSavedState(NewConfigurationsFolder());
        string oversized = new string('x', (int)BeaconSavedState.MaxValueBytes);

        BeaconQuotaExceededException ex = Assert.Throws<BeaconQuotaExceededException>(
            () => state.Set("greeter", "blob", BeaconValue.Text(oversized)));
        Assert.Contains(BeaconSavedState.MaxValueBytes.ToString(), ex.Message, StringComparison.Ordinal);

        // Just under the limit fits.
        state.Set("greeter", "ok", BeaconValue.Text(new string('x', (int)BeaconSavedState.MaxValueBytes - 3)));
    }

    #endregion
    #region shared

    [Fact]
    public void Shared_IsSet_BeforeRead()
    {
        var shared = new BeaconSharedState();
        Assert.False(shared.IsSet("shop.price.bread"));
        Assert.False(shared.TryGet("shop.price.bread", out _));

        shared.Set("shop", "shop.price.bread", BeaconValue.Number(3));

        Assert.True(shared.IsSet("shop.price.bread"));
        Assert.True(shared.TryGet("shop.price.bread", out BeaconValue? value));
        Assert.Equal(3, ((BeaconNumberValue)value!).Value);
    }

    [Fact]
    public void Shared_LockShared_SerializesPatrolVsChatWarnRace()
    {
        var shared = new BeaconSharedState();
        shared.Set("patrol", "warns.alex", BeaconValue.Number(0));
        const int iterations = 200;

        Parallel.For(0, iterations, i =>
        {
            string owner = i % 2 == 0 ? "patrol" : "chat";
            shared.LockShared(() =>
            {
                shared.TryGet("warns.alex", out BeaconValue? current);
                double next = current is BeaconNumberValue n ? n.Value + 1 : 1;
                shared.Set(owner, "warns.alex", BeaconValue.Number(next));
            });
        });

        Assert.True(shared.TryGet("warns.alex", out BeaconValue? final));
        Assert.Equal(iterations, ((BeaconNumberValue)final!).Value);
    }

    [Fact]
    public void Shared_LockShared_ReturnsValue_AndNests()
    {
        var shared = new BeaconSharedState();
        int answer = shared.LockShared(() => shared.LockShared(() => 42));
        Assert.Equal(42, answer);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData(".shop")]
    [InlineData("shop.")]
    [InlineData("shop..price")]
    [InlineData("shop price")]
    public void Shared_KeyShape_Validated(string key)
    {
        var shared = new BeaconSharedState();
        Assert.Throws<ArgumentException>(() => shared.Set("shop", key, BeaconValue.Number(1)));
        Assert.Throws<ArgumentException>(() => shared.IsSet(key));
    }

    [Fact]
    public void Shared_Quotas_RefusedNamingLimits()
    {
        var shared = new BeaconSharedState();
        string oversized = new string('x', (int)BeaconSharedState.MaxValueBytes);
        BeaconQuotaExceededException sizeEx = Assert.Throws<BeaconQuotaExceededException>(
            () => shared.Set("shop", "shop.blob", BeaconValue.Text(oversized)));
        Assert.Contains(BeaconSharedState.MaxValueBytes.ToString(), sizeEx.Message, StringComparison.Ordinal);

        for (int i = 0; i < BeaconSharedState.MaxKeys; i++)
            shared.Set("filler", $"k{i}.v", BeaconValue.Number(i));

        BeaconQuotaExceededException countEx = Assert.Throws<BeaconQuotaExceededException>(
            () => shared.Set("shop", "shop.one.too.many", BeaconValue.Number(0)));
        Assert.Contains(BeaconSharedState.MaxKeys.ToString(), countEx.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShareReport_RecordsWhoSharesWhat()
    {
        var shared = new BeaconSharedState();
        shared.Set("shop", "shop.price.bread", BeaconValue.Number(3));
        shared.Set("shop", "shop.price.milk", BeaconValue.Number(5));
        shared.Set("econ", "econ.tax", BeaconValue.Number(0.1));
        Assert.True(shared.TryGet("quiz", "shop.price.bread", out _));

        IReadOnlyList<BeaconShareEntry> report = shared.GetShareReport();
        Assert.Equal(4, report.Count);

        IReadOnlyList<BeaconShareEntry> shop = shared.GetShareReport("shop");
        Assert.Equal(2, shop.Count);
        Assert.All(shop, e => Assert.Equal("shop", e.ScriptId));
        Assert.Contains(shop, e => e.Key == "shop.price.bread" && e.Access == BeaconShareAccess.Write
            && e.ValueKind == BeaconValueKind.Number);

        Assert.Contains(report, e => e.ScriptId == "quiz" && e.Key == "shop.price.bread"
            && e.Access == BeaconShareAccess.Read);
        Assert.Contains(report, e => e.ScriptId == "econ" && e.Key == "econ.tax");
    }
    #endregion
}
