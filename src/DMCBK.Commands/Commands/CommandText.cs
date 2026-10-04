using DMCBK.Core.Localization;

namespace DMCBK.Core.Commands;

/// <summary>
/// Wording shared by more than one command, so there is one copy of each string's composition rather than one per file.
/// </summary>
internal static class CommandText
{
    /// <summary>
    /// The offline reply.
    /// The legacy client never entered a command body without a session: its console loop answered with <c>mcc.disconnected</c> and stopped there (Program.cs:1059), so that is the wording every session guard uses.
    /// <para>
    /// The message names the prefix the user actually has to type, which is configurable (<c>Permissions.CommandPrefix</c>), so it is composed rather than constant.
    /// </para>
    /// </summary>
    public static string NotConnected(ICommandDispatcher commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        return NotConnected(commands.NoPrefix, commands.Prefix);
    }

    /// <inheritdoc cref="NotConnected(ICommandDispatcher)"/>
    public static string NotConnected(bool noPrefix, char prefix)
        => McStrings.Format("mcc.disconnected", noPrefix ? string.Empty : prefix.ToString());

    /// <summary>Convenience overload for a command body, which always has its service to hand.</summary>
    public static string NotConnected(CommandContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return NotConnected(ctx.Commands);
    }
}
