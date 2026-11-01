using System.Diagnostics;
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

/// <summary>
/// 正式版运行时压力夹具。所有节点、组和命令都属于报告目录下的独立工程；
/// 原生场景和自然帧推进由真实应用负责，不替换产品刷新/历史/可见性实现。
/// </summary>
internal static class RuntimeStressTests
{
    private const int Seed = 19000501, ExtraNodes = 100, Warmup = 10, SampleCount = 80, Cycles = 120;
    private enum Phase { SaveMain, AwaitStress, CreateNodes, SettleNodes, SettleTier, ExpandedTick,
        ExpandedVisibility, SettleCollapsed, CollapsedVisibility, Exercise, SettleCycles,
        SettleMaximum, MaximumCleanup, NodeCleanup, AwaitMain }

    private sealed record Sample(double Milliseconds, long AllocatedBytes);
    private sealed class Measurement
    {
        internal int Groups;
        internal string Operation = "";
        internal readonly List<Sample> Samples = new(SampleCount);
    }
    private sealed class State
    {
        internal EditorSession Owner = null!;
        internal Phase Phase;
        internal DateTime Deadline;
        internal int Frames, Samples, Tier, Created, CompletedCycles;
        internal readonly System.Random Random = new(Seed);
        internal string MainPath = "", MainProjectId = "", MainStory = "", MainGroups = "", MainHash = "";
        internal byte[] MainCompiled = Array.Empty<byte>(), MainSidecar = Array.Empty<byte>();
        internal bool MainSidecarExisted;
        internal NodeState[] MainNodes = Array.Empty<NodeState>();
        internal string StressPath = "", StressProjectId = "", StressStory = "", MemberGroup = "";
        internal byte[] StressCompiled = Array.Empty<byte>();
        internal NodeState[] StressNodes = Array.Empty<NodeState>();
        internal WorkspaceSnapshot Empty = null!, TierSnapshot = null!;
        internal int NativeHistoryBoundary, NativeCompileDelta, StressCompileDelta;
        internal readonly List<Measurement> Measurements = new();
        internal Measurement Current = null!;
        internal readonly List<object> MaximumMeasurements = new();
        internal string ReportPath = "", RecoveryPath = "";
    }
    private static State? active;

