using NodeGroups.Core;
using Xunit;

namespace NodeGroups.Core.Tests;

public sealed class DragTests
{
    [Fact]
    public void FrozenHitOrderUsesLargestLayerThenLastInputWithoutChangingPreviewOrder()
    {
        var workspace = WorkspaceTests.Workspace();
        foreach (var id in new[] { "low", "first", "last", "outside" })
            workspace.CreateGroup(id, id, Array.Empty<string>(), default);
        var bounds = new Rect2(-50, -50, 100, 100);
        var regions = new List<GroupHitRegion>
        {
            new("first", bounds, int.MaxValue),
            new("low", bounds, int.MinValue),
            new("last", bounds, int.MaxValue),
            new("outside", new Rect2(400, -50, 100, 100), int.MaxValue)
        };
        using var drag = workspace.BeginDrag(new[] { "b", "a", "b" }, "a", regions);
        regions.Clear();

        // 同层取输入序列最后一项；不命中的更后项不得抢占目标。
        var first = drag.Preview(default);
        Assert.Equal("last", first.TargetGroupId);
        Assert.Equal(new[] { "b", "a" }, first.Nodes.Select(node => node.Id));
        Assert.Equal("outside", drag.Preview(new Point2(450, 0)).TargetGroupId);
        Assert.Null(drag.Preview(new Point2(900, 0)).TargetGroupId);
        Assert.Equal("last", drag.Preview(default).TargetGroupId);
        Assert.Equal(workspace.Snapshot.GetNode("b").Position, first.Nodes[0].Position);
        Assert.Equal("last", drag.Commit(default).After.FindGroupForNode("a")?.Id);
    }

    [Fact]
    public void SourceAndTargetBoundariesAreFrozenAndPreviewDoesNotMutate()
    {
        var workspace = WorkspaceTests.Workspace();
        workspace.CreateGroup("source", "源", new[] { "a", "b" }, default);
        var regions = new List<GroupHitRegion> { new("source", new Rect2(-50, -50, 200, 100), 0) };
        using var drag = workspace.BeginDrag(new[] { "a" }, "a", regions);
        regions[0] = new("source", new Rect2(-50, -50, 1000, 100), 0);
        var preview = drag.Preview(new Point2(200, 0));
        Assert.Null(preview.TargetGroupId);
        Assert.Equal(new Point2(0, 0), workspace.Snapshot.GetNode("a").Position);
        var change = drag.Commit(new Point2(200, 0));
        Assert.Null(workspace.Snapshot.FindGroupForNode("a"));
        Assert.Equal(new[] { "b" }, workspace.Snapshot.GetGroup("source").MemberNodeIds);
        workspace.Restore(change.Before);
        Assert.Equal("source", workspace.Snapshot.FindGroupForNode("a")?.Id);
        Assert.Equal(default, workspace.Snapshot.GetNode("a").Position);
    }

    [Fact]
    public void MultiSelectionTransfersAtPrimaryHitPointAndPreservesRelativeLayout()
    {
        var workspace = WorkspaceTests.Workspace();
        workspace.CreateGroup("source", "源", new[] { "a", "b" }, default);
        workspace.CreateGroup("target", "目标", new[] { "c" }, new Point2(500, 0));
        using var drag = workspace.BeginDrag(new[] { "a", "b" }, "a", new[]
        {
            new GroupHitRegion("target", new Rect2(450, -40, 100, 80), 1)
        });
        Assert.Equal("target", drag.Preview(new Point2(500, 0)).TargetGroupId);
        drag.Commit(new Point2(500, 0));
        Assert.Empty(workspace.Snapshot.GetGroup("source").MemberNodeIds);
        Assert.Equal(new[] { "c", "a", "b" }, workspace.Snapshot.GetGroup("target").MemberNodeIds);
        Assert.Equal(new Point2(500, 0), workspace.Snapshot.GetNode("a").Position);
        Assert.Equal(new Point2(600, 0), workspace.Snapshot.GetNode("b").Position);
    }

