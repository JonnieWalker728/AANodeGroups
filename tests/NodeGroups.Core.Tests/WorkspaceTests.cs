using NodeGroups.Core;
using Xunit;

namespace NodeGroups.Core.Tests;

public sealed class WorkspaceTests
{
    [Fact]
    public void TransferPreservesDestinationOrderAndAppendsNewIdentitiesInSelectionOrder()
    {
        var nodes = new[] { Node("a"), Node("A"), Node("b"), Node("c"), Node("d") };
        var workspace = new GroupWorkspace(nodes, new[]
        {
            new GroupState("target", "目标", new[] { "missing", "b", "a" }, false, default),
            new GroupState("source", "来源", new[] { "c", "d", "A" }, false, default)
        });
        var before = workspace.Snapshot;

        // 已有成员与暂时缺失的成员保持顺序；新成员仅按首次选中顺序追加，大小写有别。
        var change = workspace.TransferNodes(new[] { "d", "a", "c", "d", "A", "b" }, "target");
        Assert.Equal(new[] { "missing", "b", "a", "d", "c", "A" }, workspace.Snapshot.GetGroup("target").MemberNodeIds);
        Assert.Empty(workspace.Snapshot.GetGroup("source").MemberNodeIds);
        Assert.Equal(before.Nodes, workspace.Snapshot.Nodes);
        Assert.False(workspace.TransferNodes(new[] { "c", "d", "A", "a" }, "target").HasChanges);
        workspace.Restore(change.Before);
        Assert.Equal(before.GetGroup("target").MemberNodeIds, workspace.Snapshot.GetGroup("target").MemberNodeIds);
        Assert.Equal(before.GetGroup("source").MemberNodeIds, workspace.Snapshot.GetGroup("source").MemberNodeIds);
    }

    internal static NodeState Node(string id, double x = 0, double y = 0) =>
        new(id, new Point2(x, y), new Point2(80, 40));

    internal static GroupWorkspace Workspace() => new(new[] { Node("a"), Node("b", 100), Node("c", 500) });

    [Fact]
    public void GroupCreationDoesNotCreateOrReplaceStoryNodes()
    {
        var workspace = Workspace();
        var before = workspace.Snapshot;
        workspace.CreateGroup("g", "序章", new[] { "a", "b" }, new Point2(50, 0));
        Assert.Equal(before.Nodes, workspace.Snapshot.Nodes);
        Assert.Equal(new[] { "a", "b" }, workspace.Snapshot.GetGroup("g").MemberNodeIds);
        Assert.Null(workspace.Snapshot.FindGroupForNode("c"));
    }

    [Fact]
    public void EmptyGroupsCanBeNamedCollapsedAndKeptAfterLastMemberLeaves()
    {
        var workspace = Workspace();
        workspace.CreateGroup("g", "空组", Array.Empty<string>(), new Point2(30, 20));
        workspace.RenameGroup("g", " 新名称 ");
        workspace.SetCollapsed("g", true);
        workspace.TransferNodes(new[] { "a" }, "g");
        workspace.TransferNodes(new[] { "a" }, null);
        var group = workspace.Snapshot.GetGroup("g");
        Assert.Equal("新名称", group.Name);
        Assert.True(group.Collapsed);
        Assert.Empty(group.MemberNodeIds);
    }

    [Fact]
    public void ReassigningMembersCannotLeaveDuplicateOwnership()
    {
        var workspace = Workspace();
        workspace.CreateGroup("one", "第一组", new[] { "a", "b" }, default);
        workspace.CreateGroup("two", "第二组", new[] { "b", "c" }, default);
        Assert.Equal(new[] { "a" }, workspace.Snapshot.GetGroup("one").MemberNodeIds);
        Assert.Equal(new[] { "b", "c" }, workspace.Snapshot.GetGroup("two").MemberNodeIds);
    }

