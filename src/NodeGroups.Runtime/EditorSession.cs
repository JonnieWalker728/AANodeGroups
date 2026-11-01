using System.Collections.Concurrent;
using Il2CppInterop.Runtime;
using NodeGroups.Core;
using NodeGroups.Storage;
using Studio.Scripts;
using Studio.Scripts.Nodes;
using Studio.Scripts.OperationManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AzureArchive.NodeGroups.Runtime;

/// <summary>
/// 分组元数据与原生编辑器之间的适配边界。
/// 原生节点根对象、GUID、连接及场景生命周期由 AzureArchive 管理；组仅维护显示和成员关系。
/// </summary>
internal sealed partial class EditorSession : IDisposable
{
    internal readonly StudioCommon Editor;
    internal readonly Blackboard Board;
    internal GroupWorkspace Workspace { get; private set; }
    internal string ProjectId { get; }
    /// <summary>创建会话的 Unity 主线程 ID；后台保存回调不能读取实时编辑器对象。</summary>
    internal int UnityThreadId { get; } = Environment.CurrentManagedThreadId;
    /// <summary>已提交组内容相对最后成功保存点是否改变；不包含尚可取消的预览。</summary>
    internal bool Dirty => saveState.Dirty;
    private GroupSaveState saveState = new(Array.Empty<GroupState>());
    private readonly Dictionary<string, GroupSaveState> saveStatesByPath = new(StringComparer.OrdinalIgnoreCase);
    private string? saveStatePath;
    private IReadOnlyList<GroupState>? observedGroups;
    private bool? indicatedDirty;
    internal string? SelectedGroup;
    internal string? DropTarget;
    // 菜单预览节点注册前没有 Workspace 身份；仅保留本次手势的对象用于悬浮反馈，不写入侧车。
    private NodeParcel? paletteParcel;
    private Node? palettePreview;
    private IntPtr paletteParcelPointer;
    private bool paletteCancelled;
    private readonly GroupStore store = new();
    private static readonly GroupStoreLimits MetadataLimits = new();
    private readonly GroupUi ui;
    private readonly RecoveryPrompt recoveryPrompt;
    private readonly UnsavedQuitPrompt quitPrompt;
    private readonly VisibilityGuard visibility = new();
    private IReadOnlyList<GroupState>? visibilityGroups;
    private readonly HashSet<string> collapsedMembers = new(StringComparer.Ordinal);
    private DragSession? drag;
    private string? dragPrimary;
    private readonly List<Operation> gestureTrashOperations = new();
    private WorkspaceSnapshot? trashGestureBefore;
    internal bool CancelledNodeGesture { get; private set; }
    private string? movingGroup;
    private WorkspaceSnapshot? groupMoveBefore;
    private WorkspaceSnapshot? resizeBefore;
    private string? resizingGroup;
    private int resizeEdges;
    private Point2 resizePointer;
    internal const int HeaderHeight = 140;
    internal const int CardWidth = 658;
    internal const int MinimumFrameWidth = 720;
    internal const int MinimumFrameHeight = 340;
    private Point2 lastPointer;
    private readonly ConcurrentQueue<SaveReceipt> saves = new();
    private readonly object saveGate = new();
    private long saveSequence;
    private readonly Dictionary<string, long> persistedSequences = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Rect2>? frozenBounds;
    private volatile bool disposed;
    private string? recoveryProjectPath;
    private string? blockedSidecarPath;
    internal bool SidecarLoadBlocked => blockedSidecarPath != null;
    /// <summary>是否存在比当前侧车更新、可由用户主动导入的未保存恢复文件。</summary>
    internal bool RecoveryAvailable { get; private set; }
    /// <summary>本会话的原生恢复确认窗口是否打开，供 UI 与快捷键共同判断输入边界。</summary>
    internal bool RecoveryPromptOpen => recoveryPrompt.IsOpen;
    /// <summary>正常关闭应用的确认是否打开，包含确认后等待退出的短暂阶段。</summary>
    internal bool QuitPromptOpen => quitPrompt.IsOpen;
    internal bool ModalInputBlocked => RecoveryPromptOpen || QuitPromptOpen;
    /// <summary>未完成的画布手势不能被关闭确认打断；释放手势后可再次请求关闭。</summary>
    internal bool QuitPromptInputBlocked => ui.HasActiveCreateGesture || drag != null ||
        paletteParcelPointer != IntPtr.Zero || movingGroup != null || resizingGroup != null;
    /// <summary>保存时冻结的回执；后台回调必须匹配原会话实例，不能仅凭 ProjectId 匹配重开的工程。</summary>
    internal sealed record SaveReceipt(EditorSession Owner, string Path, string ProjectId, WorkspaceSnapshot Snapshot,
        long Sequence, string? SavedHash = null, string? SavedVersion = null);
    internal bool IsDisposed => disposed;
    /// <summary>仅供界面使用的光标反馈，不进入剧情模型或侧车文件。</summary>
    internal GroupCursor Cursor => ui.Cursor;

