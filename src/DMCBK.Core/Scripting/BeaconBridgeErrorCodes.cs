namespace DMCBK.Core.Beacon;
/// <summary>
/// Runtime-only bridge error codes (the B4xxx family, catchable via <c>try/catch err</c> like the verb and movement failures; they never appear in lint output, which is why they live outside <see cref="BeaconDiagnosticCodes"/> alongside <c>task error codes</c>).
/// </summary>
public static class BeaconBridgeErrorCodes
{
    /// <summary>An <c>extern</c>, <c>export</c> or <c>call</c> target is missing or mismatched.</summary>
    public const string BridgeCall = "B4012";
}