    [Fact]
    public void DissolvingGroupKeepsEveryNodeAndItsPosition()
    {
        var workspace = Workspace();
        workspace.CreateGroup("g", "组", new[] { "a", "b" }, default);
        var nodes = workspace.Snapshot.Nodes;
        workspace.DissolveGroup("g");
        Assert.Empty(workspace.Snapshot.Groups);
        Assert.Equal(nodes, workspace.Snapshot.Nodes);
    }

    [Fact]
    public void GroupMoveUsesOneDeltaAndLeavesOutsideNodesAlone()
    {
        var workspace = Workspace();
        workspace.CreateGroup("g", "组", new[] { "a", "b" }, new Point2(50, 0));
        workspace.MoveGroup("g", new Point2(21, -8));
        Assert.Equal(new Point2(21, -8), workspace.Snapshot.GetNode("a").Position);
        Assert.Equal(new Point2(121, -8), workspace.Snapshot.GetNode("b").Position);
        Assert.Equal(new Point2(500, 0), workspace.Snapshot.GetNode("c").Position);
        Assert.Equal(new Point2(71, -8), workspace.Snapshot.GetGroup("g").Anchor);
    }

    [Fact]
    public void RegisteringNodeIntoCollapsedGroupUnfoldsItAndIsOneUndoableChange()
    {
        var workspace = Workspace();
        workspace.CreateGroup("g", "组", Array.Empty<string>(), default);
        workspace.SetCollapsed("g", true);
        var change = workspace.RegisterNode(Node("new", 40), "g");
        Assert.Equal("g", workspace.Snapshot.FindGroupForNode("new")?.Id);
        Assert.False(workspace.Snapshot.GetGroup("g").Collapsed);
        workspace.Restore(change.Before);
        Assert.DoesNotContain(workspace.Snapshot.Nodes, n => n.Id == "new");
        Assert.True(workspace.Snapshot.GetGroup("g").Collapsed);
        workspace.Restore(change.After);
        Assert.Equal("g", workspace.Snapshot.FindGroupForNode("new")?.Id);
    }

    [Fact]
    public void TemporarilyMissingNodeRetainsMembershipForNativeUndo()
    {
        var workspace = Workspace();
        workspace.CreateGroup("g", "组", new[] { "a", "b" }, default);
        workspace.RemoveNode("a");
        Assert.Equal("g", workspace.Snapshot.FindGroupForNode("a")?.Id);
        workspace.ReconcileNodes(new[] { Node("a", 80), Node("b", 100), Node("c", 500) });
        Assert.Equal("g", workspace.Snapshot.FindGroupForNode("a")?.Id);
        Assert.Equal(new Point2(80, 0), workspace.Snapshot.GetNode("a").Position);
    }

    [Fact]
    public void SnapshotsDefensivelyCopyMembershipAndNodeLists()
    {
        var members = new List<string> { "a" };
        var nodes = new List<NodeState> { Node("a") };
        var groups = new List<GroupState> { new("g", "组", members, false, default) };
        var snapshot = new WorkspaceSnapshot(nodes, groups);
        nodes.Clear();
        members.Clear();
        groups.Clear();
        Assert.Single(snapshot.Nodes);
        Assert.Single(snapshot.Groups);
        Assert.Equal(new[] { "a" }, snapshot.GetGroup("g").MemberNodeIds);
    }

    [Fact]
    public void InvalidTransferIsAtomicAndInvalidSnapshotOwnershipIsRejected()
    {
        var workspace = Workspace();
        workspace.CreateGroup("g", "组", new[] { "a" }, default);
        var before = workspace.Snapshot;
        Assert.Throws<ArgumentException>(() => workspace.TransferNodes(new[] { "a", "missing" }, "g"));
        Assert.Same(before, workspace.Snapshot);
        Assert.Throws<ArgumentException>(() => new WorkspaceSnapshot(before.Nodes, new[]
        {
            new GroupState("g1", "1", new[] { "a" }, false, default),
            new GroupState("g2", "2", new[] { "a" }, false, default)
        }));
    }

