using System.Collections.Immutable;

namespace DMCBK.Core.Commands;

/// <summary>Command metadata, grammar and current capability states for host-owned help rendering.</summary>
public sealed record CommandHelpPresentation(CommandBase Command, ImmutableArray<string> DerivedUsage,
    char Prefix, bool NoPrefix, bool Terrain, bool Inventory, bool Entities, bool Physics, bool Pathfinding);
