using System.Globalization;
using DMCBK.Core.Commands;
using Microsoft.Extensions.Logging;
using Umpk;
using Umpk.Client;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// <c>move</c>'s catch-all used to swallow the reason: every navigation failure read the same line no matter what the library threw, so a transcript could not tell "the planner found nothing" apart from "the planner found a path and running it failed".
///
/// <para>
/// Where the reason surfaces has since changed, and this file says so rather than describing the shape it used to have.
/// Every <c>move</c> failure uses its legacy wording, because wording parity with the 1.x client is a deliberate constraint: the user-visible line is "Failed to compute path to &lt;position&gt;" (or the <c>-f</c> suggestion), and it carries no reason.
/// So <c>MoveCommand.DoMove</c> reports the legacy line and logs the underlying exception message at DEBUG instead, where the host's level filtering decides who sees it.
/// </para>
///
/// <para>
/// What is pinned here, and what is not.
/// <see cref="Move_ReportsTheLegacyReasonlessLine"/> covers the user-facing half end to end through the real dispatcher, and <see cref="CommandContextLogger_ReachesTheHostsOwnLoggerFactory"/> covers the channel the catch writes into, which is the half that can rot silently (a command surface wired to <c>NullLoggerFactory</c> makes every such call a no-op nobody notices).
/// The catch BODY itself is not exercised: reaching it needs the navigator to throw, and every throw it has (no path, replans exhausted, held movement lease) needs either loaded terrain or a plugin holding the lease, neither of which a bound-but-idle session has.
/// That is stated rather than faked, because a mock navigator would pin the mock and not the command.
/// </para>
///
/// <para>
/// <c>CommandStrings.MoveCannotReachWithReason</c> is the reason-bearing line.
/// It has NO production call site today, exactly because the user-facing wording went back to legacy, and the rest of this file pins its shape against the day a caller wants it again: the diagnostic prefix, the separator, the reason last and verbatim, no punctuation the format invented, and the invariant-culture route through <c>CommandStrings.F</c>.
/// Those tests exercise the formatter directly and say nothing about what <c>DoMove</c> prints.
/// </para>
/// </summary>
public sealed class MoveFailureReasonTests
{
    private const string Prefix = "Could not reach the destination: ";

