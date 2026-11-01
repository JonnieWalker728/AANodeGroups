using System.Text;
using System.Text.Json;
using AzureArchive.Automation;
using Il2CppInterop.Runtime;
using NodeGroups.Core;
using Studio.Scripts;
using Studio.Scripts.Nodes;
using Studio.Scripts.OperationManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AzureArchive.NodeGroups.Runtime.Tests;

/// <summary>在独立原生工程中验证回收站暂存与组有效计数；节点注册和历史完全由原应用完成。</summary>
internal static class TrashMembershipTests
{
    private enum Phase { SaveMain, AwaitFixture, SettleNode, SettleTrash, ParcelStarted, ParcelHeld, ReturnSettled,
        NextCase, BatchSettle, BatchTrash, SaveTrash, AwaitReload, FinalFixtureSave, AwaitMain }
    private enum ReturnCase { OriginalGroup, OtherGroup, Outside, CollapsedGroup, Cancel, OverlapTop }
    private sealed class State
    {
        internal EditorSession Owner = null!;
        internal Phase Phase;
        internal DateTime Deadline;
        internal int Frames, InitialTrash;
        internal int CaseIndex, BeforeDropHistory, BeforeReturnHistory;
        internal string MainPath = "", MainProjectId = "", MainStory = "", MainGroups = "", MainHash = "";
        internal byte[] MainCompiled = Array.Empty<byte>(), MainSidecar = Array.Empty<byte>();
        internal NodeState[] MainNodes = Array.Empty<NodeState>();
        internal bool MainSidecarExisted;
        internal string FixturePath = "", FixtureProjectId = "", RecoveryPath = "", TracePath = "";
        internal string GroupA = "", GroupB = "", NodeId = "";
        internal Node? Node;
        internal Trashcan Trash = null!;
        internal NodeParcel? Parcel;
        internal Point2 BeforeDropPosition, Target;
        internal string TrashedGroups = "";
        internal PaletteDragTests.PointerContext? Pointer;
        internal string ExtraNodeId = "", ReloadStory = "";
        internal NodeState[] BatchNodes = Array.Empty<NodeState>(), ReloadNodes = Array.Empty<NodeState>();
        internal int BatchHistory, BatchTrashCount;
    }
    private static State? active;

