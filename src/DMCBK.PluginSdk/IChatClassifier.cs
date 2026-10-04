using DMCBK.Core;
using Umpk.Client.Events;
using Umpk.Text;

namespace DMCBK.PluginSdk;

/// <summary>The structured category a chat line was classified into.</summary>
public enum ChatKind
{
    /// <summary>Could not be classified into a more specific kind.</summary>
    Unknown,

    /// <summary>A public/normal player chat message.</summary>
    Chat,

    /// <summary>A private/whisper message directed at the client.</summary>
    PrivateMessage,

    /// <summary>A server/system message (not player-authored).</summary>
    System,

    /// <summary>A teleport request (<c>/tpa</c>-style) addressed to the client.</summary>
    TeleportRequest,
}

/// <summary>A structured classification of one chat line: kind, sender, and the plain body.</summary>
/// <param name="Kind">The classified kind.</param>
/// <param name="Sender">The sender name when one could be extracted, else null.</param>
/// <param name="Body">The message body (sender/prefix stripped when possible), plain text.</param>
/// <param name="Raw">The full original line as plain text.</param>
public sealed record ClassifiedChat(ChatKind Kind, string? Sender, string Body, string Raw);

/// <summary>
/// The injectable, testable chat classification service.
/// It returns a structured (kind, sender, body) result over UMPK's already-normalized <see cref="ChatMessageReceived"/> instead of separate <c>IsChatMessage</c> / <c>IsPrivateMessage</c> / <c>IsTeleportRequest</c> checks.
/// It errs toward <see cref="ChatKind.Chat"/> / <see cref="ChatKind.System"/> and never throws on an unrecognized format (it degrades to <see cref="ChatKind.Unknown"/> with the raw body).
/// </summary>
public interface IChatClassifier
{
    /// <summary>Classifies an inbound UMPK chat event.</summary>
    ClassifiedChat Classify(ChatMessageReceived message);

    /// <summary>Classifies a raw plain-text line (for tests and log-driven inputs).</summary>
    ClassifiedChat Classify(string plainText);
}

/// <summary>The default heuristic <see cref="IChatClassifier"/>.</summary>
internal sealed class ChatClassifier : IChatClassifier
{
    private readonly ITranslationSource _translations;

    internal ChatClassifier(ITranslationSource translations)
    {
        ArgumentNullException.ThrowIfNull(translations);
        _translations = translations;
    }

    /// <inheritdoc/>
    public ClassifiedChat Classify(ChatMessageReceived message)
    {
        ArgumentNullException.ThrowIfNull(message);
        string raw = message.Message.ToPlainText(_translations);
        string? sender = message.SenderName?.ToPlainText(_translations);

        ChatKind baseKind = message.Category switch
        {
            ChatCategory.System => ChatKind.System,
            ChatCategory.Player or ChatCategory.Disguised => ChatKind.Chat,
            _ => ChatKind.Unknown,
        };

        // Refine using the text shape (whispers, teleport requests) even when the era gives us a sender.
        ClassifiedChat refined = ClassifyText(raw, sender);
        if (refined.Kind is ChatKind.PrivateMessage or ChatKind.TeleportRequest)
            return refined;

        if (baseKind == ChatKind.Chat && sender is not null)
        {
            string body = StripSenderPrefix(raw, sender);
            return new ClassifiedChat(ChatKind.Chat, sender, body, raw);
        }

        return baseKind == ChatKind.Unknown ? refined : new ClassifiedChat(baseKind, sender, raw, raw);
    }

    /// <inheritdoc/>
    public ClassifiedChat Classify(string plainText)
    {
        ArgumentNullException.ThrowIfNull(plainText);
        return ClassifyText(plainText, sender: null);
    }

    private static ClassifiedChat ClassifyText(string raw, string? sender)
    {
        string trimmed = raw.Trim();

        // Teleport request: "X has requested to teleport to you" / "X wants to teleport ...".
        int tpaIndex = trimmed.IndexOf("teleport", StringComparison.OrdinalIgnoreCase);
        if (tpaIndex > 0
            && (trimmed.Contains("request", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("wants to", StringComparison.OrdinalIgnoreCase)))
        {
            string tpaSender = sender ?? FirstWord(trimmed);
            return new ClassifiedChat(ChatKind.TeleportRequest, tpaSender, trimmed, raw);
        }

        // Whisper: "X whispers to you: body" / "X whispers: body" / "[X -> me] body".
        int whisperIndex = trimmed.IndexOf("whisper", StringComparison.OrdinalIgnoreCase);
        if (whisperIndex > 0)
        {
            int colon = trimmed.IndexOf(':', whisperIndex);
            string body = colon >= 0 ? trimmed[(colon + 1)..].Trim() : trimmed;
            string pmSender = sender ?? trimmed[..whisperIndex].Trim();
            return new ClassifiedChat(ChatKind.PrivateMessage, pmSender, body, raw);
        }

        // Vanilla whisper form: "X whispers to you" is caught above; "-> me" arrow form:
        if (trimmed.Contains("-> me", StringComparison.OrdinalIgnoreCase))
            return new ClassifiedChat(ChatKind.PrivateMessage, sender, trimmed, raw);

        // Standard "<Name> body" chat form.
        if (sender is null && trimmed.StartsWith('<'))
        {
            int close = trimmed.IndexOf('>');
            if (close > 1)
            {
                string name = trimmed[1..close];
                string body = trimmed[(close + 1)..].Trim();
                return new ClassifiedChat(ChatKind.Chat, name, body, raw);
            }
        }

        return new ClassifiedChat(sender is null ? ChatKind.Unknown : ChatKind.Chat, sender, trimmed, raw);
    }

    private static string StripSenderPrefix(string raw, string sender)
    {
        string trimmed = raw.Trim();

        // "<sender> body"
        string angle = $"<{sender}>";
        int angleIndex = trimmed.IndexOf(angle, StringComparison.Ordinal);
        if (angleIndex >= 0)
            return trimmed[(angleIndex + angle.Length)..].Trim();

        // "sender: body"
        string colon = $"{sender}:";
        int colonIndex = trimmed.IndexOf(colon, StringComparison.Ordinal);
        if (colonIndex >= 0)
            return trimmed[(colonIndex + colon.Length)..].Trim();

        return trimmed;
    }

    private static string FirstWord(string text)
    {
        int space = text.IndexOf(' ');
        return space > 0 ? text[..space] : text;
    }
}
