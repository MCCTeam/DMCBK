namespace DMCBK.Core;

/// <summary>
/// A container window the server has opened for this client.
/// </summary>
/// <remarks>
/// Never window 0: vanilla's own inventory is not opened by the server, it simply exists, and the legacy client filtered it out of exactly this announcement (<c>McClient.OnInventoryOpen</c>, McClient.cs:3861).
/// </remarks>
public sealed class ContainerOpenedEventArgs(int windowId, string title) : EventArgs
{
    /// <summary>The window id the server assigned, which is what every container command addresses.</summary>
    public int WindowId { get; } = windowId;

    /// <summary>
    /// The window's title, already resolved against the vanilla string table (so a plain chest reads "Chest" rather than "container.chest").
    /// Empty when the server sent none.
    /// </summary>
    public string Title { get; } = title;
}

/// <summary>A container window the server has closed.</summary>
public sealed class ContainerClosedEventArgs(int windowId) : EventArgs
{
    /// <summary>The window id that closed.</summary>
    public int WindowId { get; } = windowId;
}
