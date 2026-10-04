using Umpk;
using Umpk.Game.Blocks;
using Umpk.Game.Registries;
using Umpk.Game.World;
using Umpk.Geometry;

namespace DMCBK.Core.Tests;

/// <summary>
/// A minimal in-memory world for the visibility tests: air (0), stone (1) and water (2, fluid so sight passes through it).
/// Mirrors UMPK's own WorldTestData/SnapshotWorldFixture without pulling a version's generated data; the tests pass null shape tables, which reads every non-air block as a full cube.
/// Unset columns read as air, which is what makes "no terrain" fail open.
/// </summary>
internal static class VisibilityWorldFixture
{
    internal const int AirState = 0;
    internal const int StoneState = 1;
    internal const int WaterState = 2;

    internal static World NewWorld() => new(Overworld(), new VisibilityBlockData(), BuildBiomes());

    internal static void SetStone(World world, BlockPos pos) => world.SetBlockStateId(pos, StoneState);

    internal static void SetWater(World world, BlockPos pos) => world.SetBlockStateId(pos, WaterState);

    private static DimensionState Overworld() =>
        new(DimensionType(-64, 384, hasSkylight: true), Identifier.Minecraft("overworld"));

    private static RegistryEntry<DimensionTypeDefinition> DimensionType(int minY, int height, bool hasSkylight)
    {
        var reg = Registry.FromEntries(
            RegistryIds.DimensionType,
            [0],
            [Identifier.Minecraft("overworld")],
            [new DimensionTypeDefinition(minY, height, hasSkylight)]);
        return reg[0];
    }

    private static Registry<BiomeDefinition> BuildBiomes() =>
        new RegistryBuilder<BiomeDefinition>(RegistryIds.Biome, 1)
            .Add(0, Identifier.Minecraft("plains"), new BiomeDefinition())
            .Build();

    /// <summary>Three states: air, solid stone, fluid water. Every other id degrades to stone.</summary>
    private sealed class VisibilityBlockData : IBlockDataSource
    {
        public VisibilityBlockData()
        {
            Blocks = new RegistryBuilder<BlockDefinition>(RegistryIds.Block, 3)
                .Add(0, Identifier.Minecraft("air"), new BlockDefinition(0, 0, 0))
                .Add(1, Identifier.Minecraft("stone"), new BlockDefinition(1, 1, 1))
                .Add(2, Identifier.Minecraft("water"), new BlockDefinition(2, 2, 2))
                .Build();
        }

        public Registry<BlockDefinition> Blocks { get; }

        public int UnknownStateId => 0;

        public bool IsLegacy => false;

        public int StateCount => int.MaxValue;

        public bool IsValidState(int stateId) => stateId >= 0;

        public int GetBlockNetworkId(int stateId) => stateId switch
        {
            0 => 0,
            1 => 1,
            2 => 2,
            _ => 1,
        };

        public int GetDefaultStateId(int blockNetworkId) => blockNetworkId;

        public BlockFlags GetFlags(int stateId) => stateId switch
        {
            0 => BlockFlags.Air,
            2 => BlockFlags.Fluid,
            _ => BlockFlags.Solid | BlockFlags.BlocksMotion,
        };

        public float GetFriction(int stateId) => 0.6f;

        public float GetSpeedFactor(int stateId) => 1.0f;

        public float GetJumpFactor(int stateId) => 1.0f;

        public IReadOnlyList<string> GetPropertyNames(int stateId) => Array.Empty<string>();

        public bool TryGetPropertyValue(int stateId, string propertyName, out string value)
        {
            value = string.Empty;
            return false;
        }

        public bool TryDecodeLegacy(int stateId, out int blockId, out int meta)
        {
            blockId = 0;
            meta = 0;
            return false;
        }

        public int EncodeLegacy(int blockId, int meta) => (blockId << 4) | (meta & 0xF);
    }
}
