using System.Text.Json;
using NodeGroups.Core;
using Xunit;

namespace NodeGroups.Core.Tests;

/// <summary>正式版的确定性多步操作和大规模只读几何回归，不访问 Unity 或原生工程。</summary>
public sealed class ReleaseBoundaryStressTests
{
    [Theory]
    [InlineData(19790615)]
    [InlineData(20261031)]
    [InlineData(8675309)]
    public void SeededCommandsPreserveOwnershipAndRestoreEveryBoundedHistoryBatch(int seed)
    {
        var random = new Random(seed);
        var nodes = Enumerable.Range(0, 200).Select(index => Node(index)).ToArray();
        var workspace = new GroupWorkspace(nodes);
        var history = new UndoRedoHistory();
        var batch = new List<WorkspaceChange>();
        var ownerModel = new Dictionary<string, string>(StringComparer.Ordinal);
        var positions = nodes.ToDictionary(node => node.Id, node => node.Position, StringComparer.Ordinal);

        for (var step = 0; step < 1000; step++)
        {
            var before = workspace.Snapshot;
            var selection = Enumerable.Range(0, random.Next(1, 8)).Select(_ => nodes[random.Next(nodes.Length)].Id).ToArray();
            var group = before.Groups.Count == 0 ? null : before.Groups[random.Next(before.Groups.Count)];
            WorkspaceChange? change = null;
            var command = random.Next(10);
            if (group == null || (command == 0 && before.Groups.Count < 32))
            {
                var id = $"group-{step}";
                change = workspace.CreateGroup(id, "随机节点组 " + step, selection, new Point2(step, -step));
                foreach (var node in selection) ownerModel[node] = id;
            }
            else if (command == 1)
            {
                var target = random.Next(3) == 0 ? null : group.Id;
                change = workspace.TransferNodes(selection, target);
                foreach (var node in selection)
                    if (target == null) ownerModel.Remove(node); else ownerModel[node] = target;
            }
            else if (command == 2) change = workspace.SetCollapsed(group.Id, !group.Collapsed);
            else if (command == 3) change = workspace.RenameGroup(group.Id, "重命名 / " + step + " 🧩");
            else if (command == 4)
            {
                var delta = new Point2(random.Next(-30, 31) / 4d, random.Next(-30, 31) / 4d);
                change = workspace.MoveGroup(group.Id, delta);
                foreach (var member in group.MemberNodeIds) positions[member] += delta;
            }
            else if (command == 5)
            {
                change = workspace.SetExpandedBounds(group.Id, new Rect2(-step, -step, step + 1, step + 2));
                Assert.Equal(group.MemberNodeIds, workspace.Snapshot.GetGroup(group.Id).MemberNodeIds);
            }
            else if (command == 6)
            {
                change = workspace.DissolveGroup(group.Id);
                foreach (var member in group.MemberNodeIds) ownerModel.Remove(member);
            }
            else if (command == 7)
            {
                using var cancelled = workspace.BeginDrag(selection, selection[0], Array.Empty<GroupHitRegion>());
                cancelled.Preview(new Point2(12, -7));
                cancelled.Cancel();
                Assert.Throws<InvalidOperationException>(() => cancelled.Commit(default));
                Assert.Same(before, workspace.Snapshot);
            }
            else if (command == 8)
            {
                // 主节点确定整个多选集的归属；空命中区域显式表示拖出全部节点组。
                var delta = new Point2(11, -5);
                using var drag = workspace.BeginDrag(selection, selection[0], Array.Empty<GroupHitRegion>());
                change = drag.Commit(delta);
                foreach (var member in selection.Distinct(StringComparer.Ordinal))
                { ownerModel.Remove(member); positions[member] += delta; }
            }
            else
            {
                Assert.Throws<ArgumentException>(() => workspace.TransferNodes(selection.Append("missing"), group.Id));
                Assert.Throws<ArgumentException>(() => workspace.SetExpandedBounds(group.Id, new Rect2(0, 0, double.PositiveInfinity, 1)));
                Assert.Throws<ArgumentException>(() => workspace.MoveGroup(group.Id, new Point2(double.NaN, 0)));
                Assert.Same(before, workspace.Snapshot);
            }

            if (change?.HasChanges == true) { history.Push(change); batch.Add(change); }
            Assert.True(workspace.Snapshot.Revision >= before.Revision);
            Assert.Equal(nodes.Length, workspace.Snapshot.Nodes.Count);
            var owners = workspace.Snapshot.Groups.SelectMany(current => current.MemberNodeIds.Select(id => (id, current.Id))).ToArray();
            Assert.Equal(owners.Length, owners.Select(item => item.id).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(ownerModel.Count, owners.Length);
            foreach (var node in workspace.Snapshot.Nodes)
            {
                Assert.Equal(positions[node.Id], node.Position);
                Assert.Equal(ownerModel.GetValueOrDefault(node.Id), workspace.Snapshot.FindGroupForNode(node.Id)?.Id);
            }
            // 限制测试自己的历史窗口，避免将压力测试变成保留全部历史快照的内存测试。
            if (batch.Count >= 32 || step == 999)
            {
                for (var index = batch.Count - 1; index >= 0; index--)
                { Assert.True(history.Undo(workspace)); Assert.Equal(Signature(batch[index].Before), Signature(workspace.Snapshot)); }
                Assert.False(history.Undo(workspace));
                foreach (var item in batch)
                { Assert.True(history.Redo(workspace)); Assert.Equal(Signature(item.After), Signature(workspace.Snapshot)); }
                Assert.False(history.Redo(workspace));
                history.Clear(); batch.Clear();
            }
        }
    }

    [Theory]
    [InlineData(10000)]
    [InlineData(50000)]
    public void ThousandGroupsKeepFrozenTargetsAndEveryIndependentEdgeAtLargeScale(int nodeCount)
    {
        var nodes = Enumerable.Range(0, nodeCount).Select(Node).ToArray();
        const int groupCount = 1000;
        var perGroup = nodeCount / groupCount;
        var groups = Enumerable.Range(0, groupCount).Select(index => new GroupState("g" + index, "组 " + index,
            nodes.Skip(index * perGroup).Take(perGroup).Select(node => node.Id), index % 2 == 0,
            new Point2(index * 100, 0), zIndex: index)).ToArray();
        var workspace = new GroupWorkspace(nodes, groups);
        var before = workspace.Snapshot;
        var edges = Enumerable.Range(0, nodeCount).Select(index => new EdgeState("edge-" + index,
            nodes[index].Id, nodes[(index + 1) % nodeCount].Id, "option-" + index, "分支"))
            .Concat(Enumerable.Range(0, groupCount).Select(index => new EdgeState("duplicate-target-" + index,
                nodes[index].Id, nodes[(index + 1) % nodeCount].Id, "second-option-" + index, "同目标独立选项"))).ToArray();
        var projected = EdgeProjector.Project(before, edges);
        Assert.Equal(edges.Length, projected.Count);
        for (var index = 0; index < edges.Length; index++)
        {
            Assert.Same(edges[index], projected[index].Source);
            var from = before.FindGroupForNode(edges[index].FromNodeId)!;
            var to = before.FindGroupForNode(edges[index].ToNodeId)!;
            var expected = from.Id == to.Id && from.Collapsed ? EdgeVisibility.HiddenInternal :
                from.Collapsed || to.Collapsed ? EdgeVisibility.Projected : EdgeVisibility.Original;
            Assert.Equal(expected, projected[index].Visibility);
        }
        var regions = groups.Select(group => new GroupHitRegion(group.Id, new Rect2(-1, -1, 1000000, 1000000), group.ZIndex)).ToArray();
        using var drag = workspace.BeginDrag(nodes.Take(100).Select(node => node.Id), nodes[0].Id, regions);
        Assert.Equal("g999", drag.Preview(new Point2(1, 1)).TargetGroupId);
        drag.Cancel();
        Assert.Same(before, workspace.Snapshot);
        Assert.False(workspace.ReconcileNodes(nodes.Select(node => node with { })).HasChanges);
    }

    [Fact]
    public void OverflowDuringMultiNodeMoveDoesNotPublishAnyPartialPositionOrRegion()
    {
        var nodes = new[] { Node(0), Node(1) with { Position = new Point2(double.MaxValue, 0) } };
        var workspace = new GroupWorkspace(nodes, new[]
        { new GroupState("g", "组", nodes.Select(node => node.Id), false, default, expandedBounds: new Rect2(0, 0, 100, 100)) });
        var before = workspace.Snapshot;
        Assert.Throws<ArgumentException>(() => workspace.MoveGroup("g", new Point2(double.MaxValue, 0)));
        Assert.Same(before, workspace.Snapshot);
        using var drag = workspace.BeginDrag(nodes.Select(node => node.Id), nodes[0].Id, Array.Empty<GroupHitRegion>());
        Assert.Throws<ArgumentException>(() => drag.Commit(new Point2(double.MaxValue, 0)));
        Assert.Same(before, workspace.Snapshot);
    }

    [Fact]
    public void UnchangedNativeListsDoNotRebuildLargeSnapshotsAtEveryPoll()
    {
        var nodes = Enumerable.Range(0, 10000).Select(Node).ToArray();
        var groups = Enumerable.Range(0, 1000).Select(index => new GroupState("g" + index, "组",
            nodes.Skip(index * 10).Take(10).Select(node => node.Id), false, default)).ToArray();
        var workspace = new GroupWorkspace(nodes, groups);
        var before = workspace.Snapshot;
        for (var index = 0; index < 5; index++) workspace.ReconcileNodes(nodes);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var same = true;
        for (var index = 0; index < 50; index++) same &= !workspace.ReconcileNodes(nodes).HasChanges;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.True(same);
        Assert.Same(before, workspace.Snapshot);
        // 空闲轮询允许命令与枚举器的小额分配，但不能反复分配上万节点的完整索引。
        Assert.True(allocated < 65536, $"Unchanged synchronization allocated {allocated} bytes across 50 polls.");
    }

    [Fact]
    public void NativeSynchronizationEnumeratesDeferredInputOnlyOnceAndDefensivelyCopiesChangedLists()
    {
        var workspace = new GroupWorkspace(new[] { Node(0), Node(1) });
        var enumerations = 0;
        IEnumerable<NodeState> Deferred()
        {
            if (++enumerations > 1) throw new InvalidOperationException("The native snapshot is single-use.");
            yield return Node(0) with { Position = new Point2(42, 12) };
            yield return Node(1);
        }
        workspace.ReconcileNodes(Deferred());
        Assert.Equal(1, enumerations);
        Assert.Equal(new Point2(42, 12), workspace.Snapshot.GetNode("n0").Position);
        var changed = new List<NodeState> { Node(0), Node(1) };
        workspace.ReconcileNodes(changed);
        changed.Clear();
        Assert.Equal(2, workspace.Snapshot.Nodes.Count);
        Assert.Equal(Node(0).Position, workspace.Snapshot.GetNode("n0").Position);

        // IReadOnlyList 仍可能只有一次合法枚举；快退不能先枚举部分输入再重新枚举它。
        var once = new SingleEnumerationList(new[] { Node(0) with { Position = new Point2(43, 12) }, Node(1) });
        workspace.ReconcileNodes(once);
        Assert.Equal(new Point2(43, 12), workspace.Snapshot.GetNode("n0").Position);
    }

    [Fact]
    public void FiniteComponentsWithOverflowedNodeBoundsAreRejectedAtomically()
    {
        var invalid = new NodeState("overflow", new Point2(double.MaxValue, 0), new Point2(double.MaxValue, 1));
        Assert.False(invalid.Bounds.IsValid);
        Assert.Throws<ArgumentException>(() => new WorkspaceSnapshot(new[] { invalid }, Array.Empty<GroupState>()));
        var workspace = new GroupWorkspace(new[] { Node(0) });
        var before = workspace.Snapshot;
        Assert.Throws<ArgumentException>(() => workspace.ReconcileNodes(new[] { invalid }));
        Assert.Same(before, workspace.Snapshot);
    }

    [Fact]
    public void LargeIncomingSelectionKeepsOneTranslationBelowAllExistingMembers()
    {
        var existing = Enumerable.Range(0, 10000).Select(Node).ToArray();
        var incoming = Enumerable.Range(0, 500).Select(index => new NodeState("incoming-" + index,
            new Point2(index % 20 * 100, index / 20 * -70), new Point2(80, 40))).ToArray();
        var delta = NodePlacement.FindTranslationToFreeSpace(existing, incoming, default, incoming[0].Id);
        var lowestBottom = existing.Min(node => node.Bounds.Y);
        foreach (var node in incoming)
        {
            var moved = node.Bounds.Translate(delta);
            Assert.True(moved.IsValid);
            Assert.True(moved.Y + moved.Height < lowestBottom);
            Assert.Equal(node.Position - incoming[0].Position, node.Position + delta - (incoming[0].Position + delta));
        }
    }

    [Fact]
    public void RoundedGapStillUsesActualIntersectionWhenLowestMemberIsHorizontallySeparate()
    {
        // 间距被舍入吞掉时，不可只按全组最低 Y 断言碰撞；最低点可能在远处。
        var existing = new[]
        {
            new NodeState("left", new Point2(0, 100), default),
            new NodeState("bottom-far-right", new Point2(1e10, 0), default)
        };
        var incoming = new[] { new NodeState("new", new Point2(0, 10), default) };
        Assert.Equal(new Point2(0, -10), NodePlacement.FindTranslationToFreeSpace(existing, incoming, default, "new", double.Epsilon));
    }

    internal static NodeState Node(int index) => new("n" + index, new Point2(index * 2, -(index % 37)), new Point2(80, 40));

    private static string Signature(WorkspaceSnapshot snapshot) => JsonSerializer.Serialize(new { snapshot.Nodes, snapshot.Groups });

    private sealed class SingleEnumerationList : IReadOnlyList<NodeState>
    {
        private readonly NodeState[] nodes;
        private int enumerations;
        internal SingleEnumerationList(NodeState[] nodes) => this.nodes = nodes;
        public int Count => nodes.Length;
        public NodeState this[int index] => nodes[index];
        public IEnumerator<NodeState> GetEnumerator()
        {
            if (++enumerations > 1) throw new InvalidOperationException("This list can only be enumerated once.");
            foreach (var node in nodes) yield return node;
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
