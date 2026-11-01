using System.Collections;
using NodeGroups.Core;
using Xunit;

namespace NodeGroups.Core.Tests;

/// <summary>保存点以已确认写入的组内容为准，独立于原生节点、撤销步数和拖拽预览。</summary>
public sealed class GroupSaveStateTests
{
    [Fact]
    public void EmptyGroupCreationUndoRedoAndDissolveFollowSavedContent()
    {
        var state = new GroupSaveState(Array.Empty<GroupState>());
        Assert.False(state.Dirty);

        var group = Group();
        state.ObserveCommitted(new[] { group });
        Assert.True(state.Dirty);
        // 撤销建组回到保存点，重做则再次变脏；不能仅凭调用过 Restore 判断。
        state.ObserveCommitted(Array.Empty<GroupState>());
        Assert.False(state.Dirty);
        state.ObserveCommitted(new[] { group });
        Assert.True(state.Dirty);
        state.AcceptSaved(new[] { group });
        Assert.False(state.Dirty);
        state.ObserveCommitted(Array.Empty<GroupState>());
        Assert.True(state.Dirty);
        state.ObserveCommitted(new[] { group });
        Assert.False(state.Dirty);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("members")]
    [InlineData("member-order")]
    [InlineData("member-case")]
    [InlineData("collapsed")]
    [InlineData("anchor")]
    [InlineData("color")]
    [InlineData("z-index")]
    [InlineData("bounds")]
    [InlineData("auto-bounds")]
    public void EveryPersistedFieldAndMemberOrderParticipatesInComparison(string field)
    {
        var original = Group(members: new[] { "a", "b" });
        var changed = field switch
        {
            "id" => Group(id: "different", members: new[] { "a", "b" }),
            "name" => Group(name: "另一个名称", members: new[] { "a", "b" }),
            "members" => Group(members: new[] { "a", "b", "missing-native-node" }),
            "member-order" => Group(members: new[] { "b", "a" }),
            "member-case" => Group(members: new[] { "A", "b" }),
            "collapsed" => Group(members: new[] { "a", "b" }, collapsed: true),
            "anchor" => Group(members: new[] { "a", "b" }, anchor: new Point2(21, 30)),
            "color" => Group(members: new[] { "a", "b" }, color: "#112233"),
            "z-index" => Group(members: new[] { "a", "b" }, zIndex: 8),
            "bounds" => Group(members: new[] { "a", "b" }, bounds: new Rect2(1, 2, 300, 401)),
            _ => new GroupState("g", "节点组", new[] { "a", "b" }, false, new Point2(20, 30), "#4C88B8", 7)
        };
        var state = new GroupSaveState(new[] { original });
        state.ObserveCommitted(new[] { changed });
        Assert.True(state.Dirty);
        state.ObserveCommitted(new[] { Clone(original) });
        Assert.False(state.Dirty);
    }

    [Fact]
    public void ReorderingEqualLayerGroupsIsARealPersistedChange()
    {
        var first = Group("first");
        var second = Group("second");
        var state = new GroupSaveState(new[] { first, second });
        state.ObserveCommitted(new[] { second, first });
        Assert.True(state.Dirty);
        state.ObserveCommitted(new[] { Clone(first), Clone(second) });
        Assert.False(state.Dirty);
    }

    [Fact]
    public void AcknowledgingFrozenOlderSaveDoesNotCleanNewerCommittedChange()
    {
        var baseline = Group();
        var frozen = Group(name: "保存时的名称");
        var newer = Group(name: "保存期间的后续修改");
        var state = new GroupSaveState(new[] { baseline });
        state.ObserveCommitted(new[] { frozen });
        state.ObserveCommitted(new[] { newer });
        state.AcceptSaved(new[] { frozen });
        Assert.True(state.Dirty);
        // 撤销到实际成功写入的内容才变干净；原来的更旧保存点不再是基准。
        state.ObserveCommitted(new[] { Clone(frozen) });
        Assert.False(state.Dirty);
        state.ObserveCommitted(new[] { baseline });
        Assert.True(state.Dirty);
        state.ObserveCommitted(new[] { newer });
        state.AcceptSaved(new[] { Clone(newer) });
        Assert.False(state.Dirty);
    }

    [Fact]
    public void FailedOrRecoveredStateRequiresConfirmedSaveEvenAfterUndoToBaseline()
    {
        var baseline = Group();
        var state = new GroupSaveState(new[] { baseline }, requiresSave: true);
        Assert.True(state.Dirty);
        state.ObserveCommitted(new[] { Clone(baseline) });
        Assert.True(state.Dirty);
        state.AcceptSaved(new[] { Clone(baseline) });
        Assert.False(state.Dirty);

        state.RequireSave();
        state.RequireSave();
        state.ObserveCommitted(new[] { Group(name: "临时修改") });
        state.ObserveCommitted(new[] { baseline });
        Assert.True(state.Dirty);
        state.AcceptSaved(new[] { baseline });
        Assert.False(state.Dirty);
    }

