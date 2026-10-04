namespace DMCBK.Core.Configuration;

/// <summary>
/// A non-fatal issue found while validating configuration: a clamped value, a normalized or defaulted unknown enum, an unknown key, or a derivation notice.
/// Surfaced by the loader so hosts can log them; the snapshot itself is always usable (validation never throws for recoverable input).
/// </summary>
public sealed record ConfigurationWarning(string Message);
