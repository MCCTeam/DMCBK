using System.Collections.Immutable;

namespace DMCBK.Core.Commands;

/// <summary>
/// An immutable description of one currently registered command.
/// Unlike the rendered help page, this is structured host-facing data, so a TUI, MCP bridge, or other host can search and lay it out without parsing localized console text.
/// </summary>
public sealed record CommandDescriptor(
    string Name,
    string Description,
    string Usage,
    CommandCategory Category,
    ImmutableArray<string> Aliases,
    ImmutableArray<UsageLine> UsageLines,
    ImmutableArray<UsageFlag> Flags,
    ImmutableArray<string> Examples,
    CommandFeature Requirements,
    ImmutableArray<string> RelatedCommands,
    string? ManualTopic,
    bool ShowInIndex);
