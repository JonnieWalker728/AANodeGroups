using NodeGroups.Core;
using Xunit;

namespace NodeGroups.Core.Tests;

public sealed class ProjectionCacheKeyTests
{
    private static readonly ProjectionEdgePresentation Edge = new("native-line-1/option-1", "a", "b", new(5, 10), new(80, 20), "原分支", "option-1", false, 1);
    private static readonly IReadOnlyDictionary<string, Rect2> Bounds = new Dictionary<string, Rect2>
    {
        ["g1"] = new(0, 0, 300, 90), ["g2"] = new(350, 0, 300, 90)
    };
    private static WorkspaceSnapshot Snapshot(string firstMember = "a", string secondMember = "b") => new(
        new[] { WorkspaceTests.Node("a"), WorkspaceTests.Node("b") },
        new[] { new GroupState("g1", "组一", new[] { firstMember }, true, default), new GroupState("g2", "组二", new[] { secondMember }, true, default) });

    [Fact]
    public void OwnershipChangesInvalidateCacheEvenWhenGroupBoundsAndEdgeCountsAreIdentical()
    {
        var before = ProjectionCacheKey.Create(Snapshot(), Bounds, new[] { Edge });
        var after = ProjectionCacheKey.Create(Snapshot("b", "a"), Bounds, new[] { Edge });
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void EveryEdgePresentationIdentityAndBranchMutationInvalidatesTheKey()
    {
        var snapshot = Snapshot();
        var baseline = ProjectionCacheKey.Create(snapshot, Bounds, new[] { Edge });
        foreach (var mutation in new[]
        {
            Edge with { Id = "replacement-native-line" },
            Edge with { ToNodeId = "different-target-at-same-position" },
            Edge with { FromNodeId = "different-source-at-same-position" },
            Edge with { Text = "修改分支" },
            Edge with { NativeLineText = "原生汇总文字修改" },
            Edge with { OptionId = "option-2" },
            Edge with { IsDefault = true },
            Edge with { ConnectionCount = 2 },
            Edge with { Start = new Point2(6, 10) },
            Edge with { End = new Point2(80, 21) }
        }) Assert.NotEqual(baseline, ProjectionCacheKey.Create(snapshot, Bounds, new[] { mutation }));
    }

    [Fact]
    public void DuplicateEndpointsRetainDistinctOptionsAndLengthEncodingAvoidsAmbiguousConcatenation()
    {
        var snapshot = Snapshot();
        var first = ProjectionCacheKey.Create(snapshot, Bounds, new[] { Edge });
        var second = ProjectionCacheKey.Create(snapshot, Bounds, new[] { Edge, Edge with { Id = "native-line-1/option-2", OptionId = "option-2" } });
        Assert.NotEqual(first, second);
        Assert.NotEqual(
            ProjectionCacheKey.Create(snapshot, Bounds, new[] { Edge with { Id = "a|b", Text = "c" } }),
            ProjectionCacheKey.Create(snapshot, Bounds, new[] { Edge with { Id = "a", Text = "b|c" } }));
    }

    [Fact]
    public void StableEquivalentInputsProduceIdenticalKeyAndBoundsOrCollapseChangesInvalidateIt()
    {
        var snapshot = Snapshot();
        var baseline = ProjectionCacheKey.Create(snapshot, Bounds, new[] { Edge });
        Assert.Equal(baseline, ProjectionCacheKey.Create(Snapshot(), Bounds, new[] { Edge with { } }));
        var moved = new Dictionary<string, Rect2>(Bounds) { ["g1"] = new(1, 0, 300, 90) };
        Assert.NotEqual(baseline, ProjectionCacheKey.Create(snapshot, moved, new[] { Edge }));
        var unfolded = new WorkspaceSnapshot(snapshot.Nodes, new[]
        {
            new GroupState("g1", "组一", new[] { "a" }, false, default), snapshot.GetGroup("g2")
        });
        Assert.NotEqual(baseline, ProjectionCacheKey.Create(unfolded, Bounds, new[] { Edge }));
    }
}
