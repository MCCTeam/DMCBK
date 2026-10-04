using DMCBK.Core.Commands;
using Umpk.Client;
using Umpk.Protocol.Java;
using Umpk.Text;

namespace DMCBK.Core;

/// <summary>
/// Renders a UMPK <see cref="DisconnectInfo"/> as one short, user-facing fragment.
/// This is the single implementation every host uses, so the CLI, the TUI and an embedding host cannot drift apart or hardcode their own wording; it lives in DMCBK.Core so a host that references only the core (the embeddability law) can still reach it, and its text comes from <see cref="CommandStrings"/> rather than any call site.
/// <para>
/// A kick is always reported AS a kick, with the server's own text when it sent one, so the user can tell "the server threw me out, and here is why" from "the connection broke".
/// Before the play-phase disconnect packet was decoded there was nothing to report and a kick read as a bare socket error.
/// </para>
/// </summary>
public static class DisconnectDescription
{
    /// <summary>
    /// Renders <paramref name="info"/> as one short fragment, or the shared "connection was closed" text when <paramref name="info"/> is null.
    /// Hosts read <c>LastDisconnect</c>, which is null until a session has ended, so the null case is handled here by the same method rather than in three copies; extension methods (unlike instance methods) tolerate a null receiver, which is what makes one overload enough for both the <c>info.Describe()</c> call style and the null-safe <c>DisconnectDescription.Describe(info)</c> static call style.
    /// </summary>
    /// <param name="info">The disconnect to describe, or null when none was recorded.</param>
    /// <param name="includeFaultDetail">
    /// True to render a fault as its full <see cref="Exception.ToString"/> (the diagnostic form) rather than just its message.
    /// </param>
    /// <param name="renderMessage">
    /// How to turn the server's own message into display text.
    /// Null flattens it to plain text, which is the safe default for a host that prints raw strings.
    /// A host that can paint styled text passes its own renderer here, and then the server's colours survive: a kick reason arrives either as a styled component or, from a great many servers, as literal section-sign codes inside the text (<c>"§cInvalid nickname case"</c>), and plain flattening keeps neither.
    /// Both forms are the same renderer's job, so neither the wording nor the kick-versus-fault decision below is duplicated per host.
    /// </param>
    public static string Describe(
        this DisconnectInfo? info, bool includeFaultDetail = false, Func<Component, string>? renderMessage = null)
    {
        if (info is null)
            return CommandStrings.DisconnectClosed;

        // Emptiness is decided on the PLAIN text, never on the rendered form: a renderer may add escape sequences around it, so "did the server actually say anything" has to be asked before rendering.
        string messageText = info.Message?.ToPlainText().Trim() ?? string.Empty;

        if (info.IsKick)
        {
            return messageText.Length > 0
                ? CommandStrings.DisconnectKickedWith(RenderMessage(info.Message!, messageText, renderMessage))
                : CommandStrings.DisconnectKicked;
        }

        // A non-kick close can still carry server text (a transfer, for instance).
        // Prefer it over our wording.
        if (messageText.Length > 0)
            return RenderMessage(info.Message!, messageText, renderMessage);

        string reason = DescribeReason(info.Reason);
        return info.Fault is null
            ? reason
            : CommandStrings.DisconnectWithFault(
                reason, includeFaultDetail ? info.Fault.ToString() : info.Fault.Message);
    }

    // Renders the server's message through the host's renderer when it supplied one, falling back to the plain flattening whenever that produces nothing (a message made of nothing but formatting codes would otherwise turn a reason the user could read into an empty one).
    private static string RenderMessage(Component message, string plain, Func<Component, string>? renderMessage)
    {
        if (renderMessage is null)
            return plain;

        return renderMessage(message).Trim() is { Length: > 0 } rendered ? rendered : plain;
    }

    /// <summary>Maps a transport close reason onto readable text instead of leaking the enum name.</summary>
    private static string DescribeReason(CloseReason reason) => reason switch
    {
        CloseReason.Local => CommandStrings.DisconnectLocal,
        CloseReason.SocketEof => CommandStrings.DisconnectSocketEof,
        CloseReason.DisconnectMessage => CommandStrings.DisconnectKicked,
        CloseReason.ProtocolViolation => CommandStrings.DisconnectProtocolViolation,
        CloseReason.Cancelled => CommandStrings.DisconnectCancelled,
        CloseReason.Transferred => CommandStrings.DisconnectTransferred,
        CloseReason.IdleTimeout => CommandStrings.DisconnectIdleTimeout,
        _ => CommandStrings.DisconnectClosed,
    };
}
