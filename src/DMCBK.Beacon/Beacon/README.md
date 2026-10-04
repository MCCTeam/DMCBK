# Beacon (DMCBK.Core side)

Beacon is MCC's scripting language (`.mcc` files in `scripts/`). This folder is the engine home.

Boundary: everything here stays console-free (`System.Console` is analyzer-banned in DMCBK.Core).
The engine reports findings as `BeaconDiagnostic` values with stable codes; only `Mcc.Cli`
formats them for display (classic console or TUI) or as headless `lint` output.

Seams frozen in Phase 1: `SourceSpan` (span shape through desugar), `BeaconDiagnostic` plus
`BeaconDiagnosticCodes` (`B0001` parse, `B1xxx` manifests, `B2xxx` names/events, `B3xxx`
strictness), `IVirtualClock`, `ISeededRng`, `IFuelBudget`, and `IBeaconHostServices`
(chat send, command dispatch, session reads, storage IO). Changing a seam later requires a
migration note in the depending phase files.
