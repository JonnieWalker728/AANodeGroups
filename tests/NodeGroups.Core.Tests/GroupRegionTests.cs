using NodeGroups.Core;
using Xunit;

namespace NodeGroups.Core.Tests;

/// <summary>Exercises user-sized group regions and selection-driven stacking without native graph changes.</summary>
public sealed class GroupRegionTests
{
    private static readonly Rect2 InitialBounds = new(-70, -40, 220, 120);

    [Fact]
    public void CreationStoresExplicitRegionAndLegacyGroupsKeepAutomaticRegionFallback()
    {
        var workspace = WorkspaceTests.Workspace();
        var nodes = workspace.Snapshot.Nodes;
        workspace.CreateGroup("g", "组", new[] { "a" }, default, expandedBounds: InitialBounds);
        workspace.CreateGroup("legacy", "旧组", Array.Empty<string>(), default);
        Assert.Equal(InitialBounds, workspace.Snapshot.GetGroup("g").ExpandedBounds);
        Assert.Null(workspace.Snapshot.GetGroup("legacy").ExpandedBounds);
        Assert.Equal(nodes, workspace.Snapshot.Nodes);
    }

    [Fact]
    public void ResizingOnlyChangesRegionEvenWhenItCoversAnUngroupedNode()
    {
        var workspace = WorkspaceTests.Workspace();
        workspace.CreateGroup("g", "组", new[] { "a" }, new Point2(10, 20), expandedBounds: InitialBounds);
        var before = workspace.Snapshot;
        var resized = new Rect2(-100, -100, 1000, 1000);
        var change = workspace.SetExpandedBounds("g", resized);
        Assert.True(change.HasChanges);
        Assert.Equal(resized, workspace.Snapshot.GetGroup("g").ExpandedBounds);
        Assert.Equal(before.Nodes, workspace.Snapshot.Nodes);
        Assert.Equal(before.GetGroup("g").MemberNodeIds, workspace.Snapshot.GetGroup("g").MemberNodeIds);
        Assert.Equal(new Point2(10, 20), workspace.Snapshot.GetGroup("g").Anchor);
        Assert.Null(workspace.Snapshot.FindGroupForNode("b"));
        Assert.Null(workspace.Snapshot.FindGroupForNode("c"));
    }

    [Fact]
    public void MetadataAndMembershipCommandsRetainUserSizedRegion()
    {
        var workspace = WorkspaceTests.Workspace();
        workspace.CreateGroup("g", "组", new[] { "a" }, default, expandedBounds: InitialBounds);
        workspace.RenameGroup("g", "新名");
        workspace.SetCollapsed("g", true);
        workspace.TransferNodes(new[] { "b" }, "g");
        workspace.TransferNodes(new[] { "a" }, null);
        workspace.RegisterNode(WorkspaceTests.Node("new"), "g");
        workspace.RemoveNode("b");
        workspace.ReconcileNodes(new[] { WorkspaceTests.Node("a", 999), WorkspaceTests.Node("new"), WorkspaceTests.Node("c") });
        Assert.Equal(InitialBounds, workspace.Snapshot.GetGroup("g").ExpandedBounds);
    }

    [Fact]
    public void GroupMoveTranslatesRegionAndAnchorWithItsMembers()
    {
        var workspace = WorkspaceTests.Workspace();
        workspace.CreateGroup("g", "组", new[] { "a", "b" }, new Point2(20, 30), expandedBounds: InitialBounds);
        var delta = new Point2(7, -19);
        workspace.MoveGroup("g", delta);
        Assert.Equal(InitialBounds.Translate(delta), workspace.Snapshot.GetGroup("g").ExpandedBounds);
        Assert.Equal(new Point2(27, 11), workspace.Snapshot.GetGroup("g").Anchor);
        Assert.Equal(new Point2(7, -19), workspace.Snapshot.GetNode("a").Position);
        Assert.Equal(new Point2(107, -19), workspace.Snapshot.GetNode("b").Position);
        Assert.Equal(new Point2(500, 0), workspace.Snapshot.GetNode("c").Position);
    }

