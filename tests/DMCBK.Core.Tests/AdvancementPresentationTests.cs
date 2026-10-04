using DMCBK.Core;
using Xunit;

namespace DMCBK.Core.Tests;

public sealed class AdvancementPresentationTests
{
    [Theory]
    [InlineData(769, false)]
    [InlineData(770, true)]
    [InlineData(773, true)]
    [InlineData(774, false)]
    [InlineData(776, true)]
    [InlineData(777, true)]
    [InlineData(778, false)]
    public void StructuredDataAvailability_IsTruthfulForDecodedProtocols(int protocol, bool expected)
        => Assert.Equal(expected, AdvancementPresentation.StructuredDataAvailable(protocol));

    [Fact]
    public void HierarchyAndCompletionHelpers_HandleRootsChildrenAndCycles()
    {
        var root = new AdvancementInfo("minecraft:adventure/root", null, "Adventure", null, 0, 1, 1);
        var child = new AdvancementInfo(
            "minecraft:adventure/kill_a_mob", root.Id, "Monster Hunter", null, 0, 2, 1);
        var grandchild = new AdvancementInfo(
            "minecraft:adventure/kill_all_mobs", child.Id, "Monsters Hunted", null, 2, 3, 3);
        var byId = new Dictionary<string, AdvancementInfo>(StringComparer.Ordinal)
        {
            [root.Id] = root,
            [child.Id] = child,
            [grandchild.Id] = grandchild,
        };

        Assert.Equal(0, AdvancementPresentation.Depth(root, byId));
        Assert.Equal(1, AdvancementPresentation.Depth(child, byId));
        Assert.Equal(2, AdvancementPresentation.Depth(grandchild, byId));
        Assert.True(string.Compare(
            AdvancementPresentation.HierarchyKey(root, byId),
            AdvancementPresentation.HierarchyKey(child, byId),
            StringComparison.Ordinal) < 0);
        Assert.True(string.Compare(
            AdvancementPresentation.HierarchyKey(child, byId),
            AdvancementPresentation.HierarchyKey(grandchild, byId),
            StringComparison.Ordinal) < 0);
        Assert.True(AdvancementPresentation.IsComplete(root));
        Assert.False(AdvancementPresentation.IsComplete(child));
        Assert.True(AdvancementPresentation.IsComplete(grandchild));
        Assert.Equal("adventure", AdvancementPresentation.Scope(grandchild.Id));
        Assert.Equal("adventure/kill_all_mobs", AdvancementPresentation.ShortId(grandchild.Id));

        var cycle = new AdvancementInfo("custom:a", "custom:b", null, null, null, 0, 0);
        var cycleParent = new AdvancementInfo("custom:b", "custom:a", null, null, null, 0, 0);
        var cyclic = new Dictionary<string, AdvancementInfo>(StringComparer.Ordinal)
        {
            [cycle.Id] = cycle,
            [cycleParent.Id] = cycleParent,
        };
        Assert.InRange(AdvancementPresentation.Depth(cycle, cyclic), 1, 2);
    }
}
