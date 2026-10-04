namespace DMCBK.Core.Configuration;

/// <summary>
/// What the client answers when a server pushes a resource pack, and therefore what the server sees on the wire.
/// <see cref="Accept"/> (the default) restores the retired 1.x behavior: the client reports the pack as loaded so a server with <c>require-resource-pack</c> keeps the session, and pack translations are layered into chat when <see cref="LocalizationConfig.LoadResourcePackTranslations"/> is on.
/// <see cref="Decline"/> answers Declined, which a strict server kicks for.
/// <see cref="Prompt"/> asks the host once per push and answers with whatever the user chose.
/// </summary>
public enum ResourcePackPolicyMode
{
    /// <summary>Report the pack as loaded (the default).</summary>
    Accept,

    /// <summary>Answer Declined to every pack.</summary>
    Decline,

    /// <summary>Ask the host once per push; a host with no prompt answers Declined.</summary>
    Prompt,
}
