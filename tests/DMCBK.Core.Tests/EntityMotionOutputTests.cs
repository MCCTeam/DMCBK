using DMCBK.Core.Commands;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// <c>/entity &lt;id&gt;</c> has to PRINT the tracked velocity.
/// <c>EntitySnapshot</c> has carried <c>Velocity</c> all along and <c>EntityDetail</c> printed only the position, so there was no surface at all on which a user, a plugin author or a live probe could read what the server last said an entity's motion was.
/// That is what made the unapplied <c>add_entity</c> spawn velocity invisible from the outside: the value was zero and nothing displayed it either way.
/// </summary>
public sealed class EntityMotionOutputTests
{
    /// <summary>
    /// The motion is on the line, and at enough precision to compare against the server's own <c>data get entity &lt;selector&gt; Motion</c>.
    /// Position stays at one decimal (a block position does not need more); velocity is blocks per TICK, so 0.6 printed as "0.6" and a quantized -0.39998779 printed as "-0.4" would make a live comparison meaningless.
    /// </summary>
    [Fact]
    public void EntityDetail_CarriesTheMotion_AtComparablePrecision()
    {
        string line = CommandStrings.EntityDetail(
            4242, "minecraft:ender_pearl", string.Empty,
            8.5, 65.0, -3.5,
            0.6, 0.3, -0.45);

        Assert.Equal(
            "Entity #4242 minecraft:ender_pearl '' at 8.5 65.0 -3.5 motion 0.6 0.3 -0.45",
            line);
    }

    /// <summary>
    /// A quantized modern velocity has to survive the formatting: the 1.21.9+ low-precision block decodes to values like -0.39998779, and rounding that to one decimal would make it indistinguishable from a hand-written -0.4 and from a value the client invented.
    /// </summary>
    [Fact]
    public void EntityDetail_DoesNotRoundAwayAQuantizedVelocity()
    {
        string line = CommandStrings.EntityDetail(
            7, "minecraft:cow", "Bessie",
            0.5, 65.0, 0.5,
            -0.39998779222364644, 0.36080083012879194, 0.0);

        Assert.Contains("motion -0.39999 0.3608 0", line, StringComparison.Ordinal);
    }

    /// <summary>An entity at rest still reports its motion, rather than the field being omitted.</summary>
    [Fact]
    public void EntityDetail_AtRest_StillReportsZeroMotion()
    {
        string line = CommandStrings.EntityDetail(1, "minecraft:cow", string.Empty, 0.0, 64.0, 0.0, 0.0, 0.0, 0.0);

        Assert.Contains("motion 0 0 0", line, StringComparison.Ordinal);
    }
}