    [Fact]
    public void UncommittedWorkspacePreviewDoesNotAffectCachedDirtyState()
    {
        var workspace = new GroupWorkspace(Array.Empty<NodeState>(), new[] { Group() });
        var state = new GroupSaveState(workspace.Snapshot.Groups);
        workspace.SetExpandedBounds("g", new Rect2(1, 2, 900, 1000));
        Assert.False(state.Dirty);
        state.ObserveCommitted(workspace.Snapshot.Groups);
        Assert.True(state.Dirty);
    }

    [Fact]
    public void ConstructorObserveAndSaveFreezeCallerOwnedArrays()
    {
        var baseline = Group();
        var changed = Group(name: "新的名称");
        var input = new[] { baseline };
        var state = new GroupSaveState(input);
        input[0] = changed;
        state.ObserveCommitted(new[] { baseline });
        Assert.False(state.Dirty);

        var current = new[] { changed };
        state.ObserveCommitted(current);
        current[0] = baseline;
        state.AcceptSaved(new[] { baseline });
        Assert.True(state.Dirty);

        var saved = new[] { changed };
        state.AcceptSaved(saved);
        saved[0] = baseline;
        state.ObserveCommitted(new[] { Clone(changed) });
        Assert.False(state.Dirty);
    }

    [Fact]
    public void InputsAreEnumeratedOnceAndDirtyReadsNeverReenumerate()
    {
        var baseline = Group();
        var state = new GroupSaveState(new SingleEnumeration(new[] { baseline }));
        state.ObserveCommitted(new SingleEnumeration(new[] { Group(name: "已提交修改") }));
        for (var index = 0; index < 100; index++) Assert.True(state.Dirty);
        state.AcceptSaved(new SingleEnumeration(new[] { Group(name: "已提交修改") }));
        for (var index = 0; index < 100; index++) Assert.False(state.Dirty);
    }

    [Fact]
    public void NullInputsAndNullElementsAreRejectedWithoutPartialStateChanges()
    {
        Assert.Throws<ArgumentNullException>(() => new GroupSaveState(null!));
        Assert.Throws<ArgumentException>(() => new GroupSaveState(new GroupState[] { null! }));
        var state = new GroupSaveState(new[] { Group() });
        Assert.Throws<ArgumentNullException>(() => state.ObserveCommitted(null!));
        Assert.Throws<ArgumentException>(() => state.ObserveCommitted(new GroupState[] { Group(), null! }));
        Assert.False(state.Dirty);
        state.ObserveCommitted(new[] { Group(name: "新名称") });
        Assert.Throws<ArgumentNullException>(() => state.AcceptSaved(null!));
        Assert.Throws<ArgumentException>(() => state.AcceptSaved(new GroupState[] { null! }));
        Assert.True(state.Dirty);
        state.ObserveCommitted(new[] { Group() });
        Assert.False(state.Dirty);
    }

    [Fact]
    public void ThrowingInputCannotPartiallyReplaceCommittedStateOrSavedBaseline()
    {
        static IEnumerable<GroupState> Broken()
        {
            yield return Group(name: "不能部分导入");
            throw new InvalidOperationException("Injected caller enumeration failure.");
        }
        var state = new GroupSaveState(new[] { Group() });
        Assert.Throws<InvalidOperationException>(() => state.ObserveCommitted(Broken()));
        Assert.False(state.Dirty);
        state.ObserveCommitted(new[] { Group(name: "已提交修改") });
        state.RequireSave();
        Assert.Throws<InvalidOperationException>(() => state.AcceptSaved(Broken()));
        state.ObserveCommitted(new[] { Group() });
        Assert.True(state.Dirty);
        state.AcceptSaved(new[] { Group() });
        Assert.False(state.Dirty);
    }

    private static GroupState Group(string id = "g", string name = "节点组", IEnumerable<string>? members = null,
        bool collapsed = false, Point2? anchor = null, string color = "#4C88B8", int zIndex = 7, Rect2? bounds = null) =>
        new(id, name, members ?? Array.Empty<string>(), collapsed, anchor ?? new Point2(20, 30), color, zIndex,
            bounds ?? new Rect2(1, 2, 300, 400));

    private static GroupState Clone(GroupState group) => new(group.Id, new string(group.Name.ToCharArray()),
        group.MemberNodeIds.Select(member => new string(member.ToCharArray())), group.Collapsed, group.Anchor,
        group.Color, group.ZIndex, group.ExpandedBounds);

    /// <summary>第二次枚举即失败，确保保存回执不会重复触发调用方的延迟序列。</summary>
    private sealed class SingleEnumeration : IEnumerable<GroupState>
    {
        private readonly IEnumerable<GroupState> groups;
        private bool enumerated;
        internal SingleEnumeration(IEnumerable<GroupState> groups) => this.groups = groups;
        public IEnumerator<GroupState> GetEnumerator()
        {
            if (enumerated) throw new InvalidOperationException("Input was enumerated more than once.");
            enumerated = true;
            return groups.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
