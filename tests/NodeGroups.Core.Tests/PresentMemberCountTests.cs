using NodeGroups.Core;
using Xunit;

namespace NodeGroups.Core.Tests;

/// <summary>画布有效成员数与为原生撤销保留的归属分开；只读计数不能修改任一历史快照。</summary>
public sealed class PresentMemberCountTests
{
    [Fact]
    public void MissingMembersAreExcludedWithoutRemovingTheirRetainedOwnership()
    {
        var group = Group("g", new[] { "present", "in-trash", "temporarily-missing" });
        var snapshot = new WorkspaceSnapshot(new[] { Node("present"), Node("outside") }, new[] { group }, revision: 37);

        Assert.Equal(1, snapshot.CountPresentMembers("g"));
        Assert.Equal(1, snapshot.CountPresentMembers("g"));
        Assert.Equal(new[] { "present", "in-trash", "temporarily-missing" }, group.MemberNodeIds);
        Assert.Same(group, snapshot.FindGroupForNode("in-trash"));
        Assert.Same(group, snapshot.FindGroupForNode("temporarily-missing"));
        Assert.Equal(37, snapshot.Revision);
        Assert.Equal(2, snapshot.Nodes.Count);
    }

    [Fact]
    public void UnregisterRestoreAndUnregisterAgainFollowCurrentNodePresence()
    {
        var originalNodes = new[] { Node("a"), Node("b") };
        var workspace = new GroupWorkspace(originalNodes, new[] { Group("g", new[] { "a", "b" }) });
        var original = workspace.Snapshot;
        Assert.Equal(2, original.CountPresentMembers("g"));

        workspace.ReconcileNodes(originalNodes.Take(1));
        var inTrash = workspace.Snapshot;
        Assert.Equal(1, inTrash.CountPresentMembers("g"));
        Assert.Equal("g", inTrash.FindGroupForNode("b")!.Id);
        Assert.Equal(2, inTrash.GetGroup("g").MemberNodeIds.Count);

        // 原生撤销重新登记同一标识即可恢复计数，不需要另外保存可变数字或重复追加成员。
        workspace.ReconcileNodes(originalNodes);
        Assert.Equal(2, workspace.Snapshot.CountPresentMembers("g"));
        workspace.ReconcileNodes(originalNodes.Take(1));
        Assert.Equal(1, workspace.Snapshot.CountPresentMembers("g"));
        Assert.Equal(new[] { "a", "b" }, workspace.Snapshot.GetGroup("g").MemberNodeIds);
        Assert.Equal(2, original.CountPresentMembers("g"));
        Assert.Equal(1, inTrash.CountPresentMembers("g"));
    }

    [Fact]
    public void RemovingRuntimeGeometryPreservesTheOriginalGroupForUndo()
    {
        var workspace = new GroupWorkspace(new[] { Node("a"), Node("b") }, new[] { Group("g", new[] { "a", "b" }) });
        var change = workspace.RemoveNode("b");

        Assert.True(change.HasChanges);
        Assert.Equal(1, workspace.Snapshot.CountPresentMembers("g"));
        Assert.Equal("g", workspace.Snapshot.FindGroupForNode("b")!.Id);
        workspace.Restore(change.Before);
        Assert.Equal(2, workspace.Snapshot.CountPresentMembers("g"));
        workspace.Restore(change.After);
        Assert.Equal(1, workspace.Snapshot.CountPresentMembers("g"));
    }