    internal EditorSession(StudioCommon editor, Blackboard board)
    {
        Editor = editor; Board = board;
        ProjectId = editor.CaptureProject().ProjectId;
        Workspace = new GroupWorkspace(ReadNodes());
        var requiresSave = false;
        try
        {
            var path = PersistentData.projectPath;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                var nodes = ReadNodes();
                var result = store.Load(path, ProjectId, Hash(path), nodes.Select(node => node.Id));
                if (result.Status is GroupLoadStatus.Invalid or GroupLoadStatus.IoError)
                    blockedSidecarPath = Path.GetFullPath(GroupStore.GetSidecarPath(path));
                if (result.Snapshot != null)
                {
                    Workspace = new GroupWorkspace(nodes, RecoveryGroups(result.Snapshot.Groups, nodes, result.RequiresLayoutRecalculation));
                    requiresSave = result.Status == GroupLoadStatus.Recovered;
                }
                if (result.Diagnostics.Count > 0) Plugin.Logger.LogWarning("节点组读取诊断：" + string.Join("；", result.Diagnostics.Select(d => d.Message)));
            }
            recoveryProjectPath = RecoveryProjectPath(ProjectId);
            var recoveryFile = GroupStore.GetSidecarPath(recoveryProjectPath);
            var currentSidecar = string.IsNullOrWhiteSpace(path) ? null : GroupStore.GetSidecarPath(path);
            // 成功保存侧车后，时间更早的恢复文件不再代表未保存修改。
            RecoveryAvailable = File.Exists(recoveryFile) && (currentSidecar == null || !File.Exists(currentSidecar) ||
                File.GetLastWriteTimeUtc(recoveryFile) > File.GetLastWriteTimeUtc(currentSidecar));
        }
        catch (Exception error)
        {
            if (!string.IsNullOrWhiteSpace(PersistentData.projectPath))
                blockedSidecarPath = Path.GetFullPath(GroupStore.GetSidecarPath(PersistentData.projectPath));
            // 分组文件读取失败不能阻止原生工程打开。
            Plugin.Logger.LogError("节点组元数据读取失败；原工程继续打开。");
            Plugin.Logger.LogError(error);
        }
        saveState = new GroupSaveState(Workspace.Snapshot.Groups, requiresSave);
        saveStatePath = ActiveSidecarPath();
        if (saveStatePath != null) saveStatesByPath[saveStatePath] = saveState;
        observedGroups = Workspace.Snapshot.Groups;
        recoveryPrompt = new RecoveryPrompt(this);
        ui = new GroupUi(this);
        quitPrompt = new UnsavedQuitPrompt(this);
        try
        {
            Tick();
            Plugin.Logger.LogInfo($"Editor groups attached: {ProjectId}, {Workspace.Snapshot.Groups.Count} groups.");
            // 最后订阅：构造失败的会话尚未交给 GroupHost，不能遗留持有它的全局关闭委托。
            quitPrompt.Attach();
        }
        catch
        {
            TryCleanup("incomplete quit prompt", quitPrompt.Dispose);
            TryCleanup("incomplete recovery prompt", recoveryPrompt.Dispose);
            TryCleanup("incomplete visibility", visibility.Dispose);
            TryCleanup("incomplete group UI", ui.Dispose);
            throw;
        }
    }

    internal static string Hash(string path) => GroupStore.ComputeCoreSnapshotHash(File.ReadAllBytes(path));
    internal Node? Find(string id) => Board.FindNodeByGuid(NativeGuid.Parse(id));
    internal static string Id(Node node) => node.guid.ToString();
    /// <summary>读取节点在画布中的坐标，避免经世界坐标往返转换导致浮点精度损失。</summary>
    /// <remarks>TreeRoot 直接子节点的 localPosition 就是原生坐标（含小数）；
    /// 只有其他父级下的节点才需要转换。画布平移不应改变撤销快照中的节点坐标。</remarks>
    internal Point2 Position(Node node)
    {
        var local = node.root.parent != null && node.root.parent.Pointer == Board.treeRoot.Pointer
            ? node.root.localPosition : Board.treeRoot.InverseTransformPoint(node.root.position);
        return new(local.x, local.y);
    }

    /// <summary>在 Unity 主线程读取已注册节点；菜单中尚未注册的预览节点不进入快照。</summary>
    internal NodeState[] ReadNodes() => Board.nodes.ToArray().Where(node => node != null && node.root != null)
        .Select(node => new NodeState(Id(node), Position(node), new Point2(280, 110))).ToArray();

    /// <summary>主线程刷新：同步原生状态、处理保存回执、更新界面并清理失效历史。</summary>
    internal void Tick()
    {
        if (disposed) return;
        quitPrompt.Tick();
        PollPaletteDrag();
        EditorPatches.PollGuiSaves(this);
        if (drag == null && movingGroup == null && resizingGroup == null && !NativeHistory.Applying && !OperationManager.Working)
        { Sync(); CompleteTrashedGesture(); }
        while (saves.TryDequeue(out var receipt))
        {
            if (!ReferenceEquals(receipt.Owner, this) || receipt.ProjectId != ProjectId) continue;
            try
            {
                var destination = Path.GetFullPath(GroupStore.GetSidecarPath(receipt.Path));
                if (persistedSequences.TryGetValue(destination, out var latest) && receipt.Sequence <= latest) continue;
                // 在哈希核对前后检查同一原生版本。侧车使用回执冻结的哈希，
                // 不能把旧分组回执重新绑定到后来写入的剧情文件。
                if (string.IsNullOrEmpty(receipt.SavedHash) || string.IsNullOrEmpty(receipt.SavedVersion) ||
                    !File.Exists(receipt.Path) ||
                    !string.Equals(AzureArchive.Automation.ProjectFileStore.Version(receipt.Path), receipt.SavedVersion, StringComparison.Ordinal) ||
                    !string.Equals(Hash(receipt.Path), receipt.SavedHash, StringComparison.Ordinal) ||
                    !string.Equals(AzureArchive.Automation.ProjectFileStore.Version(receipt.Path), receipt.SavedVersion, StringComparison.Ordinal))
                {
                    SaveStateForPath(destination).RequireSave();
                    Plugin.Logger.LogWarning("原工程已有较新的保存版本，已跳过旧节点组回执；请重新保存。");
                    continue;
                }
                SaveGroups(receipt.Path, receipt.Snapshot, receipt.SavedHash, receipt.Sequence);
            }
            catch (Exception error)
            {
                RequireSaveForProject(receipt.Path);
                Plugin.Logger.LogError("节点组保存版本验证失败。");
                Plugin.Logger.LogError(error);
            }
        }
        ui.Refresh();
        NativeHistory.Prune();
        RefreshSaveIndicator();
        recoveryPrompt.Tick(!ui.HasActiveCreateGesture && drag == null && paletteParcelPointer == IntPtr.Zero && movingGroup == null && resizingGroup == null &&
            !UICamera.inputHasFocus && !UIPopupList.isOpen && (Editor.exitPopup == null || !Editor.exitPopup.activeInHierarchy));
    }

    /// <summary>仅在提交、撤销/重做或空闲同步完成后发布组内容；快照集合不变时不重复比较。</summary>
    private void ObserveCommittedGroups()
    {
        ActivateSaveState();
        var groups = Workspace.Snapshot.Groups;
        if (!ReferenceEquals(observedGroups, groups))
        {
            saveState.ObserveCommitted(groups);
            observedGroups = groups;
        }
        RefreshSaveIndicator();
    }

    private static string? ActiveSidecarPath() => string.IsNullOrWhiteSpace(PersistentData.projectPath) ? null :
        Path.GetFullPath(GroupStore.GetSidecarPath(PersistentData.projectPath));

    /// <summary>同一工程身份的另存、备份和导出路径各有保存点，旧路径回执不能改变活动工程的提示。</summary>
    private GroupSaveState SaveStateForPath(string sidecar)
    {
        if (!saveStatesByPath.TryGetValue(sidecar, out var state))
        {
            state = new GroupSaveState(Array.Empty<GroupState>(), requiresSave: true);
            state.ObserveCommitted(observedGroups ?? Workspace.Snapshot.Groups);
            saveStatesByPath.Add(sidecar, state);
        }
        return state;
    }

    /// <summary>原生另存为可能在保存回执之后才更新活动路径；激活该路径已经确认的保存点。</summary>
    private void ActivateSaveState()
    {
        // 原生切换工程可能先更新全局路径再销毁旧编辑器；旧 owner 清理时不能激活新工程的保存点。
        if (disposed || PersistentData.projectData != null && PersistentData.projectData.ProjectId != ProjectId) return;
        var path = ActiveSidecarPath();
        if (string.Equals(path, saveStatePath, StringComparison.OrdinalIgnoreCase)) return;
        saveState = path == null ? new GroupSaveState(Array.Empty<GroupState>(), requiresSave: true) : SaveStateForPath(path);
        saveStatePath = path;
        saveState.ObserveCommitted(observedGroups ?? Workspace.Snapshot.Groups);
        indicatedDirty = null;
    }

    /// <summary>退出和原生提示读取前立即核对活动保存路径，不等待下一次轮询，也不发布拖动预览。</summary>
    internal void EnsureActiveSaveState() => ActivateSaveState();

    private void RequireSaveForProject(string path)
    {
        try { SaveStateForPath(Path.GetFullPath(GroupStore.GetSidecarPath(path))).RequireSave(); }
        catch (ArgumentException) { saveState.RequireSave(); }
    }

    private void RefreshSaveIndicator()
    {
        if (indicatedDirty != Dirty && NativeSaveState.Refresh(this)) indicatedDirty = Dirty;
    }

    /// <summary>保存只使用最后已提交的组内容；自动保存不能把仍可取消的缩放或移动预览写入侧车。</summary>
    /// <remarks>节点几何仍来自当前原生适配快照，且不进入侧车；本方法仅复制不可变托管值，不访问 Unity。</remarks>
    private WorkspaceSnapshot SnapshotForSave()
    {
        var snapshot = Workspace.Snapshot;
        var groups = observedGroups ?? snapshot.Groups;
        return ReferenceEquals(groups, snapshot.Groups) ? snapshot : new WorkspaceSnapshot(snapshot.Nodes, groups, snapshot.Revision);
    }

    /// <summary>保存开始时冻结托管快照及顺序号，供后台完成回调安全入队。</summary>
    internal SaveReceipt FreezeSave(string path)
    {
        lock (saveGate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(EditorSession));
            return new(this, path, ProjectId, SnapshotForSave(), Interlocked.Increment(ref saveSequence));
        }
    }
    /// <summary>
    /// 线程安全的原生保存回执入口。完成钩子应先校验哈希与版本；
    /// 未知版本只标记为未确认，不读取当前文件来补造成功回执。
    /// </summary>
    internal void Saved(SaveReceipt receipt)
    {
        lock (saveGate)
        {
            if (disposed || !ReferenceEquals(receipt.Owner, this) || receipt.ProjectId != ProjectId) return;
            // 失败回执也保留顺序号，避免旧 GUI 请求的迟到失败把较新的成功保存重新标脏。
            saves.Enqueue(receipt);
        }
    }

    /// <summary>
    /// 由用户主动导入未保存的分组。使用当前原生节点身份和坐标，
    /// 不从恢复文件覆盖原生节点位置、连接或剧情内容。
    /// </summary>
    internal bool RestoreRecovery()
    {
        if (disposed || !RecoveryAvailable || recoveryProjectPath == null) return false;
        try
        {
            CancelDrag();
            var nodes = ReadNodes();
            var path = PersistentData.projectPath;
            var hash = !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? Hash(path) : new string('0', 64);
            var result = store.Load(recoveryProjectPath, ProjectId, hash, nodes.Select(node => node.Id));
            if (result.Snapshot == null)
            {
                Plugin.Logger.LogWarning("节点组恢复未成功；恢复文件已保留：" + string.Join("；", result.Diagnostics.Select(d => d.Message)));
                return false;
            }
            Workspace.ReconcileNodes(nodes);
            var before = Workspace.Snapshot;
            var after = new WorkspaceSnapshot(nodes,
                RecoveryGroups(result.Snapshot.Groups, nodes, result.RequiresLayoutRecalculation), before.Revision + 1);
            Workspace.Restore(after);
            Commit(new WorkspaceChange("恢复未保存分组", before, after));
            EnforceVisibility();
            RecoveryAvailable = false;
            Plugin.Logger.LogInfo("未保存分组已恢复，请保存工程；原生节点位置保持当前版本。");
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError("分组恢复失败；恢复文件已保留。");
            Plugin.Logger.LogError(error);
            return false;
        }
    }

    private static IEnumerable<GroupState> RecoveryGroups(IEnumerable<GroupState> groups, NodeState[] nodes, bool recalculate)
    {
        var nodeIndex = nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var members = group.MemberNodeIds.Where(nodeIndex.ContainsKey).Select(id => nodeIndex[id]).ToArray();
            var candidate = recalculate && members.Length > 0
                ? new Point2(members.Min(node => node.Position.X) - 36, members.Max(node => node.Position.Y) + 80)
                : group.Anchor;
            // 原生工程可由其他工具移至更远处；恢复只能导入合法元数据，不能制造无法再次保存的锚点。
            var anchor = CanUseGeometry(candidate, null) ? candidate : group.Anchor;
            // 保存的区域只描述组边框；恢复时保留区域，再按当前成员显示范围按需扩大。
            yield return new GroupState(group.Id, group.Name, group.MemberNodeIds, group.Collapsed, anchor,
                group.Color, group.ZIndex, group.ExpandedBounds);
        }
    }

    private static string RecoveryProjectPath(string projectId)
    {
        var directory = Path.Combine(BepInEx.Paths.GameRootPath, "ModDev", "NodeGroups", "artifacts", "recovery");
        var identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(projectId)));
        return Path.Combine(directory, identity + ".project");
    }

    private void SaveGroups(string path, WorkspaceSnapshot snapshot, string? confirmedHash = null, long sequence = 0)
    {
        try
        {
            var destination = Path.GetFullPath(GroupStore.GetSidecarPath(path));
            ActivateSaveState();
            var targetState = SaveStateForPath(destination);
            // 多次仅修改分组时，剧情版本可能相同；还需比较会话内顺序号，
            // 防止迟到回调覆盖同一剧情版本下较新的分组信息。
            if (sequence == 0) sequence = Interlocked.Increment(ref saveSequence);
            if (persistedSequences.TryGetValue(destination, out var latest) && sequence <= latest) return;
            if (string.Equals(blockedSidecarPath, destination, StringComparison.OrdinalIgnoreCase))
            {
                targetState.RequireSave();
                Plugin.Logger.LogWarning("组文件无效或不可读，已保留；请先备份并修复/移走它。");
                return;
            }
            // 原生回执必须提供已验证的哈希。只有主动保存当前分组时才可读取现文件计算哈希。
            var result = store.Save(path, new GroupFileSnapshot(ProjectId, confirmedHash ?? Hash(path), snapshot.Groups));
            if (result.Success)
            {
                persistedSequences[destination] = sequence;
                targetState.ObserveCommitted(observedGroups ?? Workspace.Snapshot.Groups);
                targetState.AcceptSaved(snapshot.Groups);
                Plugin.Logger.LogDebug(Dirty ? "此次分组保存完成，仍有较新的修改未保存。" : "节点组已保存。");
            }
            else { targetState.RequireSave(); Plugin.Logger.LogError("节点组保存失败：" + string.Join("；", result.Diagnostics.Select(d => d.Message))); }
        }
        catch (Exception error) { RequireSaveForProject(path); Plugin.Logger.LogError("节点组保存失败。"); Plugin.Logger.LogError(error); }
        finally { RefreshSaveIndicator(); }
    }

    private static bool SameGroups(WorkspaceSnapshot a, WorkspaceSnapshot b) =>
        a.Groups.Count == b.Groups.Count && a.Groups.Zip(b.Groups).All(pair =>
            pair.First.Id == pair.Second.Id && pair.First.Name == pair.Second.Name &&
            pair.First.Anchor == pair.Second.Anchor && pair.First.Collapsed == pair.Second.Collapsed &&
            pair.First.Color == pair.Second.Color && pair.First.ZIndex == pair.Second.ZIndex &&
            pair.First.ExpandedBounds == pair.Second.ExpandedBounds &&
            pair.First.MemberNodeIds.SequenceEqual(pair.Second.MemberNodeIds));

    /// <summary>保存当前分组；剧情已改动时交回原生保存流程，保留其对话框和错误处理。</summary>
    internal void Save()
    {
        if (drag == null && movingGroup == null && resizingGroup == null) ObserveCommittedGroups();
        var path = PersistentData.projectPath;
        // 原生历史也统计组操作，因此要比较剧情本身，才能判断是否仅修改了分组。
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path) &&
            string.Equals(global::AzureArchive.Automation.ProjectFileStore.Serialize(Editor.CaptureProject()),
                global::AzureArchive.Automation.ProjectFileStore.Serialize(global::AzureArchive.Automation.ProjectFileStore.Read(path).Project), StringComparison.Ordinal))
        {
            SaveGroups(path, SnapshotForSave());
            if (!Dirty) OperationManager.OnSave();
        }
        else if (global::AzureArchive.Automation.AuthoringEditorSession.Current?.SaveInProgress != true)
            Editor.Save(true); // 强制保存覆盖仅有视图修改的情况，仍由原生实现处理交互与错误。
    }

    internal void HandleKeys()
    {
        // 恢复确认由原生模态窗口处理，不能同时执行底层画布的 Mod 快捷键。
        if (ModalInputBlocked) return;
        ui.HandleEdit();
        if (Input.GetKeyDown(KeyCode.Escape)) CancelDrag();
        if (UICamera.inputHasFocus || drag != null || paletteParcelPointer != IntPtr.Zero || movingGroup != null || resizingGroup != null ||
            Node.activeNode != null || UIPopupList.isOpen ||
            (Editor.exitPopup != null && Editor.exitPopup.activeInHierarchy)) return;
        var control = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        if (control && Input.GetKeyDown(KeyCode.G)) CreateGroup(false);
        // 原生历史已处理 Ctrl+S；这里只兜底恢复文件等不在其脏栈中的状态，避免同一按键触发两次保存。
        if (control && Input.GetKeyDown(KeyCode.S))
        {
            EnsureActiveSaveState();
            if (Dirty && !NativeSaveState.OriginalDirty) Save();
        }
    }

    /// <summary>以当前原生选择创建组；没有选择时创建空组，并将创建操作登记到原生历史。</summary>
    /// <param name="empty">内部调用为 true 时忽略当前选择，供隔离测试建立空组夹具。</param>
    internal void CreateGroup(bool empty)
    {
        // 与持久化上限一致；先拒绝，再执行任何同步/选择/历史操作，避免产生不能保存的第 1025 组。
        if (Workspace.Snapshot.Groups.Count >= MetadataLimits.MaxGroups)
        {
            Plugin.Logger.LogWarning($"节点组数量已达 {MetadataLimits.MaxGroups}，请先解散不再需要的组。");
            return;
        }
        CancelDrag();
        Sync();
        var ids = empty ? Array.Empty<string>() : Blackboard.selected.ToArray().Select(Id).ToArray();
        if (!CanAssignNodes(ids, null, creating: true)) return;
        var position = ids.Length > 0 ? Position(Find(ids[0])!) : CanvasCenter();
        var anchor = new Point2(position.X - 36, position.Y + 80);
        var bounds = RequiredBounds(ids) ?? new Rect2(anchor.X, anchor.Y - MinimumFrameHeight, MinimumFrameWidth, MinimumFrameHeight);
        if (!CanUseGeometry(anchor, bounds)) { WarnGeometryLimit(); return; }
        var change = Workspace.CreateGroup(Guid.NewGuid().ToString("D"), "节点组", ids,
            anchor, expandedBounds: bounds);
        SelectedGroup = change.After.Groups.Last().Id;
        Commit(change);
    }

    /// <summary>校验并确认名称编辑；取消未结束的手势后再记录重命名历史。</summary>
    internal void Rename(string id, string name)
    {
        CancelDrag();
        Sync();
        if (string.IsNullOrWhiteSpace(name)) { Plugin.Logger.LogWarning("组名不能为空。"); return; }
        if (name.Trim().Length > 256) { Plugin.Logger.LogWarning("组名最多 256 个字符。"); return; }
        try { Commit(Workspace.RenameGroup(id, name)); }
        catch (ArgumentException error) { Plugin.Logger.LogWarning("组名无效：" + error.Message); }
    }

    /// <summary>在修改成员前检查持久化容量；拒绝时不改变快照、原生节点或历史。</summary>
    /// <param name="nodeIds">本次准备加入的节点标识，重复项只计一次。</param>
    /// <param name="targetGroupId">已有目标组；null 且不创建组时表示移出，不受容量限制。</param>
    /// <param name="creating">是否为这些成员创建一个新组。</param>
    /// <remarks>暂时缺失的成员也占容量，以保证原生撤销恢复后仍可保存；组间转移不增加总数。</remarks>
    internal bool CanAssignNodes(IEnumerable<string> nodeIds, string? targetGroupId, bool creating = false)
    {
        if (!creating && targetGroupId == null) return true;
        var snapshot = Workspace.Snapshot;
        if (creating && snapshot.Groups.Count >= MetadataLimits.MaxGroups) return false;
        var members = creating ? 0 : snapshot.GetGroup(targetGroupId!).MemberNodeIds.Count;
        long total = 0;
        foreach (var group in snapshot.Groups) total += group.MemberNodeIds.Count;
        foreach (var id in nodeIds.Distinct(StringComparer.Ordinal))
        {
            var owner = snapshot.FindGroupForNode(id);
            if (creating || owner?.Id != targetGroupId) members++;
            if (owner == null) total++;
        }
        if (members <= MetadataLimits.MaxMembersPerGroup && total <= MetadataLimits.MaxTotalMembers) return true;
        Plugin.Logger.LogWarning($"节点组成员容量不足：每组最多 {MetadataLimits.MaxMembersPerGroup}，全部组最多 {MetadataLimits.MaxTotalMembers} 个成员。");
        return false;
    }

    /// <summary>组锚点和展开区域的两个对角必须处于可保存的画布范围内。</summary>
    /// <remarks>仅限制 Mod 元数据；不拦截原生节点本身的创建、移动或注册。</remarks>
    internal static bool CanUseGeometry(Point2 anchor, Rect2? bounds)
    {
        var limit = MetadataLimits.MaxAbsoluteCoordinate;
        if (!anchor.IsFinite || Math.Abs(anchor.X) > limit || Math.Abs(anchor.Y) > limit) return false;
        return bounds is not { } region || region.IsValid && region.Width > 0 && region.Height > 0 &&
            Math.Abs(region.X) <= limit && Math.Abs(region.Y) <= limit &&
            Math.Abs(region.X + region.Width) <= limit && Math.Abs(region.Y + region.Height) <= limit;
    }

    private static void WarnGeometryLimit() => Plugin.Logger.LogWarning("节点组区域超过可保存的画布范围，请在 ±10000000 范围内整理分组。");

    private bool HasValidGeometry() => Workspace.Snapshot.Groups.All(group => CanUseGeometry(group.Anchor, group.ExpandedBounds));

    private bool CanFitMembers(string groupId, IEnumerable<string> members) =>
        CanUseGeometry(Workspace.Snapshot.GetGroup(groupId).Anchor, RequiredBounds(members));
    /// <summary>切换折叠状态，并同步原生节点和连线的可见性。</summary>
    internal void Toggle(string id)
    {
        CancelDrag();
        Sync();
        Commit(Workspace.SetCollapsed(id, !Workspace.Snapshot.GetGroup(id).Collapsed));
        EnforceVisibility();
    }

    /// <summary>解散组并恢复成员显示；原生节点和连接继续保留。</summary>
    internal void Dissolve(string id)
    {
        CancelDrag();
        Sync();
        Commit(Workspace.DissolveGroup(id));
        if (SelectedGroup == id) SelectedGroup = null;
        EnforceVisibility();
    }
    /// <summary>空闲时同步节点注册表；手势进行中保留起始快照，避免成员数量和边界随预览跳变。</summary>
    internal void Sync()
    {
        if (drag != null || movingGroup != null || resizingGroup != null) return;
        Workspace.ReconcileNodes(ReadNodes());
        EnsureMemberBounds();
        ObserveCommittedGroups();
    }

    /// <summary>选中组并提升层级。悬浮和节点入组不调用此方法；缩放需通过显式选中启动。</summary>
    internal void SelectGroup(string id)
    {
        Workspace.Snapshot.GetGroup(id);
        if (drag != null || movingGroup != null || resizingGroup != null) return;
        var change = Workspace.PromoteGroup(id);
        SelectedGroup = id;
        // 层级需要保存，但单纯选中不增加撤销步骤。
        if (change.HasChanges) ObserveCommittedGroups();
        ui.Refresh();
    }

    internal void DeselectGroup() { if (SelectedGroup == null) return; SelectedGroup = null; ui.Refresh(); }

    /// <summary>原生节点定位及组边框扩展完成后，将整个手势登记为一个历史操作。</summary>
    /// <remarks>为容纳成员而扩大的区域必须包含在同一快照中，不能等到登记撤销之后再由刷新补做。</remarks>
    internal void Commit(WorkspaceChange change, bool major = false)
    {
        if (!change.HasChanges) return;
        ApplyPositions(change.After, change.Before);
        EnsureMemberBounds();
        var after = Workspace.Snapshot;
        if (!HasValidGeometry())
        {
            // 原生拖动已产生预览位置时一并回滚，拒绝的操作不能留下额外历史或无法保存的元数据。
            ApplyPositions(change.Before, after); Workspace.Restore(change.Before);
            if (SelectedGroup != null && change.Before.Groups.All(group => group.Id != SelectedGroup)) SelectedGroup = null;
            WarnGeometryLimit(); ui.Refresh(); return;
        }
        // 拖动预览可能产生多个版本但最终回到原处，需比较内容，避免制造空撤销步骤。
        if (SameGroups(change.Before, after) && change.Before.Nodes.SequenceEqual(after.Nodes)) return;
        NativeHistory.Push(this, change.Name, change.Before, after, major);
        ObserveCommittedGroups(); ui.Refresh();
    }

    /// <summary>
    /// 按画布增量调用原生 Move。新建节点的 expectedDragPos 尚未与根位置同步，
    /// 因此先执行原生 OnDragStart 同样的初始化，避免创建、取消或撤销时跳向画布原点。
    /// </summary>
    private void MoveByCanvas(Node node, Point2 delta)
    {
        var canvasDelta = new Vector3((float)delta.X, (float)delta.Y, 0);
        // 直接子节点无需坐标转换；经缩放画布往返会使撤销也产生舍入误差。
        var parent = node.root.parent;
        var localDelta = parent != null && parent.Pointer == Board.treeRoot.Pointer ? canvasDelta :
            parent == null ? Board.treeRoot.TransformVector(canvasDelta) :
            parent.InverseTransformVector(Board.treeRoot.TransformVector(canvasDelta));
        node.expectedDragPos = node.root.localPosition;
        node.Move(localDelta, false, true);
    }

    /// <summary>仅对变化或指定的节点调用原生 Node.Move，不改变节点的父对象。</summary>
    private void ApplyPositions(WorkspaceSnapshot snapshot, WorkspaceSnapshot? comparedTo = null, IReadOnlySet<string>? affectedIds = null)
    {
        var baseline = comparedTo?.Nodes.ToDictionary(n => n.Id);
        foreach (var state in snapshot.Nodes)
        {
            if (affectedIds != null && !affectedIds.Contains(state.Id)) continue;
            if (baseline != null && baseline.TryGetValue(state.Id, out var previous) && previous.Position == state.Position) continue;
            var node = Find(state.Id); if (node == null) continue;
            var delta = state.Position - Position(node);
            if (Math.Abs(delta.X) + Math.Abs(delta.Y) > .001)
                MoveByCanvas(node, delta);
        }
    }

    /// <summary>撤销或重做分组事务，调用原生移动接口恢复坐标，并保留当前显示层序。</summary>
    /// <param name="snapshot">需要恢复的分组与几何快照。</param>
    /// <param name="comparedTo">可选的对照快照，用于跳过位置没有变化的节点。</param>
    internal void Restore(WorkspaceSnapshot snapshot, WorkspaceSnapshot? comparedTo = null)
    {
        var layers = CurrentLayers();
        CancelDrag();
        ApplyPositions(snapshot, comparedTo);
        Workspace.Restore(WithLayers(snapshot, layers)); Workspace.ReconcileNodes(ReadNodes());
        if (SelectedGroup != null && Workspace.Snapshot.Groups.All(group => group.Id != SelectedGroup)) SelectedGroup = null;
        if (!OperationManager.Working) EnsureMemberBounds();
        ObserveCommittedGroups(); ui.Refresh(); EnforceVisibility();
    }

    /// <summary>读取当前层级；它是需要持久化、但不参与命令撤销的显示状态。</summary>
    private Dictionary<string, int> CurrentLayers() => Workspace.Snapshot.Groups.ToDictionary(group => group.Id, group => group.ZIndex, StringComparer.Ordinal);

    /// <summary>恢复命令内容，同时保留前后均存在的组的最新层级。</summary>
    /// <remarks>撤销后重新出现的组使用快照层级；名称、成员、折叠状态、锚点和区域始终来自命令快照。</remarks>
    private static WorkspaceSnapshot WithLayers(WorkspaceSnapshot snapshot, IReadOnlyDictionary<string, int> layers) => new(
        snapshot.Nodes, snapshot.Groups.Select(group => new GroupState(group.Id, group.Name, group.MemberNodeIds,
            group.Collapsed, group.Anchor, group.Color, layers.TryGetValue(group.Id, out var layer) ? layer : group.ZIndex,
            group.ExpandedBounds)), snapshot.Revision);

    /// <summary>获取当前显示区域；拖动时使用冻结边界，防止原始组跟随成员一起扩大。</summary>
    internal Rect2 Bounds(GroupState group)
    {
        if (frozenBounds != null && frozenBounds.TryGetValue(group.Id, out var frozen)) return frozen;
        var expanded = group.ExpandedBounds ?? RequiredBounds(group.MemberNodeIds) ??
            new Rect2(group.Anchor.X, group.Anchor.Y - MinimumFrameHeight, MinimumFrameWidth, MinimumFrameHeight);
        return group.Collapsed ? new Rect2(expanded.X, expanded.Y + expanded.Height - HeaderHeight, CardWidth, HeaderHeight) : expanded;
    }

    /// <summary>展开组的标题不接收节点入组；折叠组以整个卡片作为目标。</summary>
    internal Rect2 DropBounds(GroupState group)
    {
        var bounds = Bounds(group);
        return group.Collapsed ? bounds : new Rect2(bounds.X, bounds.Y, bounds.Width, bounds.Height - HeaderHeight);
    }

    /// <summary>
    /// 菜单节点注册前后共用根位置和区域命中规则；悬浮本身不会提升组层级。
    /// </summary>
    private GroupState? PaletteDropTarget(Point2 position)
    {
        GroupState? target = null;
        // 单次扫描保持原有规则：层级更高者优先，同层级选择快照中靠后的组。
        // 先比较层级，减少不可能成为目标的组所需的几何计算。
        foreach (var group in Workspace.Snapshot.Groups)
        {
            if (target != null && group.ZIndex < target.ZIndex) continue;
            if (DropBounds(group).Contains(position)) target = group;
        }
        return target;
    }

    /// <summary>
    /// 原生菜单拖拽开始后启用显示反馈；预览对象不写入 Workspace、原生历史或节点注册表。
    /// </summary>
    internal void BeginPaletteDrag(NodeParcel parcel)
    {
        if (disposed || NativeHistory.Applying || drag != null || movingGroup != null || resizingGroup != null ||
            Workspace.Snapshot.Groups.Count == 0) return;
        var preview = parcel.content?.GetComponentInChildren<Node>(true);
        if (preview == null || preview.root == null ||
            Board.nodes.ToArray().Any(node => node.Pointer == preview.Pointer)) return;
        ClearPaletteDrag();
        paletteParcel = parcel;
        palettePreview = preview;
        paletteParcelPointer = parcel.Pointer;
        paletteCancelled = false;
        PreviewPaletteDrag(parcel);
    }

    /// <summary>
    /// 读取原生拖拽更新后的预览位置；outside=false 表示仍属于菜单，不能作为入组目标。
    /// 只有 OnDragStart 能建立手势，后续移动不能重新激活已取消的手势。
    /// </summary>
    internal void PreviewPaletteDrag(NodeParcel parcel)
    {
        if (disposed || paletteParcelPointer == IntPtr.Zero || parcel.Pointer != paletteParcelPointer || paletteCancelled) return;
        if (!PalettePreviewAlive()) { ClearPaletteDrag(); return; }
        SetPaletteTarget(parcel.outside ? PaletteDropTarget(Position(palettePreview!))?.Id : null);
    }

    /// <summary>
    /// 仅结束指定菜单对象的反馈。结束钩子使用起始会话与指针，防止迟到回调清除另一手势。
    /// </summary>
    internal void EndPaletteDrag(IntPtr parcelPointer)
    {
        if (paletteParcelPointer != IntPtr.Zero && paletteParcelPointer == parcelPointer) ClearPaletteDrag();
    }

    private bool PalettePreviewAlive() => paletteParcel != null && palettePreview != null &&
        paletteParcel.gameObject.activeInHierarchy && palettePreview.gameObject.activeInHierarchy &&
        palettePreview.root != null && paletteParcel.content != null &&
        paletteParcel.content.GetComponentInChildren<Node>(true)?.Pointer == palettePreview.Pointer &&
        !Board.nodes.ToArray().Any(node => node.Pointer == palettePreview.Pointer);

    /// <summary>处理未触发原生释放回调、却已禁用或销毁的预览对象。</summary>
    private void PollPaletteDrag()
    {
        if (paletteParcelPointer == IntPtr.Zero) return;
        try { if (!PalettePreviewAlive()) ClearPaletteDrag(); }
        catch (Exception error)
        {
            // 失效的 Unity 包装对象不能中断编辑器，也不能留下绿色边框。
            ClearPaletteDrag();
            Plugin.Logger.LogWarning("Palette preview expired: " + error.Message);
        }
    }

    private void SetPaletteTarget(string? target)
    {
        if (DropTarget == target) return;
        DropTarget = target;
        if (!disposed) ui.Refresh();
    }

    private void ClearPaletteDrag()
    {
        var hadGesture = paletteParcelPointer != IntPtr.Zero;
        paletteParcel = null; palettePreview = null; paletteParcelPointer = IntPtr.Zero; paletteCancelled = false;
        // 已注册节点也使用 DropTarget；菜单空闲时的清理不能误删普通节点拖动的反馈。
        if (hadGesture) SetPaletteTarget(null);
    }

    internal void BeginNodeDrag(Node node)
    {
        if (NativeHistory.Applying || drag != null || movingGroup != null || resizingGroup != null || Workspace.Snapshot.Groups.Count == 0) return;
        Sync();
        CancelledNodeGesture = false;
        var selected = Blackboard.selected.ToArray().Select(Id).ToHashSet(); selected.Add(Id(node));
        var regions = Workspace.Snapshot.Groups.Select(g => new GroupHitRegion(g.Id, DropBounds(g), g.ZIndex)).ToArray();
        frozenBounds = Workspace.Snapshot.Groups.ToDictionary(g => g.Id, Bounds);
        drag = Workspace.BeginDrag(selected, Id(node), regions); dragPrimary = Id(node);
        gestureTrashOperations.Clear(); trashGestureBefore = null;
        NativeHistory.BeginNodeGesture();
        NativeHistory.SuppressNativeMovement = true;
    }

    internal void PreviewNodeDrag()
    {
        if (drag == null || dragPrimary == null) return;
        var node = Find(dragPrimary); if (node == null) { CancelDrag(); return; }
        var preview = drag.Preview(Position(node) - drag.Before.GetNode(dragPrimary).Position);
        DropTarget = preview.TargetGroupId;
        ui.Refresh();
    }

    internal void EndNodeDrag()
    {
        if (CancelledNodeGesture) { CancelledNodeGesture = false; NativeHistory.SuppressNativeMovement = false; return; }
        if (drag == null || dragPrimary == null) return;
        var transaction = drag; var primary = dragPrimary;
        drag = null; dragPrimary = null; frozenBounds = null; DropTarget = null;
        NativeHistory.SuppressNativeMovement = false;
        NativeHistory.EndNodeGesture();
        var node = Find(primary);
        if (node == null || gestureTrashOperations.Count > 0)
        {
            // 主节点在释放前被原生逻辑删除时，仍需回滚多选集中存活节点的预览位置。
            ApplyPositions(transaction.Before, affectedIds: transaction.NodeIds.ToHashSet(StringComparer.Ordinal));
            transaction.Cancel(); transaction.Dispose();
            RefreshNativeRegistration();
            ui.Refresh(); return;
        }
        var preview = transaction.Preview(Position(node) - transaction.Before.GetNode(primary).Position);
        if (!CanAssignNodes(transaction.NodeIds, preview.TargetGroupId) ||
            preview.TargetGroupId is { } targetId && !transaction.Before.GetGroup(targetId).Collapsed && !CanFitMembers(targetId,
                transaction.Before.GetGroup(targetId).MemberNodeIds.Concat(transaction.NodeIds)))
        {
            // 本手势抑制了原生移动历史；容量不足时必须同时回滚预览位置，不能留下不可撤销的移动。
            ApplyPositions(transaction.Before, affectedIds: transaction.NodeIds.ToHashSet(StringComparer.Ordinal));
            transaction.Cancel(); transaction.Dispose(); ui.Refresh(); return;
        }
        var change = transaction.Commit(Position(node) - transaction.Before.GetNode(primary).Position);
        if (preview.TargetGroupId != null && transaction.Before.GetGroup(preview.TargetGroupId).Collapsed)
        {
            var group = transaction.Before.GetGroup(preview.TargetGroupId);
            var dragged = preview.Nodes.Select(n => n.Id).ToHashSet();
            var members = group.MemberNodeIds.Except(dragged).Select(Find).Where(n => n != null).Select(n => LayoutGeometry(n!)).ToArray();
            var incoming = dragged.Select(Find).Where(n => n != null).Select(n => LayoutGeometry(n!)).ToArray();
            var placement = NodePlacement.FindTranslationToFreeSpace(members, incoming,
                new Point2(group.Anchor.X + 120, group.Anchor.Y - 100), primary);
            Workspace.ReconcileNodes(change.After.Nodes.Select(n => dragged.Contains(n.Id) ? n with { Position = n.Position + placement } : n));
            change = change with { After = Workspace.Snapshot };
            ApplyPositions(change.After, change.Before);
        }
        if (preview.TargetGroupId is { } finalTarget && !CanFitMembers(finalTarget, Workspace.Snapshot.GetGroup(finalTarget).MemberNodeIds))
        {
            // 折叠组排布后再验证实际位置；原始悬浮点合法不代表排布结果仍处于可保存范围。
            ApplyPositions(transaction.Before, Workspace.Snapshot); Workspace.Restore(transaction.Before);
            transaction.Dispose(); WarnGeometryLimit(); ui.Refresh(); return;
        }
        Commit(change); transaction.Dispose();
        ui.Refresh();
    }

    /// <summary>回滚尚未提交的移动或缩放，结束光标反馈，并恢复原生命令抑制状态。</summary>
    internal void CancelDrag()
    {
        ui.Cursor.Reset();
        var hadPaletteGesture = paletteParcelPointer != IntPtr.Zero;
        if (hadPaletteGesture) paletteCancelled = true;
        if (resizeBefore != null)
        {
            Workspace.Restore(WithLayers(resizeBefore, CurrentLayers()));
            resizeBefore = null; resizingGroup = null;
            ui.Refresh();
        }
        if (drag != null)
        {
            CancelledNodeGesture = true;
            ApplyPositions(drag.Before, affectedIds: drag.NodeIds.ToHashSet(StringComparer.Ordinal));
            drag.Cancel(); drag.Dispose(); drag = null;
        }
        if (groupMoveBefore != null)
        {
            var members = movingGroup == null ? new HashSet<string>() :
                groupMoveBefore.GetGroup(movingGroup).MemberNodeIds.ToHashSet(StringComparer.Ordinal);
            ApplyPositions(groupMoveBefore, affectedIds: members); Workspace.Restore(WithLayers(groupMoveBefore, CurrentLayers()));
        }
        groupMoveBefore = null; movingGroup = null; dragPrimary = null; frozenBounds = null;
        NativeHistory.EndNodeGesture();
        // 原生已经成功回收的节点不能随预览取消一并丢弃；为其保留撤销边界。
        if (disposed) { gestureTrashOperations.Clear(); trashGestureBefore = null; }
        else if (gestureTrashOperations.Count > 0) RefreshNativeRegistration();
        ObserveCommittedGroups();
        NativeHistory.SuppressNativeMovement = CancelledNodeGesture; DropTarget = null;
        if (disposed) ClearPaletteDrag();
        else if (hadPaletteGesture) ui.Refresh();
    }

    /// <summary>回收前使用手势起始几何，补偿已由 Mod 抑制的原生移动历史。</summary>
    internal WorkspaceSnapshot CaptureTrashBefore()
    {
        if (drag != null) return drag.Before;
        Sync(); return Workspace.Snapshot;
    }

    /// <summary>原生已成功暂存节点；多选手势等全部结束后再冻结统一 After，避免记录中间位置。</summary>
    internal void RecordTrashed(Operation operation, WorkspaceSnapshot before)
    {
        if (drag != null)
        {
            trashGestureBefore ??= before;
            gestureTrashOperations.Add(operation);
            return;
        }
        RefreshNativeRegistration();
        NativeHistory.AttachNative(this, operation, before, Workspace.Snapshot);
    }

    /// <summary>只在完整原生操作或手势结束后同步；不能在 Undo 的中间注册位置扩大组边框。</summary>
    internal void RefreshNativeRegistration()
    {
        if (disposed || drag != null || movingGroup != null || resizingGroup != null || NativeHistory.Applying || OperationManager.Working) return;
        Sync(); CompleteTrashedGesture(); ui.Refresh(); EnforceVisibility();
    }

    /// <summary>完整手势结束后冻结统一结果；原生回放途中保留待处理记录，交给外层完成回调。</summary>
    private void CompleteTrashedGesture()
    {
        if (gestureTrashOperations.Count == 0 || trashGestureBefore == null || NativeHistory.Applying || OperationManager.Working) return;
        foreach (var operation in gestureTrashOperations)
            NativeHistory.AttachNative(this, operation, trashGestureBefore, Workspace.Snapshot);
        gestureTrashOperations.Clear(); trashGestureBefore = null;
    }

    /// <summary>冻结取回前的目标区域，避免原组的自动边界跟随重新注册的成员跳到组外落点。</summary>
    internal GroupHitRegion[] CaptureReturnRegions() => Workspace.Snapshot.Groups
        .Select(group => new GroupHitRegion(group.Id, DropBounds(group), group.ZIndex)).ToArray();

    /// <summary>原生从回收站取回已完成；按最终落点提交归属，组外也必须显式清除旧归属。</summary>
    internal void TrashReturned(Node node, NodeMoveOutTrashOperation operation, WorkspaceSnapshot before, IReadOnlyList<GroupHitRegion> regions)
    {
        if (disposed || NativeHistory.Applying) return;
        var position = Position(node);
        string? targetId = null; var top = int.MinValue;
        foreach (var region in regions)
            if (region.Bounds.Contains(position) && (targetId == null || region.ZIndex >= top))
            { targetId = region.GroupId; top = region.ZIndex; }
        // 先只同步注册表，再转移归属；不能先用旧归属把原组边框扩大到组外落点。
        Workspace.ReconcileNodes(ReadNodes());
        var ids = new[] { Id(node) };
        if (targetId != null && !CanAssignNodes(ids, targetId)) targetId = null;
        var originalNativePosition = operation.newPosLocal;
        try
        {
            Point2? offset = null;
            if (targetId != null && Workspace.Snapshot.GetGroup(targetId).Collapsed)
            {
                var group = Workspace.Snapshot.GetGroup(targetId);
                var existing = group.MemberNodeIds.Where(id => id != ids[0]).Select(Find).Where(found => found != null)
                    .Select(found => LayoutGeometry(found!)).ToArray();
                // 先计算布局再变更归属；布局拒绝非法几何时不能留下半完成状态。
                offset = NodePlacement.FindTranslationToFreeSpace(existing, new[] { LayoutGeometry(node) },
                    new Point2(group.Anchor.X + 120, group.Anchor.Y - 100), ids[0]);
            }
            Workspace.TransferNodes(ids, targetId);
            if (offset is { } translation)
            {
                MoveByCanvas(node, translation);
                // 原生取回历史保存的是 Node.transform 的局部位置，不能拿世界坐标代替。
                operation.newPosLocal = node.transform.localPosition;
                Workspace.ReconcileNodes(ReadNodes()); Workspace.SetCollapsed(targetId!, false);
            }
            EnsureMemberBounds();
            if (!HasValidGeometry() || targetId != null && !CanFitMembers(targetId, Workspace.Snapshot.GetGroup(targetId).MemberNodeIds))
                throw new InvalidOperationException("取回节点的分组布局超出可保存范围。");
        }
        catch (Exception error)
        {
            // 仅撤回 Mod 的适配，保留原生取回结果和历史，明确降级为组外节点。
            MoveByCanvas(node, position - Position(node)); operation.newPosLocal = originalNativePosition;
            Workspace.Restore(new WorkspaceSnapshot(ReadNodes(), before.Groups));
            Workspace.TransferNodes(ids, null); EnsureMemberBounds(); WarnGeometryLimit();
            Plugin.Logger.LogWarning("回收站节点已保留在原生落点、置于组外：" + error.Message);
        }
        NativeHistory.AttachNative(this, operation, before, Workspace.Snapshot);
        ObserveCommittedGroups(); ui.Refresh(); EnforceVisibility();
    }

    /// <summary>回调中优先使用 NGUI 当前触点，空闲时才使用系统鼠标位置。</summary>
    internal Point2 CanvasPointer()
    {
        var camera = UICamera.currentCamera ?? UICamera.FindCameraForLayer(Board.treeRoot.gameObject.layer)?.GetComponent<Camera>();
        if (camera == null) return new Point2(0, 0);
        var touch = UICamera.currentTouch;
        var screen = touch == null ? Input.mousePosition : new Vector3(touch.pos.x, touch.pos.y, 0);
        return CanvasRay(camera.ScreenPointToRay(screen));
    }
    private Point2 CanvasCenter()
    {
        var camera = UICamera.FindCameraForLayer(Board.treeRoot.gameObject.layer)?.GetComponent<Camera>();
        return camera == null ? new Point2(0, 0) : CanvasRay(camera.ViewportPointToRay(new Vector3(.5f, .5f, 0)));
    }
    private Point2 CanvasRay(Ray ray)
    {
        var plane = new Plane(Board.treeRoot.forward, Board.treeRoot.position);
        if (!plane.Raycast(ray, out var distance)) return new Point2(0, 0);
        var point = Board.treeRoot.InverseTransformPoint(ray.GetPoint(distance));
        return new(point.x, point.y);
    }
    internal void BeginGroupMove(string id) { SelectGroup(id); Sync(); groupMoveBefore = Workspace.Snapshot; movingGroup = id; lastPointer = CanvasPointer(); }

    /// <summary>开始仅修改区域的缩放手势；上边框用于移动。</summary>
    /// <param name="id">目标组 ID。</param>
    /// <param name="edges">方向位掩码：左=1、右=2、下=4、左下=5、右下=6。</param>
    internal void BeginResize(string id, int edges)
    {
        if (Workspace.Snapshot.GetGroup(id).Collapsed || edges is not (1 or 2 or 4 or 5 or 6)) return;
        SelectGroup(id); Sync();
        resizeBefore = Workspace.Snapshot; resizingGroup = id;
        resizeEdges = edges; resizePointer = CanvasPointer();
    }
    internal void PreviewResize()
    {
        if (resizingGroup == null || resizeBefore == null) return;
        var original = resizeBefore.GetGroup(resizingGroup);
        var start = original.ExpandedBounds ?? Bounds(original);
        var delta = CanvasPointer() - resizePointer;
        var left = start.X; var right = start.X + start.Width;
        var bottom = start.Y; var top = start.Y + start.Height;
        if ((resizeEdges & 1) != 0) left = Math.Min(left + delta.X, right - MinimumFrameWidth);
        if ((resizeEdges & 2) != 0) right = Math.Max(right + delta.X, left + MinimumFrameWidth);
        if ((resizeEdges & 4) != 0) bottom = Math.Min(bottom + delta.Y, top - MinimumFrameHeight);
        if (RequiredBounds(original.MemberNodeIds) is { } minimum)
        {
            left = Math.Min(left, minimum.X); bottom = Math.Min(bottom, minimum.Y);
            right = Math.Max(right, minimum.X + minimum.Width); top = Math.Max(top, minimum.Y + minimum.Height);
        }
        var region = new Rect2(left, bottom, right - left, top - bottom);
        if (!CanUseGeometry(original.Anchor, region)) return;
        Workspace.SetExpandedBounds(resizingGroup, region);
        ui.Refresh();
    }
    internal void EndResize()
    {
        if (resizingGroup == null || resizeBefore == null) return;
        var before = resizeBefore; var after = Workspace.Snapshot;
        resizeBefore = null; resizingGroup = null;
        if (SameGroups(before, after) && before.Nodes.SequenceEqual(after.Nodes))
        {
            ui.Refresh();
            return;
        }
        Commit(new WorkspaceChange("调整节点组区域", before, after));
    }

    /// <summary>根据可见原生控件计算容纳成员所需的区域；选择动画不参与，避免边框随动画膨胀。</summary>
    private Rect2? RequiredBounds(IEnumerable<string> ids)
    {
        var found = false;
        var minX = double.PositiveInfinity; var minY = double.PositiveInfinity;
        var maxX = double.NegativeInfinity; var maxY = double.NegativeInfinity;
        // 每个角点只计算一次极值，避免拖动刷新时构造所有角点的临时集合。
        foreach (var id in ids)
        {
            var node = Find(id); if (node == null) continue;
            foreach (var widget in node.GetComponentsInChildren<UIWidget>(true))
            {
                if (!widget.gameObject.activeInHierarchy ||
                    (node.selectedEffect != null && widget.transform.IsChildOf(node.selectedEffect.transform))) continue;
                foreach (var corner in widget.worldCorners)
                {
                    var point = Board.treeRoot.InverseTransformPoint(corner);
                    minX = Math.Min(minX, point.x); minY = Math.Min(minY, point.y);
                    maxX = Math.Max(maxX, point.x); maxY = Math.Max(maxY, point.y);
                    found = true;
                }
            }
        }
        if (!found) return null;
        var left = minX - 36; var bottom = minY - 36;
        var right = maxX + 36;
        var top = maxY + HeaderHeight + 36;
        return new Rect2(left, bottom, Math.Max(MinimumFrameWidth, right - left), Math.Max(MinimumFrameHeight, top - bottom));
    }

    private void EnsureMemberBounds()
    {
        foreach (var group in Workspace.Snapshot.Groups.ToArray())
        {
            if (group.ExpandedBounds is not { } region || RequiredBounds(group.MemberNodeIds) is not { } required) continue;
            var left = Math.Min(region.X, required.X); var bottom = Math.Min(region.Y, required.Y);
            var right = Math.Max(region.X + region.Width, required.X + required.Width);
            var top = Math.Max(region.Y + region.Height, required.Y + required.Height);
            var expanded = new Rect2(left, bottom, right - left, top - bottom);
            if (expanded == region) continue;
            // 原生自动化仍可把节点移至范围外；不要因此把原本可保存的组区域改成非法值。
            if (!CanUseGeometry(group.Anchor, expanded)) continue;
            Workspace.SetExpandedBounds(group.Id, expanded);
        }
    }
    internal void PreviewGroupMove()
    {
        if (movingGroup == null || groupMoveBefore == null) return;
        var pointer = CanvasPointer(); var delta = pointer - lastPointer; lastPointer = pointer;
        var group = Workspace.Snapshot.GetGroup(movingGroup);
        var region = group.ExpandedBounds is { } bounds ? new Rect2(bounds.X + delta.X, bounds.Y + delta.Y, bounds.Width, bounds.Height) : (Rect2?)null;
        if (!CanUseGeometry(group.Anchor + delta, region)) return;
        var change = Workspace.MoveGroup(movingGroup, delta); ApplyPositions(change.After, change.Before); ui.Refresh();
    }
    internal void EndGroupMove()
    {
        if (movingGroup == null || groupMoveBefore == null) return;
        var before = groupMoveBefore; groupMoveBefore = null; movingGroup = null;
        Commit(new WorkspaceChange("移动节点组", before, Workspace.Snapshot));
    }

    /// <summary>临时排布使用控件完整包围盒；移动节点时仍以原生根坐标为准。</summary>
    private NodeState LayoutGeometry(Node node)
    {
        var corners = node.GetComponentsInChildren<UIWidget>(true).SelectMany(widget => widget.worldCorners.ToArray())
            .Select(corner => Board.treeRoot.InverseTransformPoint(corner)).ToArray();
        if (corners.Length == 0) return new NodeState(Id(node), Position(node), new Point2(280,110));
        var minX = corners.Min(p => p.x); var maxX = corners.Max(p => p.x);
        var minY = corners.Min(p => p.y); var maxY = corners.Max(p => p.y);
        return new NodeState(Id(node), new Point2((minX + maxX) / 2, (minY + maxY) / 2), new Point2(maxX-minX,maxY-minY));
    }

    /// <summary>节点完成原生注册后补充入组，并把入组操作链接到同一次原生创建历史。</summary>
    internal void PaletteCreated(Node node)
    {
        if (NativeHistory.Applying || disposed || OperationManager.history.Count == 0) return;
        var previousCreation = OperationManager.history.Peek().TryCast<NodeCreateOperation>();
        if (previousCreation == null || previousCreation.guid.ToString() != Id(node)) return;
        Sync(); var target = PaletteDropTarget(Position(node));
        // 原生创建已完成；容量不足时保留原生创建与撤销，节点保持在组外。
        if (target == null || !CanAssignNodes(new[] { Id(node) }, target.Id)) return;
        var originalPosition = Position(node); var originalCreationPosition = previousCreation.pos;
        var change = Workspace.TransferNodes(new[] { Id(node) }, target.Id);
        if (target.Collapsed)
        {
            var existing = target.MemberNodeIds.Where(id => id != Id(node)).Select(Find).Where(n => n != null).Select(n => LayoutGeometry(n!)).ToArray();
            var placement = NodePlacement.FindTranslationToFreeSpace(existing, new[] { LayoutGeometry(node) },
                new Point2(target.Anchor.X + 120, target.Anchor.Y - 100), Id(node));
            MoveByCanvas(node, placement);
            previousCreation.pos = new Vector2((float)Position(node).X, (float)Position(node).Y);
            Workspace.ReconcileNodes(ReadNodes());
            Workspace.SetCollapsed(target.Id, false);
        }
        // 在同一复合命令中记录区域扩大，使重做立即恢复节点、成员关系和最终区域。
        EnsureMemberBounds();
        if (!HasValidGeometry() || !CanFitMembers(target.Id, Workspace.Snapshot.GetGroup(target.Id).MemberNodeIds))
        {
            // 入组失败不撤销原生创建；还原折叠组临时排布及其创建操作中的最终位置。
            MoveByCanvas(node, originalPosition - Position(node)); previousCreation.pos = originalCreationPosition;
            Workspace.Restore(change.Before); WarnGeometryLimit(); ui.Refresh(); return;
        }
        // 向前链接已有创建操作，不能重复执行原生节点创建。
        NativeHistory.Push(this, "新节点加入组", change.Before, Workspace.Snapshot);
        if (OperationManager.history.Count > 1) { var last = OperationManager.history.Pop(); var previous = OperationManager.history.Peek(); OperationManager.history.Push(last); last.linkedPrev = previous; previous.linkedNext = last; }
        ObserveCommittedGroups();
        ui.Refresh();
    }

    /// <summary>原生切线工具应忽略折叠后隐藏的原线，以及只读的显示投影线。</summary>
    internal bool IsHidden(Line line) => visibility.IsHidden(line.Pointer) || ui.IsProjected(line);
    /// <summary>折叠时隐藏原生成员及相关连线；由 VisibilityGuard 保存并恢复原启用状态。</summary>
    internal void EnforceVisibility()
    {
        if (disposed) return;
        var groups = Workspace.Snapshot.Groups;
        if (!ReferenceEquals(visibilityGroups, groups))
        {
            collapsedMembers.Clear();
            foreach (var group in groups)
                if (group.Collapsed)
                    foreach (var id in group.MemberNodeIds) collapsedMembers.Add(id);
            visibilityGroups = groups;
        }
        if (collapsedMembers.Count == 0)
        {
            visibility.RestoreAll();
            return;
        }
        var hidden = new List<Component>();
        foreach (var node in Board.nodes.ToArray())
        {
            if (collapsedMembers.Contains(Id(node))) hidden.Add(node);
            foreach (var connection in node.connectionsTo.ToArray())
                if (connection.line != null && (collapsedMembers.Contains(Id(node)) || collapsedMembers.Contains(Id(connection.target)))) hidden.Add(connection.line);
        }
        visibility.Apply(hidden);
    }

    public void Dispose()
    {
        lock (saveGate)
        {
            if (disposed) return;
            disposed = true; // 清空队列和拆除会话前，先关闭后台回调入口。
            while (saves.TryDequeue(out _)) { }
        }
        TryCleanup("pending save receipts", () => EditorPatches.ClearPending(this));
        TryCleanup("native recovery prompt", recoveryPrompt.Dispose);
        TryCleanup("native quit prompt", quitPrompt.Dispose);
        // 各项清理独立执行；某个 Unity 引用或文件失效不能阻止其他子系统恢复，异常隔离在 Mod 内。
        TryCleanup("drag rollback", CancelDrag);
        TryCleanup("native movement suppression", () => NativeHistory.SuppressNativeMovement = false);
        TryCleanup("native visibility restoration", visibility.Dispose);
        TryCleanup("group UI", ui.Dispose);
        TryCleanup("group history", NativeHistory.Clear);
        if (!Dirty) return;
        TryCleanup("dirty group recovery", () =>
        {
            var path = RecoveryProjectPath(ProjectId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var hash = new string('0', 64);
            var current = PersistentData.projectPath;
            // 计算哈希失败也保留恢复快照；零哈希会在下次打开工程时触发布局安全校正。
            if (!string.IsNullOrEmpty(current) && File.Exists(current))
            {
                try { hash = Hash(current); }
                catch (Exception error) { Plugin.Logger.LogWarning("Recovery hash unavailable: " + error.Message); }
            }
            var result = store.Save(path, new GroupFileSnapshot(ProjectId, hash, Workspace.Snapshot.Groups));
            if (result.Success)
                Plugin.Logger.LogWarning($"Unsaved node-group recovery retained at {GroupStore.GetSidecarPath(path)}");
            else
                Plugin.Logger.LogError("Unsaved node-group recovery could not be saved: " + string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        });
    }

    private static void TryCleanup(string activity, Action cleanup)
    {
        try { cleanup(); }
        catch (Exception error)
        {
            // 日志失败也不能中断原生 Load/OnDestroy。
            try { Plugin.Logger.LogError($"NodeGroups cleanup failed ({activity}): {error}"); }
            catch { }
        }
    }
}

/// <summary>隐藏原生控件和碰撞体时记录原启用状态，取消隐藏时精确恢复，而非一律启用。</summary>
internal sealed class VisibilityGuard : IDisposable
{
    private readonly Dictionary<IntPtr, (UIWidget Widget, bool Enabled)> widgets = new();
    private readonly Dictionary<IntPtr, (Collider Collider, bool Enabled)> colliders = new();
    private readonly HashSet<IntPtr> hiddenObjects = new();
    private readonly HashSet<IntPtr> wantedWidgets = new(), wantedColliders = new();
    private readonly List<IntPtr> stale = new();
    internal bool IsHidden(IntPtr pointer) => hiddenObjects.Contains(pointer);
    internal void Apply(IEnumerable<Component> hidden)
    {
        hiddenObjects.Clear(); wantedWidgets.Clear(); wantedColliders.Clear();
        foreach (var target in hidden)
        {
            if (target == null || !hiddenObjects.Add(target.Pointer)) continue;
            foreach (var widget in target.GetComponentsInChildren<UIWidget>(true))
            {
                if (widget == null) continue;
                wantedWidgets.Add(widget.Pointer); if (!widgets.ContainsKey(widget.Pointer)) widgets.Add(widget.Pointer, (widget, widget.enabled));
                widget.enabled = false;
            }
            foreach (var collider in target.GetComponentsInChildren<Collider>(true))
            {
                if (collider == null) continue;
                wantedColliders.Add(collider.Pointer); if (!colliders.ContainsKey(collider.Pointer)) colliders.Add(collider.Pointer, (collider, collider.enabled));
                collider.enabled = false;
            }
        }
        // 每帧仍重新发现原生子控件，以覆盖运行时新增/删除；仅复用托管工作集合，不缓存原生层级。
        stale.Clear();
        foreach (var key in widgets.Keys) if (!wantedWidgets.Contains(key)) stale.Add(key);
        foreach (var key in stale) { var state = widgets[key]; if (state.Widget != null) state.Widget.enabled = state.Enabled; widgets.Remove(key); }
        stale.Clear();
        foreach (var key in colliders.Keys) if (!wantedColliders.Contains(key)) stale.Add(key);
        foreach (var key in stale) { var state = colliders[key]; if (state.Collider != null) state.Collider.enabled = state.Enabled; colliders.Remove(key); }
        stale.Clear();
    }
    /// <summary>恢复所有隐藏前状态；全展开时直接调用，避免每帧扫描原生节点和连接。</summary>
    internal void RestoreAll()
    {
        // 场景销毁时组件可能先后失效，逐个独立恢复，避免一个失效引用使其他节点仍被隐藏。
        hiddenObjects.Clear();
        foreach (var state in widgets.Values.ToArray())
        {
            try { if (state.Widget != null) state.Widget.enabled = state.Enabled; }
            catch (Exception error) { LogRestoreError(error); }
        }
        widgets.Clear();
        foreach (var state in colliders.Values.ToArray())
        {
            try { if (state.Collider != null) state.Collider.enabled = state.Enabled; }
            catch (Exception error) { LogRestoreError(error); }
        }
        colliders.Clear();
        wantedWidgets.Clear(); wantedColliders.Clear(); stale.Clear();
    }

    public void Dispose() => RestoreAll();

    private static void LogRestoreError(Exception error)
    {
        try { Plugin.Logger.LogWarning("Could not restore an expired native group component: " + error.Message); }
        catch { }
    }
}
