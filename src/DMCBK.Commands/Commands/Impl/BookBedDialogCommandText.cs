using System.Globalization;
using System.Text;
using DMCBK.Core.Commands.ArgumentTypes;
using DMCBK.Core.Localization;
using Umpk.Client.Actions;
using Umpk.Client.Navigation;
using Umpk.Commands;
using Umpk.Geometry;

namespace DMCBK.Core.Commands.Impl;

/// <summary>
/// Text the book/bed/dialog commands share.
/// Kept here rather than pulled from a neighbouring command file so this file stands on its own; the consolidation pass can merge it with the identical helpers next door.
/// </summary>
internal static class BookBedDialogCommandText
{
    /// <summary>
    /// The legacy offline reply.
    /// The legacy client never entered a command body without a session: the console loop answered with <c>mcc.disconnected</c> and stopped there (Program.cs:1059), so that is the wording the session guards use here.
    /// </summary>
    public static string NotConnected(CommandContext ctx)
        => CommandText.NotConnected(ctx);
}