    /// <summary>冻结并保存主夹具；随后在单独工程中执行压力测试，不污染主夹具的撤销/重做分支。</summary>
    internal static void Begin(EditorSession session, Action<bool, string> check)
    {
        var report = Environment.GetEnvironmentVariable("NODEGROUPS_TEST_REPORT");
        if (string.IsNullOrWhiteSpace(report) || active != null)
            throw new InvalidOperationException("Runtime stress requires one explicitly isolated report process.");
        var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report))!, "stress");
        Directory.CreateDirectory(directory);
        var path = Path.GetFullPath(PersistentData.projectPath);
        var reportDirectory = Path.GetDirectoryName(Path.GetFullPath(report))! + Path.DirectorySeparatorChar;
        if (!path.StartsWith(reportDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Stress tests may only save the generated report fixture.");
        active = new State
        {
            Owner = session, MainPath = path, MainProjectId = session.ProjectId,
            MainStory = CanonicalStory(session.Editor.CaptureProject()),
            MainCompiled = AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray(),
            MainNodes = session.ReadNodes(), StressPath = Path.Combine(directory, "stress-fixture" + ProjectFileStore.ProjectExtension),
            ReportPath = Path.Combine(directory, "runtime-stress.json")
        };
        SetPhase(active, Phase.SaveMain);
        check(active.MainCompiled.Length > 0, "release stress starts from a compilable isolated main fixture");
        session.Editor.Save(true);
    }

    /// <summary>每次调用只推进有限工作；测量在自然帧初始化之后进行，setup 与 warmup 不计入样本。</summary>
    internal static bool Tick(EditorSession session, Action<bool, string> check)
    {
        var state = active ?? throw new InvalidOperationException("Runtime stress was not started.");
        if (DateTime.UtcNow > state.Deadline) throw new TimeoutException("Runtime stress timed out in " + state.Phase);
        if (session.IsDisposed) return false;
        state.Frames++;
        if (state.Phase == Phase.SaveMain)
        {
            if (!ReferenceEquals(state.Owner, session) || !Saved(session)) return false;
            state.MainGroups = GroupSignature(session.Workspace.Snapshot.Groups);
            state.MainHash = EditorSession.Hash(state.MainPath);
            var sidecar = global::NodeGroups.Storage.GroupStore.GetSidecarPath(state.MainPath);
            state.MainSidecarExisted = File.Exists(sidecar);
            state.MainSidecar = state.MainSidecarExisted ? File.ReadAllBytes(sidecar) : Array.Empty<byte>();
            check(CanonicalStory(ProjectFileStore.Read(state.MainPath).Project) == state.MainStory,
                "release stress freezes the completed native main save before switching scenes");
            var data = NativeProjectFixture.Build(); state.StressProjectId = data.ProjectId;
            state.RecoveryPath = RecoveryPath(data.ProjectId);
            if (File.Exists(state.StressPath) || File.Exists(state.RecoveryPath))
                throw new InvalidOperationException("Stress fixture/recovery identity already exists.");
            ProjectFileStore.Save(state.StressPath, data, null, false, null);
            SwitchScene(state, session, state.StressPath, Phase.AwaitStress);
            return false;
        }
        if (state.Phase == Phase.AwaitStress)
        {
            if (ReferenceEquals(state.Owner, session) || session.ProjectId != state.StressProjectId) return false;
            state.Owner = session;
            state.StressStory = CanonicalStory(session.Editor.CaptureProject());
            state.StressCompiled = AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray();
            state.StressNodes = session.ReadNodes();
            state.Empty = session.Workspace.Snapshot;
            state.NativeHistoryBoundary = OperationManager.history.Count;
            state.NativeCompileDelta = OperationManager.compileDelta;
            check(state.NativeHistoryBoundary == 0 && OperationManager.future.Count == 0 && state.Empty.Groups.Count == 0,
                "release stress uses a fresh scene with empty native history, redo branch and group metadata");
            SetPhase(state, Phase.CreateNodes);
            return false;
        }
        if (state.Phase == Phase.AwaitMain)
        {
            if (ReferenceEquals(state.Owner, session) || session.ProjectId != state.MainProjectId || state.Frames < 4) return false;
            session.Tick();
            check(CanonicalStory(session.Editor.CaptureProject()) == state.MainStory &&
                AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray().SequenceEqual(state.MainCompiled),
                "release stress returns through native scene loading with the original main graph and compiled bytes");
            check(NodesEqual(session.ReadNodes(), state.MainNodes) && GroupSignature(session.Workspace.Snapshot.Groups) == state.MainGroups,
                "release stress reopening restores original main node positions, group membership and regions");
            var sidecar = global::NodeGroups.Storage.GroupStore.GetSidecarPath(state.MainPath);
            check(EditorSession.Hash(state.MainPath) == state.MainHash && File.Exists(sidecar) == state.MainSidecarExisted &&
                (!state.MainSidecarExisted || File.ReadAllBytes(sidecar).SequenceEqual(state.MainSidecar)),
                "release stress leaves the frozen main project and sidecar file bytes unchanged");
            check(OperationManager.history.Count == 0 && OperationManager.future.Count == 0 &&
                GroupRoots(session) == MainGroupsCount(state),
                "release stress normal scene reload starts fresh history and owns no leftover stress group views");
            DeleteOwnedRecovery(state);
            WriteReport(state, true, null);
            active = null;
            return true;
        }
        if (!ReferenceEquals(state.Owner, session) || session.ProjectId != state.StressProjectId)
            throw new InvalidOperationException("Runtime stress changed owner before its planned scene transition.");

        if (state.Phase == Phase.CreateNodes)
        {
            // 分批创建真实原生节点，避免一次 Update 执行全部初始化造成无意义的长帧。
            for (var i = 0; i < 10 && state.Created < ExtraNodes; i++, state.Created++)
            {
                var bytes = new byte[16]; state.Random.NextBytes(bytes);
                var id = new Guid(bytes).ToString("D");
                var operation = new NodeCreateOperation(Il2CppType.Of<ScriptNode>(), NativeGuid.Parse(id),
                    new Vector2(6000 + state.Created % 20 * 900, -400 - state.Created / 20 * 400));
                operation.Redo(); OperationManager.WriteIsolated(operation);
            }
            if (state.Created == ExtraNodes) SetPhase(state, Phase.SettleNodes);
            return false;
        }
        if (state.Phase == Phase.SettleNodes)
        {
            if (state.Frames < 5) return false;
            session.Tick();
            check(session.Board.nodes.Count == state.StressNodes.Length + ExtraNodes &&
                OperationManager.history.Count == state.NativeHistoryBoundary + ExtraNodes,
                "release stress creates exactly 100 native ScriptNodes through original creation operations");
            state.StressCompileDelta = OperationManager.compileDelta;
            ApplyTier(state, session, 64);
            return false;
        }
        if (state.Phase == Phase.SettleTier)
        {
            if (state.Frames < 5) return false;
            session.Tick();
            check(GroupRoots(session) == state.Tier && ProjectedLines(session) == 0,
                "release stress expanded tier owns exactly " + state.Tier + " group roots and no projected lines");
            BeginMeasurement(state, "expanded-idle-tick", Phase.ExpandedTick);
            return false;
        }
        if (state.Phase == Phase.ExpandedTick || state.Phase == Phase.ExpandedVisibility || state.Phase == Phase.CollapsedVisibility)
        {
            Action action = state.Phase == Phase.ExpandedTick ? session.Tick : session.EnforceVisibility;
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp(); action();
            var elapsed = Stopwatch.GetTimestamp() - start;
            var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            if (state.Samples++ >= Warmup)
                state.Current.Samples.Add(new Sample(elapsed * 1000d / Stopwatch.Frequency, bytes));
            if (state.Current.Samples.Count < SampleCount) return false;
            if (state.Phase == Phase.ExpandedTick)
                BeginMeasurement(state, "expanded-enforce-visibility", Phase.ExpandedVisibility);
            else if (state.Phase == Phase.ExpandedVisibility)
            {
                session.Toggle(state.MemberGroup);
                SetPhase(state, Phase.SettleCollapsed);
            }
            else if (state.Tier == 64)
            {
                session.Toggle(state.MemberGroup);
                // 此轮测量的两个折叠命令用原生 Undo 回退；下一层使用同一场景与节点分布。
                OperationManager.Undo(); OperationManager.Undo();
                ApplyTier(state, session, 260);
            }
            else
            {
                session.Toggle(state.MemberGroup); OperationManager.Undo(); OperationManager.Undo();
                state.TierSnapshot = session.Workspace.Snapshot;
                SetPhase(state, Phase.Exercise);
            }
            return false;
        }
        if (state.Phase == Phase.SettleCollapsed)
        {
            if (state.Frames < 4) return false;
            check(ProjectedLines(session) > 0 && session.Workspace.Snapshot.GetGroup(state.MemberGroup).Collapsed,
                "release stress collapsed tier projects real fixture boundary connections without unregistering nodes");
            BeginMeasurement(state, "collapsed-enforce-visibility", Phase.CollapsedVisibility);
            return false;
        }
        if (state.Phase == Phase.Exercise)
        {
            Exercise(state, session, check);
            if (++state.CompletedCycles == Cycles) SetPhase(state, Phase.SettleCycles);
            return false;
        }
        if (state.Phase == Phase.SettleCycles)
        {
            if (state.Frames < 5) return false;
            session.Tick();
            check(GroupRoots(session) == 260 && ProjectedLines(session) == 0 && TotalProjectedLines(session) == 0,
                "release stress 120 native command cycles leave no extra group roots or retained projected-line objects");
            check(GroupSignature(session.Workspace.Snapshot.Groups) == GroupSignature(state.TierSnapshot.Groups),
                "release stress 120 cycles restore the exact initial membership, regions, names and collapse state");
            var privateBefore = Process.GetCurrentProcess().PrivateMemorySize64;
            var managedBefore = GC.GetTotalMemory(false);
            var groups = Enumerable.Range(0, 1024).Select(index => EmptyGroup(index)).ToArray();
            var start = Stopwatch.GetTimestamp();
            session.Workspace.Restore(new WorkspaceSnapshot(session.ReadNodes(), groups)); session.Tick();
            state.MaximumMeasurements.Add(new { operation = "create-1024-empty-group-views", groups = 1024,
                elapsedMs = (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency,
                privateBytesBefore = privateBefore, privateBytesAfter = Process.GetCurrentProcess().PrivateMemorySize64,
                managedBytesBefore = managedBefore, managedBytesAfter = GC.GetTotalMemory(false) });
            SetPhase(state, Phase.SettleMaximum);
            return false;
        }
        if (state.Phase == Phase.SettleMaximum)
        {
            if (state.Frames < 5) return false;
            check(GroupRoots(session) == 1024 && session.Workspace.Snapshot.Groups.Count == 1024,
                "release stress renders the 1024-group persisted metadata boundary as real native UI views");
            var toolbar = session.Board.treeRoot.parent.Find("NodeGroups toolbar")!;
            var groupDepth = Enumerable.Range(0, session.Board.treeRoot.childCount)
                .Select(index => session.Board.treeRoot.GetChild(index))
                .Where(child => child.name.StartsWith("NodeGroup ", StringComparison.Ordinal))
                .SelectMany(child => child.GetComponentsInChildren<UIWidget>(true)).Max(widget => widget.depth);
            check(toolbar.GetComponentsInChildren<UIWidget>(true).Min(widget => widget.depth) > groupDepth,
                "release stress creation entry remains above all 1024 group layers");
            var before = GC.GetAllocatedBytesForCurrentThread(); var start = Stopwatch.GetTimestamp(); session.Tick();
            state.MaximumMeasurements.Add(new { operation = "idle-tick-1024-empty-groups", groups = 1024,
                elapsedMs = (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency,
                allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before });
            // 将已完成的基准先落盘，即使随后边界用例变红也保留优化前可比较的样本。
            WriteReport(state, false, "Performance samples are complete; final admission and cleanup checks are still pending.");
            CheckAdmissionPolicies(session, check);
            CheckGeometryPolicies(session, check);
            CheckCollapsedDropGeometry(session, check);
            var beforeLimit = session.Workspace.Snapshot; var history = OperationManager.history.Count;
            var selected = session.SelectedGroup; var story = CanonicalStory(session.Editor.CaptureProject());
            session.CreateGroup(true);
            check(session.Workspace.Snapshot.Groups.Count == 1024 && ReferenceEquals(beforeLimit, session.Workspace.Snapshot) &&
                OperationManager.history.Count == history && session.SelectedGroup == selected &&
                CanonicalStory(session.Editor.CaptureProject()) == story,
                "release stress rejects a 1025th group before changing metadata, selection, native graph or history");
            session.Workspace.Restore(new WorkspaceSnapshot(session.ReadNodes(), Array.Empty<GroupState>())); session.Tick();
            SetPhase(state, Phase.MaximumCleanup);
            return false;
        }
        if (state.Phase == Phase.MaximumCleanup)
        {
            if (state.Frames < 5) return false;
            check(GroupRoots(session) == 0 && ProjectedLines(session) == 0 && TotalProjectedLines(session) == 0,
                "release stress maximum-tier rollback releases all stress views and projected lines after natural frames");
            SetPhase(state, Phase.NodeCleanup);
            return false;
        }
        if (state.Phase == Phase.NodeCleanup)
        {
            RollbackNodes(state, session);
            check(NodesEqual(session.ReadNodes(), state.StressNodes) &&
                CanonicalStory(session.Editor.CaptureProject()) == state.StressStory &&
                AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray().SequenceEqual(state.StressCompiled),
                "release stress original native Undo removes every temporary node and restores the exact stress graph and compile bytes");
            check(OperationManager.history.Count == state.NativeHistoryBoundary && OperationManager.future.Count == 0 &&
                OperationManager.compileDelta == state.NativeCompileDelta,
                "release stress cleanup restores its fresh native history and compile-change boundary");
            session.Save();
            if (!Saved(session)) return false;
            SwitchScene(state, session, state.MainPath, Phase.AwaitMain);
            return false;
        }
        return false;
    }

    private static void ApplyTier(State state, EditorSession session, int count)
    {
        state.Tier = count;
        var nodes = session.ReadNodes();
        var originalIds = state.StressNodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var connected = session.Board.nodes.ToArray().First(node => originalIds.Contains(EditorSession.Id(node)) && node.TryCast<ScriptNode>() != null);
        var members = nodes.Where(node => !originalIds.Contains(node.Id)).Select(node => node.Id).Append(EditorSession.Id(connected));
        var region = new Rect2(-2000, -6000, 32000, 10000);
        var groups = new[] { new GroupState(GroupId(0), "压力成员组", members, false,
            new Point2(region.X, region.Y + region.Height), zIndex: 1, expandedBounds: region) }
            .Concat(Enumerable.Range(1, count - 1).Select(EmptyGroup));
        session.Workspace.Restore(new WorkspaceSnapshot(nodes, groups)); session.Tick();
        state.MemberGroup = GroupId(0);
        SetPhase(state, Phase.SettleTier);
    }

    private static string GroupId(int index) => new Guid(index + 1, 0x5752, 0x5354,
        new byte[] { 0xAA, 0x10, 0x01, 0x00, 0x00, 0x00, 0x00, 0x01 }).ToString("D");
    private static GroupState EmptyGroup(int index) => new(GroupId(index), "压力空组 " + index,
        Array.Empty<string>(), false, new Point2(40000 + index % 64 * 1000, -10000 - index / 64 * 500),
        zIndex: index + 2, expandedBounds: new Rect2(40000 + index % 64 * 1000, -10340 - index / 64 * 500, 720, 340));

    private static void BeginMeasurement(State state, string operation, Phase phase)
    {
        state.Current = new Measurement { Groups = state.Tier, Operation = operation };
        state.Measurements.Add(state.Current); state.Samples = 0;
        SetPhase(state, phase);
    }

    private static void Exercise(State state, EditorSession session, Action<bool, string> check)
    {
        var history = OperationManager.history.Count;
        session.Toggle(state.MemberGroup); OperationManager.Undo(); OperationManager.Redo(); session.Toggle(state.MemberGroup);
        OperationManager.Undo(); OperationManager.Undo();
        session.Commit(session.Workspace.MoveGroup(state.MemberGroup, new Point2(.25, -.25)));
        OperationManager.Undo(); OperationManager.Redo(); OperationManager.Undo();
        session.CreateGroup(true); var id = session.SelectedGroup!; session.Dissolve(id);
        OperationManager.Undo(); OperationManager.Undo();
        if (state.CompletedCycles % 20 == 0 || state.CompletedCycles == Cycles - 1)
            check(OperationManager.history.Count == history && OperationManager.compileDelta == state.StressCompileDelta &&
                session.Board.nodes.Count == state.StressNodes.Length + ExtraNodes,
                "release stress atomic native collapse/move/create/dissolve UndoRedo cycle " + (state.CompletedCycles + 1));
    }

    /// <summary>
    /// 直接验证数量政策，利用 Core 为原生撤销保留的缺失 GUID 建立大成员快照。
    /// 不创建五万个 Unity 节点，不刷新这些暂存元数据，也不把它们写入侧车。
    /// </summary>
    private static void CheckAdmissionPolicies(EditorSession session, Action<bool, string> check)
    {
        var original = session.Workspace.Snapshot;
        var history = OperationManager.history.Count; var future = OperationManager.future.Count;
        var selected = session.SelectedGroup; var dirty = session.Dirty;
        var story = CanonicalStory(session.Editor.CaptureProject());
        var incoming = EditorSession.Id(session.Board.nodes.ToArray().First(node => node.TryCast<ScriptNode>() != null));
        var members = Enumerable.Range(0, 50000).Select(index => GroupId(500000 + index)).ToArray();
        GroupState FixtureGroup(int index, IEnumerable<string> ids) => new(GroupId(2000 + index),
            "只读准入边界 " + index, ids, false, default, zIndex: index,
            expandedBounds: new Rect2(-400, -400, 720, 400));
        void Expect(bool expected, string[] ids, string? target, string name, bool creating = false)
        {
            var before = session.Workspace.Snapshot;
            var allowed = session.CanAssignNodes(ids, target, creating);
            check(allowed == expected && ReferenceEquals(before, session.Workspace.Snapshot), name);
        }
        try
        {
            var full = FixtureGroup(0, members.Take(10000));
            session.Workspace.Restore(new WorkspaceSnapshot(original.Nodes, new[] { full }));
            Expect(false, new[] { incoming }, full.Id,
                "release admission refuses a new member in a group already holding 10000 retained identities");
            Expect(true, new[] { members[0], members[0] }, full.Id,
                "release admission permits existing target members and deduplicates repeated identities at 10000");
            Expect(true, new[] { members[0] }, null,
                "release admission always permits moving a member outside a full group");

            var groups = Enumerable.Range(0, 5).Select(index => FixtureGroup(index, members.Skip(index * 10000).Take(10000))).ToArray();
            var empty = FixtureGroup(5, Array.Empty<string>());
            session.Workspace.Restore(new WorkspaceSnapshot(original.Nodes, groups.Append(empty)));
            Expect(false, new[] { incoming }, empty.Id,
                "release admission refuses a previously unowned member at the 50000 total identity limit");
            Expect(true, new[] { members[0] }, empty.Id,
                "release admission permits transfer between groups at 50000 because ownership total does not grow");
            Expect(true, new[] { members[0], members[0] }, empty.Id,
                "release admission repeated transfer input does not consume additional total capacity");
            Expect(true, new[] { members[0] }, null,
                "release admission permits transferring outside all groups at the total limit");
            Expect(false, new[] { incoming }, null,
                "release admission rejects creating a new group with an unowned member at the total limit", creating: true);
            Expect(false, members.Take(10001).ToArray(), null,
                "release admission rejects a new group with more than 10000 selected retained identities", creating: true);
            Expect(true, members.Take(10000).ToArray(), null,
                "release admission permits regrouping exactly 10000 already owned identities without increasing the total", creating: true);
            check(OperationManager.history.Count == history && OperationManager.future.Count == future &&
                session.SelectedGroup == selected && session.Dirty == dirty && CanonicalStory(session.Editor.CaptureProject()) == story,
                "release admission policy leaves native graph, history, redo, selection and dirty state untouched");
        }
        finally
        {
            // 只恢复 Core 快照；中间从未 Tick，不会产生五万成员对应的任何真实界面或原生命令。
            session.Workspace.Restore(original);
        }
    }

    /// <summary>持久化坐标边界的纯计算回归，包含精确边界、相邻浮点数和远端边界溢出。</summary>
    private static void CheckGeometryPolicies(EditorSession session, Action<bool, string> check)
    {
        const double limit = 10000000;
        var before = session.Workspace.Snapshot; var history = OperationManager.history.Count;
        check(EditorSession.CanUseGeometry(new Point2(-limit, limit), null),
            "release geometry accepts anchors exactly at both persisted coordinate limits");
        check(EditorSession.CanUseGeometry(default, new Rect2(-limit, -limit, 2 * limit, 2 * limit)),
            "release geometry validates both rectangle corners rather than treating dimensions as absolute coordinates");
        check(!EditorSession.CanUseGeometry(new Point2(Math.BitIncrement(limit), 0), null) &&
            !EditorSession.CanUseGeometry(new Point2(0, Math.BitDecrement(-limit)), null),
            "release geometry rejects one representable step beyond either anchor limit");
        check(!EditorSession.CanUseGeometry(new Point2(double.NaN, 0), null) &&
            !EditorSession.CanUseGeometry(new Point2(0, double.PositiveInfinity), null),
            "release geometry rejects nonfinite anchors before native movement or UI conversion");
        check(!EditorSession.CanUseGeometry(default, new Rect2(0, 0, 0, 1)) &&
            !EditorSession.CanUseGeometry(default, new Rect2(0, 0, 1, -1)),
            "release geometry rejects zero or negative region dimensions");
        check(!EditorSession.CanUseGeometry(default, new Rect2(0, 0, Math.BitIncrement(limit), 1)) &&
            !EditorSession.CanUseGeometry(default, new Rect2(Math.BitDecrement(-limit), 0, 1, 1)),
            "release geometry rejects one representable step beyond the origin or far rectangle edge");
        check(!EditorSession.CanUseGeometry(default, new Rect2(double.MaxValue, 0, double.MaxValue, 1)) &&
            !EditorSession.CanUseGeometry(default, new Rect2(0, 0, double.PositiveInfinity, 1)) &&
            !EditorSession.CanUseGeometry(default, new Rect2(0, 0, 1, double.NaN)),
            "release geometry rejects overflowing far edges and nonfinite region dimensions");
        check(ReferenceEquals(before, session.Workspace.Snapshot) && OperationManager.history.Count == history,
            "release geometry policy is read-only and does not create a partial workspace or native history edit");
    }

    /// <summary>
    /// 使用两个已注册原生节点走普通拖放的真实提交路径。折叠卡片本身合法，
    /// 但排在现有成员下方的新节点会跨越持久化边界，必须原子拒绝整个手势。
    /// </summary>
    private static void CheckCollapsedDropGeometry(EditorSession session, Action<bool, string> check)
    {
        const double limit = 10000000;
        var original = session.Workspace.Snapshot;
        var selection = Blackboard.selected.ToArray();
        var pair = session.Board.nodes.ToArray().Where(node => node.TryCast<ScriptNode>() != null).TakeLast(2).ToArray();
        var member = pair[0]; var incoming = pair[1];
        var memberPosition = session.Position(member); var incomingPosition = session.Position(incoming);
        var memberExpected = member.expectedDragPos; var incomingExpected = incoming.expectedDragPos;
        void MoveTo(Node node, Point2 position)
        {
            var delta = position - session.Position(node);
            var canvasDelta = new Vector3((float)delta.X, (float)delta.Y, 0);
            var parent = node.root.parent;
            var localDelta = parent != null && parent.Pointer == session.Board.treeRoot.Pointer ? canvasDelta :
                parent == null ? session.Board.treeRoot.TransformVector(canvasDelta) :
                parent.InverseTransformVector(session.Board.treeRoot.TransformVector(canvasDelta));
            node.expectedDragPos = node.root.localPosition; node.Move(localDelta, false, true);
        }
        NodeState Geometry(Node node)
        {
            // 与产品排布相同地读取全部原生控件；不伪造节点尺寸或替换排布方法。
            var points = node.GetComponentsInChildren<UIWidget>(true).SelectMany(widget => widget.worldCorners.ToArray())
                .Select(corner => session.Board.treeRoot.InverseTransformPoint(corner)).ToArray();
            var left = points.Min(point => point.x); var right = points.Max(point => point.x);
            var bottom = points.Min(point => point.y); var top = points.Max(point => point.y);
            return new NodeState(EditorSession.Id(node), new Point2((left + right) / 2, (bottom + top) / 2),
                new Point2(right - left, top - bottom));
        }
        try
        {
            Blackboard.selected.Clear();
            MoveTo(member, default);
            MoveTo(member, new Point2(0, -limit + 48 - Geometry(member).Bounds.Y));
            var group = new GroupState(GroupId(4000), "折叠拖入坐标边界", new[] { EditorSession.Id(member) }, true,
                new Point2(-2000, -limit + 2049), expandedBounds: new Rect2(-2000, -limit + 1, 4000, 2048));
            session.Workspace.Restore(new WorkspaceSnapshot(session.ReadNodes(), new[] { group }));
            Blackboard.selected.Add(incoming);
            session.BeginNodeDrag(incoming);
            var before = session.Workspace.Snapshot; var nodes = session.ReadNodes();
            var history = OperationManager.history.Count; var future = OperationManager.future.Count;
            var dirty = session.Dirty; var selectedGroup = session.SelectedGroup;
            var story = CanonicalStory(session.Editor.CaptureProject());
            var card = session.DropBounds(group);
            MoveTo(incoming, new Point2(card.X + card.Width / 2, card.Y + card.Height / 2));
            session.PreviewNodeDrag();
            var memberGeometry = Geometry(member); var incomingGeometry = Geometry(incoming);
            var placement = NodePlacement.FindTranslationToFreeSpace(new[] { memberGeometry }, new[] { incomingGeometry },
                new Point2(group.Anchor.X + 120, group.Anchor.Y - 100), EditorSession.Id(incoming));
            check(session.DropTarget == group.Id && EditorSession.CanUseGeometry(group.Anchor, group.ExpandedBounds) &&
                incomingGeometry.Bounds.Y + placement.Y - 36 < -limit,
                "release collapsed-drop fixture hits a legal card whose actual native placement crosses the persisted lower edge");
            session.EndNodeDrag();
            check(NodesEqual(session.ReadNodes(), nodes) && GroupSignature(session.Workspace.Snapshot.Groups) == GroupSignature(before.Groups) &&
                OperationManager.history.Count == history && OperationManager.future.Count == future && session.Dirty == dirty &&
                session.SelectedGroup == selectedGroup && !NativeHistory.SuppressNativeMovement &&
                CanonicalStory(session.Editor.CaptureProject()) == story,
                "release collapsed-drop rejects out-of-range placement and restores native nodes, membership, history, redo and dirty state");
        }
        finally
        {
            session.CancelDrag(); session.EndNodeDrag();
            MoveTo(member, memberPosition); MoveTo(incoming, incomingPosition);
            member.expectedDragPos = memberExpected; incoming.expectedDragPos = incomingExpected;
            session.Workspace.Restore(original);
            Blackboard.selected.Clear(); foreach (var node in selection) Blackboard.selected.Add(node);
        }
    }

    private static void RollbackNodes(State state, EditorSession session)
    {
        session.CancelDrag();
        session.Workspace.Restore(new WorkspaceSnapshot(session.ReadNodes(), Array.Empty<GroupState>())); session.Tick();
        var guard = Math.Max(0, OperationManager.history.Count - state.NativeHistoryBoundary) + 8;
        while (OperationManager.history.Count > state.NativeHistoryBoundary && guard-- > 0) OperationManager.Undo();
        // 新鲜压力场景进入时没有 Redo；这里只清理该场景自己撤销出来的测试命令。
        OperationManager.future.Clear(); NativeHistory.Prune(); session.Sync(); session.EnforceVisibility();
    }

    /// <summary>失败只清理本压力场景；错误日志不得覆盖最初断言，外层仍负责安装与 Profile 还原。</summary>
    internal static void Abort(EditorSession? session)
    {
        var state = active; if (state == null) return;
        try
        {
            if (session != null && !session.IsDisposed && session.ProjectId == state.StressProjectId)
            {
                try { RollbackNodes(state, session); } catch (Exception error) { Plugin.Logger.LogWarning("Stress rollback: " + error); }
                session.Dispose();
            }
            DeleteOwnedRecovery(state);
            WriteReport(state, false, "Stress was aborted; inspect the enclosing runtime report for the original failure.");
        }
        catch (Exception error) { Plugin.Logger.LogWarning("Stress cleanup: " + error); }
        finally { active = null; }
    }

    private static bool Saved(EditorSession session)
    {
        session.Tick();
        return AuthoringEditorSession.Current is { SaveInProgress: false } authoring &&
            authoring.ProjectId == session.ProjectId && !session.Dirty;
    }
    private static void SwitchScene(State state, EditorSession session, string path, Phase phase)
    {
        var data = ProjectFileStore.Read(path).Project;
        session.Dispose(); GroupHost.Session = null;
        PersistentData.projectPath = path; PersistentData.projectData = data;
        SetPhase(state, phase); SceneManager.LoadScene("ScenarioEditorScene");
    }
    private static void SetPhase(State state, Phase phase)
    { state.Phase = phase; state.Frames = 0; state.Deadline = DateTime.UtcNow.AddSeconds(90); }

    private static int GroupRoots(EditorSession session)
    {
        var count = 0; var root = session.Board.treeRoot;
        for (var index = 0; index < root.childCount; index++)
        {
            var child = root.GetChild(index);
            if (child.gameObject.activeInHierarchy && child.name.StartsWith("NodeGroup ", StringComparison.Ordinal)) count++;
        }
        return count;
    }
    private static int ProjectedLines(EditorSession session) => session.Board.treeRoot.GetComponentsInChildren<Line>(true)
        .Count(line => line.gameObject.activeInHierarchy && line.name.StartsWith("Read-only projected connection ", StringComparison.Ordinal));
    private static int TotalProjectedLines(EditorSession session) => session.Board.treeRoot.GetComponentsInChildren<Line>(true)
        .Count(line => line.name.StartsWith("Read-only projected connection ", StringComparison.Ordinal));
    private static bool NodesEqual(NodeState[] a, NodeState[] b) => a.Length == b.Length && a.All(node => b.Any(other =>
        node.Id == other.Id && Math.Abs(node.Position.X - other.Position.X) < .001 && Math.Abs(node.Position.Y - other.Position.Y) < .001));
    private static string GroupSignature(IEnumerable<GroupState> groups) => JsonSerializer.Serialize(groups.Select(group => new
        { group.Id, group.Name, group.MemberNodeIds, group.Collapsed, group.Anchor, group.Color, group.ZIndex, group.ExpandedBounds }));
    private static int MainGroupsCount(State state)
    { using var document = JsonDocument.Parse(state.MainGroups); return document.RootElement.GetArrayLength(); }
    private static string RecoveryPath(string projectId) => Path.Combine(BepInEx.Paths.GameRootPath, "ModDev", "NodeGroups", "artifacts", "recovery",
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(projectId))) + ".project.nodegroups.json");
    private static void DeleteOwnedRecovery(State state)
    {
        // 此路径由刚生成且已断言不存在的独立工程身份决定，不能指向用户工程的恢复副本。
        if (state.RecoveryPath.Length > 0 && File.Exists(state.RecoveryPath)) File.Delete(state.RecoveryPath);
    }

    private static string CanonicalStory(ProjectData project)
    {
        var serialized = ProjectFileStore.Serialize(project); var start = serialized.IndexOf('{');
        if (start < 0) throw new InvalidOperationException("Native serialization has no JSON payload.");
        using var document = JsonDocument.Parse(serialized[start..]); using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, document.RootElement);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element, string? propertyName = null)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value, property.Name); }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray(); var values = element.EnumerateArray().ToArray();
            if (propertyName == "nodes") values = values.OrderBy(node => node.GetProperty("Guid").GetString(), StringComparer.Ordinal).ToArray();
            foreach (var value in values) WriteCanonical(writer, value);
            writer.WriteEndArray();
        }
        else element.WriteTo(writer);
    }

    private static void WriteReport(State state, bool success, string? error)
    {
        static double Percentile(double[] values, double percentile) => values.Length == 0 ? 0 :
            values.OrderBy(value => value).ElementAt(Math.Clamp((int)Math.Ceiling(values.Length * percentile) - 1, 0, values.Length - 1));
        var metrics = state.Measurements.Select(item => new
        {
            groups = item.Groups, operation = item.Operation, samples = item.Samples.Count,
            p50Milliseconds = Percentile(item.Samples.Select(sample => sample.Milliseconds).ToArray(), .5),
            p95Milliseconds = Percentile(item.Samples.Select(sample => sample.Milliseconds).ToArray(), .95),
            p50AllocatedBytes = Percentile(item.Samples.Select(sample => (double)sample.AllocatedBytes).ToArray(), .5),
            p95AllocatedBytes = Percentile(item.Samples.Select(sample => (double)sample.AllocatedBytes).ToArray(), .95)
        }).ToArray();
        File.WriteAllText(state.ReportPath, JsonSerializer.Serialize(new
        {
            success, error, seed = Seed, additionalNativeNodes = ExtraNodes,
            actualNativeNodes = state.StressNodes.Length + state.Created, warmupPerMetric = Warmup,
            sampleCountPerMetric = SampleCount, completedCycles = state.CompletedCycles,
            uiBoundaryGroups = 1024, measurements = metrics, maximumBoundary = state.MaximumMeasurements,
            unityVersion = Application.unityVersion, process64Bit = Environment.Is64BitProcess,
            note = "Measurements describe this machine and fixture; setup/warmup/reporting are excluded. Managed allocations do not represent all native/GPU memory. Scene reload intentionally uses the native history reset semantics."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