    /// <summary>
    /// End to end through the real dispatcher against a bound-but-idle session: a <c>move</c> that does not arrive reports the LEGACY line and nothing else.
    /// This is the constraint the debug logging exists to respect, so it is pinned separately from the logging: a change that "helpfully" put the reason back on the user's line would break wording parity with the 1.x client, which was a deliberate decision and not an oversight to correct.
    /// </summary>
    [Fact]
    public async Task Move_ReportsTheLegacyReasonlessLine()
    {
        await using Client client = new ClientBuilder().UseCommands().UseBeacon()
            .UseUsername("Tester")
            .UseServer("localhost")
            .Build();
        await using UmpkClient umpk = IdleUmpkClient();
        client.AttachIdleSessionForTesting(umpk);

        CmdResult result = await client.Commands.DispatchAsync("move center");
        string message = result.Message ?? string.Empty;

        Assert.NotEqual(CmdStatus.Done, result.Status);
        Assert.StartsWith("Failed to compute path to", message, StringComparison.Ordinal);
        Assert.DoesNotContain(Prefix, message, StringComparison.Ordinal);
        Assert.DoesNotContain("Could not reach the destination", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The channel <c>DoMove</c>'s catch writes the swallowed reason into.
    /// It has to be the HOST's factory, not <c>NullLoggerFactory</c>: the command surface had no logger at all before this, so the seam itself is the fix and a regression to a null logger would turn every such call back into the silent discard it replaced, with no test failing anywhere else.
    /// </summary>
    [Fact]
    public async Task CommandContextLogger_ReachesTheHostsOwnLoggerFactory()
    {
        var logs = new CapturingLoggerFactory();
        await using Client client = new ClientBuilder().UseCommands().UseBeacon()
            .UseUsername("Tester")
            .UseServer("localhost")
            .UseLoggerFactory(logs)
            .Build();

        var ctx = new CommandContext(
            client,
            NullCommandOutput.Instance,
            ui: null,
            config: client.Configuration,
            variables: client.Variables,
            features: new Umpk.Client.ClientFeatures(),
            commands: client.Commands,
            cancellation: CancellationToken.None);

        ctx.Logger.LogDebug(new InvalidOperationException("No path to the goal was found."), "move to {Target} failed: {Reason}", "X:0.50 Y:0.00 Z:0.50", "No path to the goal was found.");

        (LogLevel Level, string Message, Exception? Error) logged = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Debug, logged.Level);
        Assert.Equal("move to X:0.50 Y:0.00 Z:0.50 failed: No path to the goal was found.", logged.Message);
        Assert.IsType<InvalidOperationException>(logged.Error);
    }

    /// <summary>
    /// A built-but-unconnected UMPK client.
    /// Same trick <c>FalseSuccessReportingTests</c> uses: capabilities resolve from the version at construction, so a bound-but-idle session lets the command's real branches run without a server.
    /// </summary>
    private static UmpkClient IdleUmpkClient()
    {
        Assert.True(Umpk.Data.Java.JavaVersions.TryGetByName("1.21.5", out Umpk.Protocol.Java.JavaVersion? version));
        return new UmpkClientBuilder()
            .UseVersion(version!)
            .UseProfile(new GameProfile(Guid.NewGuid(), "Tester"))
            .Build();
    }

    /// <summary>Records every emitted entry with its level and its exception.</summary>
    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public List<(LogLevel Level, string Message, Exception? Error)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Recorder(Entries);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class Recorder(List<(LogLevel Level, string Message, Exception? Error)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (entries)
                    entries.Add((logLevel, formatter(state, exception), exception));
            }
        }
    }

    /// <summary>
    /// The whole line, pinned exactly.
    /// The reason is the planner's own <c>InvalidOperationException("No path to the goal was found.")</c> from <c>Navigator.MoveToAsync</c>, which is the failure that motivated the fix.
    /// </summary>
    [Fact]
    public void TheReasonFollowsTheDestinationPrefix()
    {
        Assert.Equal(
            "Could not reach the destination: No path to the goal was found.",
            CommandStrings.MoveCannotReachWithReason("No path to the goal was found."));
    }

    /// <summary>
    /// Argument order, over the four failures a <c>move</c> can actually surface: the three planner/replan messages <c>Navigator</c> throws and the held-movement-lease one.
    /// The reason is the TAIL, and a member that emitted "No path to the goal was found.: Could not reach the destination" would still contain both halves, so containment is not enough and every case pins the full line.
    /// </summary>
    [Theory]
    [InlineData("No path to the goal was found.")]
    [InlineData("Navigation failed after exhausting replans.")]
    [InlineData("Replan produced no path to the goal.")]
    [InlineData("Movement is held by 'Farmer'.")]
    public void EveryLibraryFailureKeepsItsOwnReasonLast(string reason)
    {
        string line = CommandStrings.MoveCannotReachWithReason(reason);

        Assert.Equal(Prefix + reason, line);
        Assert.EndsWith(reason, line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The format contributes no terminal punctuation of its own.
    /// Reasons are exception messages and bring their own full stop, so a period added to the format would read "...was found..".
    /// </summary>
    [Fact]
    public void TheFormatAddsNoPunctuationOfItsOwn()
    {
        string sentence = CommandStrings.MoveCannotReachWithReason("No path to the goal was found.");

        Assert.EndsWith("was found.", sentence, StringComparison.Ordinal);
        Assert.DoesNotContain("..", sentence, StringComparison.Ordinal);

        // A reason that brought no full stop is not given one either: the reason is reproduced, not edited.
        Assert.Equal("Could not reach the destination: timed out", CommandStrings.MoveCannotReachWithReason("timed out"));
    }

    /// <summary>
    /// The reason is an ARGUMENT, never part of the template.
    /// Exception messages routinely carry braces (the lease message is itself interpolated, a payload can be serialized into one), and folding the reason into the format before <c>string.Format</c> ran would either throw <see cref="FormatException"/> or silently eat the braces.
    /// </summary>
    [Theory]
    [InlineData("Movement is held by '{0}'.")]
    [InlineData("Unbalanced brace { in a payload")]
    [InlineData("{{escaped}}")]
    public void ABracedReasonIsCopiedVerbatim(string reason)
    {
        Assert.Equal(Prefix + reason, CommandStrings.MoveCannotReachWithReason(reason));
    }

    /// <summary>
    /// <c>CommandStrings.F</c> formats with <see cref="CultureInfo.InvariantCulture"/> and this member has to keep that route.
    /// Today the single placeholder is a string, so no ambient culture can move the line; the pin is what protects that if a formatted argument (a distance, a timeout) is ever added.
    /// Each case runs on a thread of its own so no ambient culture leaks into the shared test threads.
    /// </summary>
    [Theory]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    [InlineData("ar-SA")]
    public void TheLineDoesNotDependOnTheAmbientCulture(string culture)
    {
        const string reason = "Navigation failed after exhausting replans.";

        Assert.Equal(CommandStrings.MoveCannotReachWithReason(reason), FormatUnderCulture(culture, reason));
    }

    /// <summary>
    /// A failure reported through this member always carries a reason, which is the entire point of it: the reasonless line must not be reachable again, and the message must not be confusable with the arrival reports <c>Navigator.Judge</c> produces.
    /// </summary>
    [Fact]
    public void AFailureAlwaysCarriesAReason()
    {
        string line = CommandStrings.MoveCannotReachWithReason("No path to the goal was found.");

        Assert.NotEqual("Could not reach the destination.", line);
        Assert.Contains(": ", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Walking to", line, StringComparison.Ordinal);
        Assert.DoesNotContain(CommandStrings.MoveArrived, line, StringComparison.Ordinal);
    }

    /// <summary>Formats the message with <paramref name="culture"/> as the ambient culture of its own thread.</summary>
    private static string FormatUnderCulture(string culture, string reason)
    {
        string? line = null;
        var worker = new Thread(() =>
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            line = CommandStrings.MoveCannotReachWithReason(reason);
        });

        worker.Start();
        worker.Join();
        return line!;
    }
}
