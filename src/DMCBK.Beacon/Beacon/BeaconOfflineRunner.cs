namespace DMCBK.Core.Beacon;
/// <summary>Deterministic offline interpretation using explicit source, seed and clock advance.</summary>
public static class BeaconOfflineRunner
{
    /// <summary>Runs one source and returns diagnostics and captured game-free output.</summary>
    public static async Task<BeaconRunReport> RunSourceAsync(
        string path, string source, int seed, double tickSeconds)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(tickSeconds);
        if (!double.IsFinite(tickSeconds)) throw new ArgumentOutOfRangeException(nameof(tickSeconds));
        var clock = new VirtualClock();
        var rng = new SeededRng(seed);
        var engine = new BeaconEngine(BeaconOfflineHost.Shared, clock, rng, new FuelBudget());
        string id = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(id))
            id = "stdin";

        BeaconRunResult result;
        try
        {
            // Full path, not the bare file name: imports resolve against the importing file, and lint already checked them there.
            // Passing the bare id would silently rebase relative imports onto the working directory.
            result = await engine.RunScriptAsync(
                id, source, fileName: Path.GetFullPath(path)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var diagnostic = new BeaconDiagnostic(
                BeaconDiagnosticCodes.Parse,
                BeaconSeverity.Error,
                $"run '{path}' failed before lint: {ex.Message}",
                new SourceSpan(Path.GetFileName(path), 1, 1, 0),
                "Lint the file first; headless run never executes a file lint refuses.");
            return new BeaconRunReport(path, false, [diagnostic], [], []);
        }

        var timerOutput = new List<string>(result.LocalOutput);
        var timerDiagnostics = new List<BeaconDiagnostic>(result.Diagnostics);
        if (!result.Success && result.Error is { } topError)
            timerDiagnostics.Add(topError);

        if (result.Success && tickSeconds > 0)
        {
            clock.Advance(TimeSpan.FromSeconds(tickSeconds));
            try
            {
                foreach (BeaconEveryRun every in await engine.TickEveryAsync().ConfigureAwait(false))
                {
                    timerOutput.AddRange(every.Result.LocalOutput);
                    timerDiagnostics.AddRange(every.Result.Diagnostics);
                    if (!every.Result.Success && every.Result.Error is { } timerError)
                        timerDiagnostics.Add(timerError);
                }

                foreach (BeaconOnceRun once in await engine.TickOnceAsync().ConfigureAwait(false))
                {
                    timerOutput.AddRange(once.Result.LocalOutput);
                    timerDiagnostics.AddRange(once.Result.Diagnostics);
                    if (!once.Result.Success && once.Result.Error is { } onceError)
                        timerDiagnostics.Add(onceError);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                timerDiagnostics.Add(new BeaconDiagnostic(
                    BeaconDiagnosticCodes.Parse,
                    BeaconSeverity.Error,
                    $"run '{path}' timer tick failed: {ex.Message}",
                    new SourceSpan(Path.GetFileName(path), 1, 1, 0),
                    "Run with --tick 0 to skip timers."));
            }
        }

        bool ok = result.Success && timerDiagnostics.All(d => d.Severity != BeaconSeverity.Error);
        return new BeaconRunReport(path, ok, timerDiagnostics, timerOutput, result.PassthroughLog);
    }
}

/// <summary>One headless run report: whether the file ran clean, its diagnostics, and its local output.</summary>
/// <param name="Path">The file path as given.</param>
/// <param name="Ok">True when the run succeeded with no error diagnostic.</param>
/// <param name="Diagnostics">All diagnostics, sorted by line then column.</param>
/// <param name="Output">Local <c>show</c> output lines, in order.</param>
/// <param name="Passthrough">Audit entries for outside-world verbs.</param>
public sealed record BeaconRunReport(
    string Path,
    bool Ok,
    IReadOnlyList<BeaconDiagnostic> Diagnostics,
    IReadOnlyList<string> Output,
    IReadOnlyList<BeaconPassthroughLog> Passthrough);
