using Umpk.Client.Snapshots;
using Umpk.Geometry;

namespace DMCBK.Core.Commands;

/// <summary>A chunk snapshot and optional markers for host-owned presentation.</summary>
public sealed record ChunkMapPresentation(
    ChunkStatusGrid Grid,
    IReadOnlySet<ChunkPos> LoadingChunks,
    string PlayerPositionText,
    Vec3d? MarkedLocation,
    ChunkPos? MarkedChunk,
    int Rows,
    int Columns);