    [Fact]
    public void UndoRedoRestoresCombinedChangeAndNewCommandDiscardsFuture()
    {
        var workspace = Workspace();
        var history = new UndoRedoHistory();
        history.Push(workspace.CreateGroup("g", "旧名", new[] { "a", "b" }, default));
        history.Push(workspace.RenameGroup("g", "新名"));
        Assert.True(history.Undo(workspace));
        Assert.Equal("旧名", workspace.Snapshot.GetGroup("g").Name);
        Assert.True(history.Redo(workspace));
        Assert.Equal("新名", workspace.Snapshot.GetGroup("g").Name);
        Assert.True(history.Undo(workspace));
        history.Push(workspace.SetCollapsed("g", true));
        Assert.False(history.Redo(workspace));
        Assert.True(history.Undo(workspace));
        Assert.False(workspace.Snapshot.GetGroup("g").Collapsed);
        Assert.True(history.Undo(workspace));
        Assert.Empty(workspace.Snapshot.Groups);
        Assert.False(history.Undo(workspace));
    }

    [Fact]
    public void NameAndGeometryValidationLeavesWorkspaceUnchanged()
    {
        var workspace = Workspace();
        workspace.CreateGroup("g", "组", Array.Empty<string>(), default);
        var before = workspace.Snapshot;
        Assert.Throws<ArgumentException>(() => workspace.RenameGroup("g", "  "));
        Assert.Throws<ArgumentException>(() => workspace.MoveGroup("g", new Point2(double.NaN, 0)));
        Assert.Same(before, workspace.Snapshot);
    }

    [Fact]
    public void NoOpsDoNotDirtyStateOrDiscardRedoBranch()
    {
        var workspace = Workspace();
        var history = new UndoRedoHistory();
        history.Push(workspace.CreateGroup("g", "组", new[] { "a", "b" }, default));
        history.Push(workspace.SetCollapsed("g", true));
        history.Undo(workspace);
        var before = workspace.Snapshot;
        history.Push(workspace.SetCollapsed("g", false));
        Assert.Same(before, workspace.Snapshot);
        Assert.False(workspace.RenameGroup("g", "组").HasChanges);
        Assert.False(workspace.MoveGroup("g", default).HasChanges);
        Assert.False(workspace.TransferNodes(new[] { "a", "b" }, "g").HasChanges);
        Assert.True(history.Redo(workspace));
        Assert.True(workspace.Snapshot.GetGroup("g").Collapsed);
    }

    [Fact]
    public void NativeSynchronizationDoesNotInferMembershipFromNewPositions()
    {
        var workspace = Workspace();
        workspace.CreateGroup("g", "组", new[] { "a" }, default);
        workspace.ReconcileNodes(new[] { Node("a", 9999), Node("b"), Node("c") });
        Assert.Equal("g", workspace.Snapshot.FindGroupForNode("a")?.Id);
        Assert.Null(workspace.Snapshot.FindGroupForNode("b"));
        Assert.Equal(new Point2(9999, 0), workspace.Snapshot.GetNode("a").Position);
    }

    [Fact]
    public void InvalidRegistrationDoesNotExpandOrAddMemberToTarget()
    {
        var workspace = Workspace();
        workspace.CreateGroup("g", "组", Array.Empty<string>(), default);
        workspace.SetCollapsed("g", true);
        var before = workspace.Snapshot;
        Assert.Throws<ArgumentException>(() => workspace.RegisterNode(Node("bad", double.NaN), "g"));
        Assert.Same(before, workspace.Snapshot);
        Assert.Empty(workspace.Snapshot.GetGroup("g").MemberNodeIds);
        Assert.True(workspace.Snapshot.GetGroup("g").Collapsed);
    }

    [Fact]
    public void RestoreKeepsRevisionMonotonicEvenWhenRestoringOldContents()
    {
        var workspace = Workspace();
        var initial = workspace.Snapshot;
        workspace.CreateGroup("g", "组", Array.Empty<string>(), default);
        var revision = workspace.Snapshot.Revision;
        workspace.Restore(initial);
        Assert.True(workspace.Snapshot.Revision > revision);
        Assert.Equal(initial.Nodes, workspace.Snapshot.Nodes);
    }
}