    /// <summary>先冻结主工程，再通过真实场景加载隔离的回收站夹具。</summary>
    internal static void Begin(EditorSession session, Action<bool, string> check)
    {
        var report = Environment.GetEnvironmentVariable("NODEGROUPS_TEST_REPORT");
        if (string.IsNullOrWhiteSpace(report) || active != null) throw new InvalidOperationException("Trash tests require one isolated report process.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(report))!;
        var main = Path.GetFullPath(PersistentData.projectPath);
        if (!main.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Trash tests cannot save a user project.");
        active = new State
        {
            Owner = session, MainPath = main, MainProjectId = session.ProjectId,
            MainStory = Story(session.Editor.CaptureProject()), MainNodes = session.ReadNodes(),
            MainCompiled = AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray(),
            FixturePath = Path.Combine(directory, "trash-fixture" + ProjectFileStore.ProjectExtension),
            TracePath = Path.Combine(directory, "trash-native-trace.log")
        };
        check(active.MainCompiled.Length > 0, "trash regression starts from a compilable isolated main fixture");
        SetPhase(active, Phase.SaveMain); session.Editor.Save(true);
    }

    /// <summary>只推进当前场景的有限工作，并等待真实 Start、回收站动画及场景销毁自然执行。</summary>
    internal static bool Tick(EditorSession session, Action<bool, string> check)
    {
        var state = active ?? throw new InvalidOperationException("Trash tests were not started.");
        if (DateTime.UtcNow > state.Deadline) throw new TimeoutException("Trash tests timed out in " + state.Phase);
        if (session.IsDisposed) return false;
        state.Frames++;
        if (state.Phase == Phase.SaveMain)
        {
            if (!ReferenceEquals(state.Owner, session) || !Saved(session)) return false;
            state.MainGroups = Groups(session); state.MainHash = EditorSession.Hash(state.MainPath);
            var sidecar = GroupStorePath(state.MainPath); state.MainSidecarExisted = File.Exists(sidecar);
            state.MainSidecar = state.MainSidecarExisted ? File.ReadAllBytes(sidecar) : Array.Empty<byte>();
            var data = NativeProjectFixture.Build(); state.FixtureProjectId = data.ProjectId;
            state.RecoveryPath = RecoveryPath(data.ProjectId);
            if (File.Exists(state.FixturePath) || File.Exists(state.RecoveryPath)) throw new InvalidOperationException("Trash fixture identity already exists.");
            ProjectFileStore.Save(state.FixturePath, data, null, false, null);
            SwitchScene(state, session, state.FixturePath, Phase.AwaitFixture); return false;
        }
        if (state.Phase == Phase.AwaitMain)
        {
            if (ReferenceEquals(state.Owner, session) || session.ProjectId != state.MainProjectId || state.Frames < 5) return false;
            session.Tick();
            check(Story(session.Editor.CaptureProject()) == state.MainStory && NodesEqual(session.ReadNodes(), state.MainNodes) &&
                AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray().SequenceEqual(state.MainCompiled),
                "trash regression restores the original main native graph, positions and compiled bytes");
            check(Groups(session) == state.MainGroups && EditorSession.Hash(state.MainPath) == state.MainHash &&
                File.Exists(GroupStorePath(state.MainPath)) == state.MainSidecarExisted &&
                (!state.MainSidecarExisted || File.ReadAllBytes(GroupStorePath(state.MainPath)).SequenceEqual(state.MainSidecar)),
                "trash regression preserves the frozen main project and group sidecar bytes");
            check(OperationManager.history.Count == 0 && OperationManager.future.Count == 0 && session.Find(state.NodeId) == null,
                "trash native scene reload starts fresh history and retains no temporary trash node");
            DeleteOwnedRecovery(state); active = null; return true;
        }
        if (state.Phase == Phase.AwaitFixture)
        {
            if (ReferenceEquals(state.Owner, session) || session.ProjectId != state.FixtureProjectId || state.Frames < 5) return false;
            state.Owner = session;
            state.Trash = UnityEngine.Object.FindObjectOfType<Trashcan>() ?? throw new InvalidOperationException("Native trashcan is missing.");
            state.InitialTrash = state.Trash.trash.Count;
            check(state.InitialTrash == 0 && session.Workspace.Snapshot.Groups.Count == 0 && OperationManager.history.Count == 0,
                "trash fixture starts with its own empty native trash, history and groups");
            Blackboard.selected.Clear(); session.CreateGroup(true); state.GroupA = session.SelectedGroup!;
            session.Commit(session.Workspace.SetExpandedBounds(state.GroupA, new Rect2(-3500, -1600, 2600, 2500)));
            session.CreateGroup(true); state.GroupB = session.SelectedGroup!;
            session.Commit(session.Workspace.SetExpandedBounds(state.GroupB, new Rect2(1000, -1600, 2600, 2500)));
            state.NodeId = Guid.NewGuid().ToString("D");
            var operation = new NodeCreateOperation(Il2CppType.Of<ScriptNode>(), NativeGuid.Parse(state.NodeId), new Vector2(-2200, -400));
            operation.Redo(); OperationManager.WriteIsolated(operation);
            state.Node = session.Find(state.NodeId) ?? throw new InvalidOperationException("Native node creation did not register the trash fixture.");
            SetPhase(state, Phase.SettleNode); return false;
        }
        if (state.Phase == Phase.AwaitReload)
        {
            if (ReferenceEquals(state.Owner, session) || session.ProjectId != state.FixtureProjectId || state.Frames < 5) return false;
            state.Owner = session; session.Tick();
            check(NodesEqual(session.ReadNodes(), state.ReloadNodes) && Story(session.Editor.CaptureProject()) == state.ReloadStory &&
                Caption(session, state.GroupA).EndsWith(" (1)", StringComparison.Ordinal) &&
                Caption(session, state.GroupB).EndsWith(" (0)", StringComparison.Ordinal) && session.Find(state.NodeId) == null,
                "saving and reopening counts only the registered group member and does not resurrect the recycled node");
            check(OperationManager.history.Count == 0 && OperationManager.future.Count == 0,
                "trash save/reopen follows the native fresh-history lifecycle rather than restoring old undo commands");
            session.Editor.Save(true); SetPhase(state, Phase.FinalFixtureSave); return false;
        }
        if (!ReferenceEquals(state.Owner, session) || session.ProjectId != state.FixtureProjectId)
            throw new InvalidOperationException("Trash fixture changed owner unexpectedly.");
        if (state.Phase == Phase.SettleNode)
        {
            if (state.Frames < 5) return false;
            session.Sync(); session.Commit(session.Workspace.TransferNodes(new[] { state.NodeId }, state.GroupA)); session.Tick();
            check(Caption(session, state.GroupA).EndsWith(" (1)", StringComparison.Ordinal),
                "trash fixture displays one registered native member before recycling");
            StartDrop(state, session, check); return false;
        }
        if (state.Phase == Phase.SettleTrash)
        {
            if (state.Frames < 5) return false;
            session.Tick(); Trace(state, session, "settled trash before effective-count assertion");
            check(session.Workspace.Snapshot.GetGroup(state.GroupA).MemberNodeIds.Contains(state.NodeId),
                "trash keeps the native identity association available for a later Undo");
            check(Caption(session, state.GroupA).EndsWith(" (0)", StringComparison.Ordinal),
                "a successfully recycled group member is excluded from the displayed canvas member count");
            var history = OperationManager.history.Count;
            OperationManager.Undo(); session.Tick(); Trace(state, session, "after native undo trash");
            check(session.Find(state.NodeId) != null && Near(session.Position(session.Find(state.NodeId)!), state.BeforeDropPosition) &&
                session.Workspace.Snapshot.FindGroupForNode(state.NodeId)?.Id == state.GroupA &&
                Caption(session, state.GroupA).EndsWith(" (1)", StringComparison.Ordinal) &&
                OperationManager.history.Count == state.BeforeDropHistory,
                "one native Undo restores the pre-trash group member and exact canvas position without undoing an unrelated command");
            OperationManager.Redo(); session.Tick(); Trace(state, session, "after native redo trash");
            check(session.Find(state.NodeId) == null && Caption(session, state.GroupA).EndsWith(" (0)", StringComparison.Ordinal) &&
                OperationManager.history.Count == history,
                "one native Redo unregisters the restored member and reduces the group display again");
            PrepareReturn(state, session, check); return false;
        }
        if (state.Phase == Phase.ParcelStarted)
        {
            if (state.Frames < 4) return false;
            var parcel = state.Parcel!;
            state.Pointer = new PaletteDragTests.PointerContext(session, parcel);
            parcel.OnDragStart();
            if ((ReturnCase)state.CaseIndex != ReturnCase.Cancel)
            {
                state.Pointer.MoveTo(state.Target); parcel.OnDrag();
            }
            Trace(state, session, "parcel drag preview");
            check(session.Find(state.NodeId) == null && Caption(session, state.GroupA).EndsWith(" (0)", StringComparison.Ordinal),
                "a trash parcel remains unregistered and uncounted while its return gesture is only a preview");
            SetPhase(state, Phase.ParcelHeld); return false;
        }
        if (state.Phase == Phase.ParcelHeld)
        {
            if (state.Frames < 4) return false;
            var cancel = (ReturnCase)state.CaseIndex == ReturnCase.Cancel;
            if (!cancel) { state.Pointer!.MoveTo(state.Target); state.Parcel!.OnDrag(); }
            var target = ExpectedTarget(state);
            if (!cancel)
                check(state.Parcel!.outside && session.DropTarget == target,
                    "the real trash parcel previews the intended canvas target for " + (ReturnCase)state.CaseIndex);
            else check(!state.Parcel!.outside, "a cancelled trash return stays inside its original native parcel bounds");
            try { state.Parcel!.OnDragEnd(); }
            finally { state.Pointer!.Dispose(); state.Pointer = null; }
            SetPhase(state, Phase.ReturnSettled); return false;
        }
        if (state.Phase == Phase.ReturnSettled)
        {
            if (state.Frames < 5) return false;
            session.Tick(); Trace(state, session, "settled parcel release " + (ReturnCase)state.CaseIndex);
            if ((ReturnCase)state.CaseIndex == ReturnCase.Cancel)
            {
                check(session.Find(state.NodeId) == null && state.Trash.trash.Count == state.InitialTrash + 1 &&
                    OperationManager.history.Count == state.BeforeReturnHistory && Groups(session) == state.TrashedGroups &&
                    Caption(session, state.GroupA).EndsWith(" (0)", StringComparison.Ordinal),
                    "cancelling a real trash parcel return preserves its native trash, group association, count and history");
            }
            else
            {
                var target = ExpectedTarget(state); var node = session.Find(state.NodeId);
                check(node != null && state.Trash.trash.Count == state.InitialTrash &&
                    session.Workspace.Snapshot.FindGroupForNode(state.NodeId)?.Id == target,
                    "a registered native trash return commits the final group ownership for " + (ReturnCase)state.CaseIndex);
                check(Caption(session, state.GroupA).EndsWith(target == state.GroupA ? " (1)" : " (0)", StringComparison.Ordinal) &&
                    Caption(session, state.GroupB).EndsWith(target == state.GroupB ? " (1)" : " (0)", StringComparison.Ordinal),
                    "a returned trash node is counted exactly once in its destination for " + (ReturnCase)state.CaseIndex);
                var returned = session.Position(node!); var returnedGroups = Groups(session); var history = OperationManager.history.Count;
                OperationManager.Undo(); session.Tick(); Trace(state, session, "undo parcel return");
                check(session.Find(state.NodeId) == null && state.Trash.trash.Count == state.InitialTrash + 1 &&
                    Caption(session, state.GroupA).EndsWith(" (0)", StringComparison.Ordinal) &&
                    Caption(session, state.GroupB).EndsWith(" (0)", StringComparison.Ordinal) &&
                    OperationManager.history.Count == state.BeforeReturnHistory && Groups(session) == state.TrashedGroups,
                    "one native Undo returns the restored node to trash and restores its metadata boundary for " + (ReturnCase)state.CaseIndex);
                OperationManager.Redo(); session.Tick();
                check(session.Find(state.NodeId) != null && Near(session.Position(session.Find(state.NodeId)!), returned) &&
                    Groups(session) == returnedGroups && OperationManager.history.Count == history,
                    "one native Redo restores the returned node, final position and ownership for " + (ReturnCase)state.CaseIndex);
            }
            RollbackCase(state, session, check);
            state.CaseIndex++;
            SetPhase(state, Phase.NextCase); return false;
        }
        if (state.Phase == Phase.NextCase)
        {
            if (state.Frames < 5) return false;
            if (state.CaseIndex > (int)ReturnCase.OverlapTop)
            {
                state.ExtraNodeId = Guid.NewGuid().ToString("D");
                var pos = session.Position(session.Find(state.NodeId)!);
                var operation = new NodeCreateOperation(Il2CppType.Of<ScriptNode>(), NativeGuid.Parse(state.ExtraNodeId),
                    new Vector2((float)pos.X + 250, (float)pos.Y - 200));
                operation.Redo(); OperationManager.WriteIsolated(operation);
                SetPhase(state, Phase.BatchSettle); return false;
            }
            if ((ReturnCase)state.CaseIndex == ReturnCase.OverlapTop)
            {
                var a = session.Bounds(session.Workspace.Snapshot.GetGroup(state.GroupA));
                session.Commit(session.Workspace.SetExpandedBounds(state.GroupB, a)); session.SelectGroup(state.GroupB);
            }
            StartDrop(state, session, check); return false;
        }
        if (state.Phase == Phase.BatchSettle)
        {
            if (state.Frames < 5) return false;
            session.Sync(); session.Commit(session.Workspace.TransferNodes(new[] { state.ExtraNodeId }, state.GroupA)); session.Tick();
            check(Caption(session, state.GroupA).EndsWith(" (2)", StringComparison.Ordinal),
                "the multi-selection trash fixture starts with two real native members");
            state.BatchNodes = session.ReadNodes(); state.BatchHistory = OperationManager.history.Count;
            var first = session.Find(state.NodeId)!; var second = session.Find(state.ExtraNodeId)!;
            Blackboard.selected.Clear(); Blackboard.selected.Add(first); Blackboard.selected.Add(second);
            using (var touch = new NativeCommandIntegrationTests.TouchContext(session, first))
            {
                first.OnDragStart();
                var world = state.Trash.bg.worldCorners.ToArray().Aggregate(Vector3.zero, (sum, value) => sum + value) / 4;
                var local = session.Board.treeRoot.InverseTransformPoint(world);
                first.OnDrag(touch.MoveTo(new Point2(local.x, local.y)));
                check(state.Trash.isCursorInBound, "the native multi-selection pointer reaches the real trashcan");
                first.OnDragEnd();
            }
            // 原生明确拒绝批量回收，Mod 不能扩大原生删除范围；正常拖动仍可改变位置和归属。
            state.BatchTrashCount = (session.Find(state.NodeId) == null ? 1 : 0) + (session.Find(state.ExtraNodeId) == null ? 1 : 0);
            check(state.BatchTrashCount == 0 && state.Trash.trash.Count == state.InitialTrash,
                "the original app rejects multi-selection recycling and keeps both nodes registered");
            Trace(state, session, "after multi-selection native trash drag");
            SetPhase(state, Phase.BatchTrash); return false;
        }
        if (state.Phase == Phase.BatchTrash)
        {
            if (state.Frames < 5) return false;
            session.Tick();
            var movedCount = session.Workspace.Snapshot.CountPresentMembers(state.GroupA);
            check(Caption(session, state.GroupA).EndsWith(" (" + movedCount + ")", StringComparison.Ordinal),
                "a rejected batch recycle displays the members retained by the completed native canvas drag");
            var history = OperationManager.history.Count;
            OperationManager.Undo(); session.Tick();
            check(OperationManager.history.Count == state.BatchHistory && NodesEqual(session.ReadNodes(), state.BatchNodes) &&
                state.Trash.trash.Count == state.InitialTrash && Caption(session, state.GroupA).EndsWith(" (2)", StringComparison.Ordinal),
                "one native Undo restores all affected multi-selected nodes and both displayed members without prior-command loss");
            OperationManager.Redo(); session.Tick();
            check(OperationManager.history.Count == history && state.Trash.trash.Count == state.InitialTrash &&
                Caption(session, state.GroupA).EndsWith(" (" + movedCount + ")", StringComparison.Ordinal),
                "one native Redo restores the rejected batch drag without inventing recycled nodes");
            OperationManager.Undo(); session.Tick(); OperationManager.future.Clear(); NativeHistory.Prune();
            // 单独保存一个在画布、一个在回收站的状态；原生决定回收站是否跨场景保留。
            StartDrop(state, session, check);
            session.Tick();
            check(Caption(session, state.GroupA).EndsWith(" (1)", StringComparison.Ordinal),
                "the persisted trash fixture has one canvas member and one native recycled member");
            session.Editor.Save(true); SetPhase(state, Phase.SaveTrash); return false;
        }
        if (state.Phase == Phase.SaveTrash)
        {
            if (!Saved(session)) return false;
            state.ReloadNodes = session.ReadNodes(); state.ReloadStory = Story(session.Editor.CaptureProject());
            check(session.Find(state.NodeId) == null && Caption(session, state.GroupA).EndsWith(" (1)", StringComparison.Ordinal),
                "successful native saving retains the reduced effective group count");
            SwitchScene(state, session, state.FixturePath, Phase.AwaitReload); return false;
        }
        if (state.Phase == Phase.FinalFixtureSave)
        {
            if (!Saved(session)) return false;
            check(Caption(session, state.GroupA).EndsWith(" (1)", StringComparison.Ordinal),
                "confirming recovered metadata after reload does not re-count a missing trash identity");
            SwitchScene(state, session, state.MainPath, Phase.AwaitMain); return false;
        }
        return false;
    }

    /// <summary>真实按下、移动与释放；不能直接补写历史，否则无法发现回收站错误链接前一条命令的问题。</summary>
    private static void StartDrop(State state, EditorSession session, Action<bool, string> check)
    {
        var node = session.Find(state.NodeId) ?? throw new InvalidOperationException("Trash source node is no longer on the native canvas.");
        state.Node = node; state.BeforeDropHistory = OperationManager.history.Count; state.BeforeDropPosition = session.Position(node);
        // 原生将 selected 中的节点视为批量拖动，批量节点不走单节点回收入口。
        Blackboard.selected.Clear();
        Trace(state, session, "before actual Node trash drag " + (ReturnCase)state.CaseIndex);
        using (var touch = new NativeCommandIntegrationTests.TouchContext(session, node))
        {
            node.OnDragStart();
            var world = state.Trash.bg.worldCorners.ToArray().Aggregate(Vector3.zero, (sum, value) => sum + value) / 4;
            var local = session.Board.treeRoot.InverseTransformPoint(world);
            node.OnDrag(touch.MoveTo(new Point2(local.x, local.y)));
            check(state.Trash.isCursorInBound, "the native pointer lies in the real trashcan before node release");
            check(!node.inBatch && node.deletionMarked,
                "the native single-node drag samples the trash hover before its release handler");
            node.OnDragEnd();
        }
        state.Parcel = state.Trash.trash.Count == 0 ? null : state.Trash.trash.Peek().content;
        Trace(state, session, "after actual Node trash drag");
        check(session.Find(state.NodeId) == null && state.Trash.trash.Count == state.InitialTrash + 1 &&
            state.Parcel?.content.GetComponentInChildren<Node>(true)?.Pointer == node.Pointer,
            "native trash retains the same dragged node in a real parcel while unregistering it from the canvas");
        SetPhase(state, Phase.SettleTrash);
    }

    private static string? ExpectedTarget(State state) => (ReturnCase)state.CaseIndex switch
    { ReturnCase.OriginalGroup or ReturnCase.CollapsedGroup => state.GroupA, ReturnCase.OtherGroup or ReturnCase.OverlapTop => state.GroupB, _ => null };

    private static void PrepareReturn(State state, EditorSession session, Action<bool, string> check)
    {
        if ((ReturnCase)state.CaseIndex == ReturnCase.CollapsedGroup) session.Toggle(state.GroupA);
        state.Parcel = state.Trash.trash.Peek().content;
        var target = ExpectedTarget(state);
        var bounds = target == null ? new Rect2(6000, 2500, 800, 800) : session.DropBounds(session.Workspace.Snapshot.GetGroup(target));
        state.Target = new Point2(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        state.BeforeReturnHistory = OperationManager.history.Count; state.TrashedGroups = Groups(session);
        check(state.Parcel.content.GetComponentInChildren<Node>(true)?.guid.ToString() == state.NodeId,
            "the next native trash parcel retains its original node identity for " + (ReturnCase)state.CaseIndex);
        SetPhase(state, Phase.ParcelStarted);
    }

    private static void RollbackCase(State state, EditorSession session, Action<bool, string> check)
    {
        var guard = OperationManager.history.Count - state.BeforeDropHistory + 10;
        while (OperationManager.history.Count > state.BeforeDropHistory && guard-- > 0) OperationManager.Undo();
        OperationManager.future.Clear(); NativeHistory.Prune(); session.Tick();
        check(OperationManager.history.Count == state.BeforeDropHistory && session.Find(state.NodeId) != null &&
            Near(session.Position(session.Find(state.NodeId)!), state.BeforeDropPosition) && state.Trash.trash.Count == state.InitialTrash &&
            session.Workspace.Snapshot.FindGroupForNode(state.NodeId)?.Id == state.GroupA && Caption(session, state.GroupA).EndsWith(" (1)", StringComparison.Ordinal),
            "native Undo clears the completed trash case without touching prior group or node commands: " + (ReturnCase)state.CaseIndex);
    }

    private static bool Near(Point2 first, Point2 second) => Math.Abs(first.X - second.X) < .01 && Math.Abs(first.Y - second.Y) < .01;

    private static string Caption(EditorSession session, string groupId)
    {
        var root = session.Board.treeRoot.Find("NodeGroup " + groupId);
        return root == null ? "<missing group>" : root.Find("Header/Name (double click)").GetComponent<UILabel>().text;
    }

    private static void Trace(State state, EditorSession session, string phase)
    {
        var node = state.Node;
        var parcel = state.Parcel != null ? state.Parcel : null;
        var top = OperationManager.history.Count == 0 ? null : OperationManager.history.Peek();
        var line = $"{DateTime.UtcNow:O} {phase}; registered={session.Find(state.NodeId) != null}; " +
            $"guid={state.NodeId}; nodePointer={node?.Pointer}; nodeParent={node?.root?.parent?.name}; " +
            $"trash={state.Trash.trash.Count}; parcel={parcel?.Pointer}; outside={parcel?.outside}; " +
            $"history={OperationManager.history.Count}; future={OperationManager.future.Count}; " +
            $"top={top?.GetType().FullName}/{top?.Name}; moveOut={top?.TryCast<NodeMoveOutTrashOperation>() != null}; " +
            $"groupA={Caption(session, state.GroupA)}; owner={session.Workspace.Snapshot.FindGroupForNode(state.NodeId)?.Id}";
        File.AppendAllText(state.TracePath, line + Environment.NewLine); Plugin.Logger.LogInfo(line);
    }

    private static bool Saved(EditorSession session)
    { session.Tick(); return AuthoringEditorSession.Current?.SaveInProgress != true && !session.Dirty; }
    private static void SetPhase(State state, Phase phase)
    { state.Phase = phase; state.Frames = 0; state.Deadline = DateTime.UtcNow.AddSeconds(45); }
    private static void SwitchScene(State state, EditorSession session, string path, Phase phase)
    {
        var data = ProjectFileStore.Read(path).Project;
        session.Dispose(); GroupHost.Session = null;
        PersistentData.projectPath = path; PersistentData.projectData = data;
        SetPhase(state, phase); SceneManager.LoadScene("ScenarioEditorScene");
    }
    private static string GroupStorePath(string path) => global::NodeGroups.Storage.GroupStore.GetSidecarPath(path);
    private static string Groups(EditorSession session) => JsonSerializer.Serialize(session.Workspace.Snapshot.Groups.Select(group => new
    { group.Id, group.Name, group.MemberNodeIds, group.Collapsed, group.Anchor, group.Color, group.ZIndex, group.ExpandedBounds }));
    private static bool NodesEqual(NodeState[] first, NodeState[] second) => first.Length == second.Length && first.All(node => second.Any(other =>
        node.Id == other.Id && Math.Abs(node.Position.X - other.Position.X) < .001 && Math.Abs(node.Position.Y - other.Position.Y) < .001));
    private static string RecoveryPath(string id) => Path.Combine(BepInEx.Paths.GameRootPath, "ModDev", "NodeGroups", "artifacts", "recovery",
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(id))) + ".project.nodegroups.json");
    private static void DeleteOwnedRecovery(State state)
    { if (state.RecoveryPath.Length > 0 && File.Exists(state.RecoveryPath)) File.Delete(state.RecoveryPath); }

    /// <summary>仅释放本独立夹具及其首次确认不存在的恢复文件，不触碰主工程或用户回收站。</summary>
    internal static void Abort(EditorSession? session)
    {
        var state = active; active = null; if (state == null) return;
        try
        {
            state.Pointer?.Dispose(); state.Pointer = null;
            if (session != null && !session.IsDisposed && session.ProjectId == state.FixtureProjectId) session.Dispose();
            DeleteOwnedRecovery(state);
        }
        catch (Exception error) { Plugin.Logger.LogWarning("Trash fixture cleanup: " + error); }
    }

    private static string Story(ProjectData project)
    {
        var payload = ProjectFileStore.Serialize(project); var start = payload.IndexOf('{');
        if (start < 0) throw new InvalidOperationException("Native project has no JSON payload.");
        using var document = JsonDocument.Parse(payload[start..]); using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, document.RootElement);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element, string? property = null)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var value in element.EnumerateObject().OrderBy(value => value.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(value.Name); WriteCanonical(writer, value.Value, value.Name); }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray(); var values = element.EnumerateArray().ToArray();
            if (property == "nodes") values = values.OrderBy(node => node.GetProperty("Guid").GetString(), StringComparer.Ordinal).ToArray();
            foreach (var value in values) WriteCanonical(writer, value); writer.WriteEndArray();
        }
        else element.WriteTo(writer);
    }
}