    [Fact]
    public void RegionResizeIsUndoableAndRepeatedSameBoundsAreANoOp()
    {
        var workspace = WorkspaceTests.Workspace();
        workspace.CreateGroup("g", "组", new[] { "a" }, default, expandedBounds: InitialBounds);
        var history = new UndoRedoHistory();
        var resized = new Rect2(-100, -100, 400, 300);
        history.Push(workspace.SetExpandedBounds("g", resized));
        var resizedSnapshot = workspace.Snapshot;
        Assert.False(workspace.SetExpandedBounds("g", resized).HasChanges);
        Assert.Same(resizedSnapshot, workspace.Snapshot);
        Assert.True(history.Undo(workspace));
        Assert.Equal(InitialBounds, workspace.Snapshot.GetGroup("g").ExpandedBounds);
        Assert.True(history.Redo(workspace));
        Assert.Equal(resized, workspace.Snapshot.GetGroup("g").ExpandedBounds);
    }

    [Theory]
    [InlineData(0, 0, 0, 10)]
    [InlineData(0, 0, 10, 0)]
    [InlineData(0, 0, -1, 10)]
    [InlineData(0, 0, 10, -1)]
    [InlineData(double.NaN, 0, 10, 10)]
    [InlineData(0, double.PositiveInfinity, 10, 10)]
    [InlineData(0, 0, double.PositiveInfinity, 10)]
    [InlineData(double.MaxValue, 0, double.MaxValue, 10)]
    public void InvalidRegionsAreRejectedAtEveryBoundaryWithoutPartialChanges(double x, double y, double width, double height)
    {
        var invalid = new Rect2(x, y, width, height);
        var workspace = WorkspaceTests.Workspace();
        workspace.CreateGroup("g", "组", new[] { "a" }, default);
        var before = workspace.Snapshot;
        Assert.Throws<ArgumentException>(() => workspace.SetExpandedBounds("g", invalid));
        Assert.Throws<ArgumentException>(() => workspace.CreateGroup("bad", "坏组", new[] { "a" }, default, expandedBounds: invalid));
        Assert.Throws<ArgumentException>(() => new WorkspaceSnapshot(before.Nodes, new[]
        {
            new GroupState("g", "组", new[] { "a" }, false, default, expandedBounds: invalid)
        }));
        Assert.Same(before, workspace.Snapshot);
    }

    [Fact]
    public void OverflowingRegionTranslationIsAtomic()
    {
        var workspace = WorkspaceTests.Workspace();
        workspace.CreateGroup("g", "组", Array.Empty<string>(), default,
            expandedBounds: new Rect2(double.MaxValue / 2, 0, 10, 10));
        var before = workspace.Snapshot;
        Assert.Throws<ArgumentException>(() => workspace.MoveGroup("g", new Point2(double.MaxValue, 0)));
        Assert.Same(before, workspace.Snapshot);
    }

    [Fact]
    public void PromotingOneGroupChangesOnlyItsLayerAndLeavesOtherRelativeOrder()
    {
        var workspace = WorkspaceTests.Workspace();
        workspace.CreateGroup("one", "一", new[] { "a" }, default, expandedBounds: InitialBounds);
        workspace.CreateGroup("two", "二", new[] { "b" }, default);
        workspace.CreateGroup("three", "三", Array.Empty<string>(), default);
        var before = workspace.Snapshot;
        var change = workspace.PromoteGroup("one");
        Assert.True(change.HasChanges);
        Assert.Equal(before.Nodes, workspace.Snapshot.Nodes);
        Assert.Equal(new[] { "two", "three", "one" }, LayerOrder(workspace.Snapshot));
        Assert.Equal(before.GetGroup("two").ZIndex, workspace.Snapshot.GetGroup("two").ZIndex);
        Assert.Equal(before.GetGroup("three").ZIndex, workspace.Snapshot.GetGroup("three").ZIndex);
        Assert.Equal(InitialBounds, workspace.Snapshot.GetGroup("one").ExpandedBounds);
        Assert.Equal(before.GetGroup("one").MemberNodeIds, workspace.Snapshot.GetGroup("one").MemberNodeIds);
        workspace.Restore(change.Before);
        Assert.Equal(new[] { "one", "two", "three" }, LayerOrder(workspace.Snapshot));
        workspace.Restore(change.After);
        Assert.Equal(new[] { "two", "three", "one" }, LayerOrder(workspace.Snapshot));
    }

