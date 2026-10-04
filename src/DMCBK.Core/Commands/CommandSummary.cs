namespace DMCBK.Core.Commands;

/// <summary>One internal command described for bridges: name, usage grammar, and description.</summary>
/// <param name="Name">The command name.</param>
/// <param name="Usage">The usage grammar (never a sentence).</param>
/// <param name="Description">The description, starting with a capital and ending with a full stop.</param>
public sealed record CommandSummary(string Name, string Usage, string Description);