    [Fact]
    public void TransfersCountOnlyThePresentMembersOfTheirCurrentGroup()
    {
        var workspace = new GroupWorkspace(new[] { Node("a"), Node("b"), Node("c") }, new[]
        {
            Group("source", new[] { "a", "b", "in-trash" }),
            Group("target", new[] { "c" })
        });
        var before = workspace.Snapshot;

        var transfer = workspace.TransferNodes(new[] { "a" }, "target");
        Assert.Equal(1, workspace.Snapshot.CountPresentMembers("source"));
        Assert.Equal(2, workspace.Snapshot.CountPresentMembers("target"));
        Assert.Equal("source", workspace.Snapshot.FindGroupForNode("in-trash")!.Id);
        workspace.TransferNodes(new[] { "a" }, null);
        Assert.Equal(1, workspace.Snapshot.CountPresentMembers("source"));
        Assert.Equal(1, workspace.Snapshot.CountPresentMembers("target"));
        Assert.Null(workspace.Snapshot.FindGroupForNode("a"));

        workspace.Restore(transfer.Before);
        Assert.Equal(2, workspace.Snapshot.CountPresentMembers("source"));
        Assert.Equal(1, workspace.Snapshot.CountPresentMembers("target"));
        Assert.Equal(2, before.CountPresentMembers("source"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CollapsedStateAndGeometryDoNotSubstituteForExplicitMembership(bool collapsed)
    {
        // 实际成员即使位于边框外也应计数；边框内的非成员和缺失成员不能依靠坐标被误计。
        var group = new GroupState("g", "显示数量", new[] { "far-member", "missing-member" }, collapsed, default,
            expandedBounds: new Rect2(-100, -100, 200, 200));
        var snapshot = new WorkspaceSnapshot(new[] { Node("far-member", 5000, 7000), Node("inside-outsider", 0, 0) },
            new[] { group });

        Assert.Equal(1, snapshot.CountPresentMembers("g"));
        Assert.Equal(collapsed, group.Collapsed);
        Assert.Null(snapshot.FindGroupForNode("inside-outsider"));
    }

    [Fact]
    public void EmptyAndEntirelyMissingGroupsStayZeroEvenWhenTheCanvasContainsNodes()
    {
        var snapshot = new WorkspaceSnapshot(Enumerable.Range(0, 100).Select(index => Node("node-" + index)), new[]
        {
            Group("empty", Array.Empty<string>()),
            Group("all-missing", new[] { "in-trash-a", "in-trash-b" })
        });

        Assert.Equal(0, snapshot.CountPresentMembers("empty"));
        Assert.Equal(0, snapshot.CountPresentMembers("all-missing"));
        Assert.Equal(2, snapshot.GetGroup("all-missing").MemberNodeIds.Count);
    }

    [Fact]
    public void PresenceUsesOrdinalIdentityAndNotPositionOrNumericText()
    {
        var snapshot = new WorkspaceSnapshot(new[] { Node("A"), Node("1000000") }, new[]
        {
            Group("lowercase", new[] { "a" }),
            Group("numeric", new[] { "1000000", "0" })
        });

        Assert.Equal(0, snapshot.CountPresentMembers("lowercase"));
        Assert.Equal(1, snapshot.CountPresentMembers("numeric"));
    }

    [Fact]
    public void CountUsesTheFrozenSnapshotAfterCallerCollectionsChange()
    {
        var nodes = new[] { Node("a") };
        var groups = new[] { Group("g", new[] { "a", "b" }) };
        var snapshot = new WorkspaceSnapshot(nodes, groups, revision: 12);
        nodes[0] = Node("b");
        groups[0] = Group("replacement", new[] { "b" });

        Assert.Equal(1, snapshot.CountPresentMembers("g"));
        Assert.Equal("a", snapshot.Nodes[0].Id);
        Assert.Equal(12, snapshot.Revision);
        Assert.Equal(new[] { "a", "b" }, snapshot.GetGroup("g").MemberNodeIds);
    }

    [Fact]
    public void UnknownGroupKeepsGetGroupFailureSemanticsAndLeavesTheSnapshotUnchanged()
    {
        var snapshot = new WorkspaceSnapshot(new[] { Node("a") }, new[] { Group("g", new[] { "a" }) }, revision: 7);

        Assert.Throws<KeyNotFoundException>(() => snapshot.CountPresentMembers("missing-group"));
        Assert.Equal(1, snapshot.CountPresentMembers("g"));
        Assert.Equal(7, snapshot.Revision);
    }

    private static NodeState Node(string id, double x = 0, double y = 0) => new(id, new Point2(x, y), new Point2(80, 40));
    private static GroupState Group(string id, IEnumerable<string> members) => new(id, "显示数量", members, false, default);
}
