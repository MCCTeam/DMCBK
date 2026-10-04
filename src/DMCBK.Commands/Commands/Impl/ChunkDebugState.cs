using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using DMCBK.Core.Commands.ArgumentTypes;
using DMCBK.Core.Localization;
using Umpk.Client.Actions;
using Umpk.Client.Snapshots;
using Umpk.Commands;
using Umpk.Game.Players;
using Umpk.Geometry;

namespace DMCBK.Core.Commands.Impl;

/// <summary>
/// The MCC-side debug chunk overlay backing the <c>chunk _setloading/_setloaded/_delete</c> ops.
/// Testable in isolation so the Chunk.cs:266 fix (setloaded marks loaded=true) is pinned by a unit test.
/// </summary>
public sealed class ChunkDebugState
{
    private readonly Dictionary<(int X, int Z), bool> _overlay = [];

    /// <summary>Marks a chunk as loading (loaded flag false).</summary>
    public void SetLoading(int x, int z) => _overlay[(x, z)] = false;

    /// <summary>Marks a chunk as loaded (loaded flag true). This is the fixed behavior.</summary>
    public void SetLoaded(int x, int z) => _overlay[(x, z)] = true;

    /// <summary>Removes a chunk from the overlay.</summary>
    public void Delete(int x, int z) => _overlay.Remove((x, z));

    /// <summary>Reads the overlay flag for a chunk; false result means no overlay entry.</summary>
    public bool TryGet(int x, int z, out bool loaded) => _overlay.TryGetValue((x, z), out loaded);
}