    [Fact]
    public void CollapsedTargetIsExpandedAndCancelIsNonDestructive()
    {
        var workspace = WorkspaceTests.Workspace();
        workspace.CreateGroup("g", "目标", Array.Empty<string>(), new Point2(500, 0));
        workspace.SetCollapsed("g", true);
        var regions = new[] { new GroupHitRegion("g", new Rect2(450, -40, 100, 80), 0) };
        var before = workspace.Snapshot;
        using (var cancelled = workspace.BeginDrag(new[] { "a" }, "a", regions))
        {
            cancelled.Preview(new Point2(500, 0));
            cancelled.Cancel();
            Assert.Same(before, workspace.Snapshot);
            Assert.Throws<InvalidOperationException>(() => cancelled.Commit(default));
        }
        using var drag = workspace.BeginDrag(new[] { "a" }, "a", regions);
        var change = drag.Commit(new Point2(500, 0));
        Assert.False(workspace.Snapshot.GetGroup("g").Collapsed);
        Assert.Equal("g", workspace.Snapshot.FindGroupForNode("a")?.Id);
        workspace.Restore(change.Before);
        Assert.True(workspace.Snapshot.GetGroup("g").Collapsed);
        Assert.Null(workspace.Snapshot.FindGroupForNode("a"));
    }

    [Fact]
    public void OverlappingTargetsSelectHighestZAndPreviewAndCommitAgree()
    {
        var workspace = WorkspaceTests.Workspace();
        workspace.CreateGroup("lower", "下层", Array.Empty<string>(), default);
        workspace.CreateGroup("upper", "上层", Array.Empty<string>(), default);
        using var drag = workspace.BeginDrag(new[] { "a" }, "a", new[]
        {
            new GroupHitRegion("lower", new Rect2(-30, -30, 60, 60), 1),
            new GroupHitRegion("upper", new Rect2(-30, -30, 60, 60), 2)
        });
        Assert.Equal("upper", drag.Preview(default).TargetGroupId);
        drag.Commit(default);
        Assert.Equal("upper", workspace.Snapshot.FindGroupForNode("a")?.Id);
    }

    [Fact]
    public void ConcurrentWorkspaceChangeRejectsCommitWithoutOverwritingNewState()
    {
        var workspace = WorkspaceTests.Workspace();
        workspace.CreateGroup("g", "组", new[] { "a" }, default);
        using var drag = workspace.BeginDrag(new[] { "a" }, "a", Array.Empty<GroupHitRegion>());
        workspace.RenameGroup("g", "更新名称");
        var latest = workspace.Snapshot;
        Assert.Throws<InvalidOperationException>(() => drag.Commit(new Point2(200, 0)));
        Assert.Same(latest, workspace.Snapshot);
    }

    [Fact]
    public void DisposingPreviewCannotChangeMembershipAndCompletedGestureCannotBeReused()
    {
        var workspace = WorkspaceTests.Workspace();
        workspace.CreateGroup("g", "组", new[] { "a" }, default);
        var before = workspace.Snapshot;
        using (var preview = workspace.BeginDrag(new[] { "a" }, "a", Array.Empty<GroupHitRegion>()))
            preview.Preview(new Point2(200, 0));
        Assert.Same(before, workspace.Snapshot);
        using var drag = workspace.BeginDrag(new[] { "a" }, "a", Array.Empty<GroupHitRegion>());
        drag.Commit(new Point2(200, 0));
        Assert.Throws<InvalidOperationException>(() => drag.Preview(default));
        Assert.Throws<InvalidOperationException>(() => drag.Commit(default));
    }

    [Fact]
    public void LastNodeDraggedOutsideLeavesEmptyGroupAndUndoRestoresGeometryAndOwnership()
    {
        var workspace = WorkspaceTests.Workspace();
        workspace.CreateGroup("g", "组", new[] { "a" }, default);
        using var drag = workspace.BeginDrag(new[] { "a" }, "a", new[]
        {
            new GroupHitRegion("g", new Rect2(-50, -50, 100, 100), 0)
        });
        var history = new UndoRedoHistory();
        history.Push(drag.Commit(new Point2(1000, 20)));
        Assert.Empty(workspace.Snapshot.GetGroup("g").MemberNodeIds);
        Assert.Equal(new Point2(1000, 20), workspace.Snapshot.GetNode("a").Position);
        history.Undo(workspace);
        Assert.Equal("g", workspace.Snapshot.FindGroupForNode("a")?.Id);
        Assert.Equal(default, workspace.Snapshot.GetNode("a").Position);
        history.Redo(workspace);
        Assert.Empty(workspace.Snapshot.GetGroup("g").MemberNodeIds);
        Assert.Equal(new Point2(1000, 20), workspace.Snapshot.GetNode("a").Position);
    }
}