    [Fact]
    public void TopmostSelectionIsANoOpAndTiesUseTheSameLastListedWinnerAsDrag()
    {
        var workspace = new GroupWorkspace(new[] { WorkspaceTests.Node("a") }, new[]
        {
            new GroupState("first", "先", Array.Empty<string>(), false, default, zIndex: 5),
            new GroupState("last", "后", Array.Empty<string>(), false, default, zIndex: 5)
        });
        var before = workspace.Snapshot;
        Assert.False(workspace.PromoteGroup("last").HasChanges);
        Assert.Same(before, workspace.Snapshot);
        using (var drag = workspace.BeginDrag(new[] { "a" }, "a", HitRegions(workspace.Snapshot)))
            Assert.Equal("last", drag.Preview(default).TargetGroupId);
        workspace.PromoteGroup("first");
        Assert.Equal(new[] { "last", "first" }, LayerOrder(workspace.Snapshot));
        using var promotedDrag = workspace.BeginDrag(new[] { "a" }, "a", HitRegions(workspace.Snapshot));
        Assert.Equal("first", promotedDrag.Preview(default).TargetGroupId);
    }

    [Fact]
    public void PromotionAtIntegerLimitNormalizesLayersWithoutReorderingOtherGroups()
    {
        var workspace = new GroupWorkspace(new[] { WorkspaceTests.Node("a") }, new[]
        {
            new GroupState("selected", "选中", Array.Empty<string>(), false, default, zIndex: 4),
            new GroupState("oldTopFirst", "上层先", Array.Empty<string>(), false, default, zIndex: int.MaxValue),
            new GroupState("bottom", "下层", Array.Empty<string>(), false, default, zIndex: -8),
            new GroupState("oldTopLast", "上层后", Array.Empty<string>(), false, default, zIndex: int.MaxValue)
        });
        workspace.PromoteGroup("selected");
        Assert.Equal(new[] { "bottom", "oldTopFirst", "oldTopLast", "selected" }, LayerOrder(workspace.Snapshot));
        Assert.Equal(new[] { 0, 1, 2, 3 }, workspace.Snapshot.Groups.Select(g => g.ZIndex).OrderBy(z => z));
        Assert.False(workspace.PromoteGroup("selected").HasChanges);
        using var drag = workspace.BeginDrag(new[] { "a" }, "a", HitRegions(workspace.Snapshot));
        Assert.Equal("selected", drag.Preview(default).TargetGroupId);
    }

    [Fact]
    public void CreatingGroupAtIntegerLimitStillCreatesTheNewTopLayer()
    {
        var workspace = new GroupWorkspace(new[] { WorkspaceTests.Node("a") }, new[]
        {
            new GroupState("first", "一", Array.Empty<string>(), false, default, zIndex: int.MaxValue),
            new GroupState("second", "二", Array.Empty<string>(), false, default, zIndex: int.MaxValue)
        });
        workspace.CreateGroup("new", "新", Array.Empty<string>(), default);
        Assert.Equal(new[] { "first", "second", "new" }, LayerOrder(workspace.Snapshot));
        Assert.True(workspace.Snapshot.GetGroup("new").ZIndex > workspace.Snapshot.GetGroup("second").ZIndex);
    }

    private static string[] LayerOrder(WorkspaceSnapshot snapshot) => snapshot.Groups
        .Select((group, index) => (group, index)).OrderBy(item => item.group.ZIndex).ThenBy(item => item.index)
        .Select(item => item.group.Id).ToArray();

    private static GroupHitRegion[] HitRegions(WorkspaceSnapshot snapshot) => snapshot.Groups
        .Select(group => new GroupHitRegion(group.Id, InitialBounds, group.ZIndex)).ToArray();
}
