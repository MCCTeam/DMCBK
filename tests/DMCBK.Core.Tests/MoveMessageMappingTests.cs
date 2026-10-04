using System.Globalization;
using DMCBK.Core.Commands.Impl;
using Umpk.Geometry;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// <c>move</c>'s arrival VERDICT is <see cref="Umpk.Client.Navigation.Navigator"/>'s job and is pinned by UMPK's own tests.
/// What is left on the MCC side is the MESSAGE.
/// <para>
/// They used to pin this client's own wording, "Walking to {target}.", derived from a MoveResult after the move finished.
/// The legacy client announces the move BEFORE it walks and names both ends: <c>cmd.move.walk</c> is "Walking from {1} to {0}".
/// Two things in that are easy to get wrong and are what these tests exist to catch: the corpus orders the placeholders TARGET first, and positions render in legacy's own two-decimal "X:.. Y:.. Z:.." shape rather than as bare numbers.
/// </para>
/// </summary>
public sealed class MoveMessageMappingTests : IDisposable
{
    private readonly CultureInfo _previous = CultureInfo.CurrentCulture;

    // Positions render through CurrentCulture, exactly as legacy's Location.ToString() did, so on a locale with a comma decimal separator the client prints "X:17,45".
    // That is legacy behaviour and is kept; it just means these assertions have to name the culture they are written for rather than inheriting the developer's.
    public MoveMessageMappingTests() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

    public void Dispose() => CultureInfo.CurrentCulture = _previous;

    [Fact]
    public void Walk_NamesBothEnds_FromCurrentToTarget()
    {
        string message = MovementCommandText.Walk(
            target: new Vec3d(17, 65, -28), current: new Vec3d(17.453, 65.0, -27.50));

        // "from" is the CURRENT position and "to" is the TARGET.
        // A swapped argument order still produces a grammatical sentence, which is exactly why it needs pinning.
        Assert.Equal("Walking from X:17.45 Y:65.00 Z:-27.50 to X:17.00 Y:65.00 Z:-28.00", message);
    }

    [Fact]
    public void Walk_RendersPositionsInTheLegacyLocationShape()
    {
        string message = MovementCommandText.Walk(
            target: new Vec3d(100.5, 80, 100.5), current: new Vec3d(100.52, 80.0, 100.48));

        Assert.Equal("Walking from X:100.52 Y:80.00 Z:100.48 to X:100.50 Y:80.00 Z:100.50", message);
    }

    [Fact]
    public void Fail_NamesTheTarget()
        => Assert.Equal(
            "Failed to compute path to X:17.00 Y:65.00 Z:-28.00",
            MovementCommandText.Fail(new Vec3d(17, 65, -28)));

    [Fact]
    public void SuggestForce_PointsAtTheForceFlag()
    {
        string message = MovementCommandText.SuggestForce(new Vec3d(8, 64, 8));

        Assert.Equal(
            "Failed to compute a safe path to X:8.00 Y:64.00 Z:8.00. Try -f parameter to allow unsafe movements.",
            message);
    }

    [Fact]
    public void Position_MatchesLegacyLocationToString()
        => Assert.Equal("X:1.50 Y:-2.25 Z:300.00", MovementCommandText.Position(new Vec3d(1.5, -2.25, 300)));
}
