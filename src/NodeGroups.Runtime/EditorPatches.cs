using System.Collections.Concurrent;
using AzureArchive.Automation;
using HarmonyLib;
using NodeGroups.Core;
using Studio.Scripts;
using Studio.Scripts.Nodes;
using Studio.Scripts.OperationManagement;

namespace AzureArchive.NodeGroups.Runtime;

/// <summary>观察原生输入、创建、保存和生命周期事件；非本 Mod 会话保持原生流程。</summary>
/// <remarks>Prefix 捕获本次调用的归属和快照，Postfix 观察成功结果，Finalizer 清理手势并保留原生异常。</remarks>
internal static class EditorPatches
{
    [HarmonyPatch(typeof(Node), nameof(Node.OnDragStart))]
    private static class DragStart
    {
        static void Prefix(out bool __state) { __state = GroupHost.Session?.Workspace.Snapshot.Groups.Count > 0; if (__state) NativeHistory.NativeMovementCallbackDepth++; }
        static void Postfix(Node __instance) => TryHook("drag start", () => GroupHost.Session?.BeginNodeDrag(__instance));
        static Exception? Finalizer(Exception? __exception, bool __state) { if (__state) NativeHistory.NativeMovementCallbackDepth--; return __exception; }
    }
    [HarmonyPatch(typeof(Node), nameof(Node.OnDrag))]
    private static class DragMove
    {
        static bool Prefix(out bool __state)
        {
            __state = NativeHistory.SuppressNativeMovement;
            if (__state) NativeHistory.NativeMovementCallbackDepth++;
            return GroupHost.Session?.CancelledNodeGesture != true;
        }
        static void Postfix() => TryHook("drag preview", () => GroupHost.Session?.PreviewNodeDrag());
        static Exception? Finalizer(Exception? __exception, bool __state) { if (__state) NativeHistory.NativeMovementCallbackDepth--; return __exception; }
    }
    [HarmonyPatch(typeof(Node), nameof(Node.OnDragEnd))]
    private static class DragEnd
    {
        static void Prefix(out bool __state) { __state = NativeHistory.SuppressNativeMovement; if (__state) NativeHistory.NativeMovementCallbackDepth++; }
        static void Postfix() => TryHook("drag commit", () => GroupHost.Session?.EndNodeDrag());
        static Exception? Finalizer(Exception? __exception, bool __state) { if (__state) NativeHistory.NativeMovementCallbackDepth--; return __exception; }
    }
    [HarmonyPatch(typeof(Blackboard), nameof(Blackboard.MarkDeletion))]
    private static class CutFilter { static bool Prefix(Line __0) => GroupHost.Session?.IsHidden(__0) != true; }

    /// <summary>原生节点成功选中后，清除独立的节点组选中外观。</summary>
    [HarmonyPatch(typeof(Node), nameof(Node.Select), new[] { typeof(bool) })]
    private static class NativeSelection
    {
        static void Postfix(Node __instance, bool __result)
        {
            if (!__result || NativeHistory.Applying) return;
            ClearGroupAfterNodeSelection(__instance);
        }
    }

    /// <summary>点击原生节点时清除组选中状态，包括原生点击逻辑反选节点的情况。</summary>
    [HarmonyPatch(typeof(Node), nameof(Node.OnClick))]
    private static class NativeSelectionClick
    {
        static void Prefix(Node __instance, out EditorSession? __state)
        {
            EditorSession? captured = null;
            // 提前捕获会话归属，但隔离失效包装对象异常，不能阻止原生点击。
            TryHook("native node click ownership", () =>
            {
                var owner = GroupHost.Session;
                if (!NativeHistory.Applying && owner != null && !owner.IsDisposed &&
                    owner.Find(EditorSession.Id(__instance))?.Pointer == __instance.Pointer) captured = owner;
            });
            __state = captured;
        }
        // 原生点击可能取消选中或删除节点，因此回调只检查已捕获的会话，不再次访问节点。
        static void Postfix(EditorSession? __state) => TryHook("native node click", () =>
        { if (__state != null && !__state.IsDisposed && ReferenceEquals(GroupHost.Session, __state)) __state.DeselectGroup(); });
    }

    private static void ClearGroupAfterNodeSelection(Node node) => TryHook("native node selection", () =>
    {
        var session = GroupHost.Session;
        if (session == null || session.IsDisposed || session.Find(EditorSession.Id(node))?.Pointer != node.Pointer) return;
        session.DeselectGroup();
    });

    /// <summary>空白画布的原生取消选择操作也应取消组选中状态。</summary>
    [HarmonyPatch(typeof(Blackboard), nameof(Blackboard.OnClick))]
    private static class CanvasSelection
    {
        static void Postfix(Blackboard __instance) => ClearGroupAfterCanvasCancellation(__instance);
    }

    [HarmonyPatch(typeof(Blackboard), nameof(Blackboard.DeselectAllChildren), new[] { typeof(bool) })]
    private static class CanvasDeselect
    {
        static void Postfix(Blackboard __instance) => ClearGroupAfterCanvasCancellation(__instance);
    }

    private static void ClearGroupAfterCanvasCancellation(Blackboard board)
    {
        if (NativeHistory.Applying || IsGroupPointerTarget()) return;
        TryHook("native canvas cancellation", () =>
        {
            var session = GroupHost.Session;
            if (session == null || session.IsDisposed || session.Board.Pointer != board.Pointer || Blackboard.selected.Count != 0) return;
            session.DeselectGroup();
        });
    }

    /// <summary>
    /// 组标题或面板处理 NGUI 事件时也可能触发原生清选，此时不能误清除组选中状态。
    /// 同时检查按下对象与当前对象，因为释放回调结束前，光标可能已移出标题。
    /// </summary>
    private static bool IsGroupPointerTarget()
    {
        var touch = UICamera.currentTouch;
        return IsGroupSurface(touch?.pressed) || IsGroupSurface(touch?.current);
    }

    private static bool IsGroupSurface(UnityEngine.GameObject? target)
    {
        for (var transform = target?.transform; transform != null; transform = transform.parent)
            if (transform.name.StartsWith("NodeGroup ", StringComparison.Ordinal) || transform.name == "NodeGroups toolbar") return true;
        return false;
    }

    /// <summary>
    /// 菜单拖拽绑定开始时的编辑器和菜单对象，与稍后的创建成功回执分开管理。
    /// 钩子仅观察原生回调，不抑制原生移动、注册或取消逻辑。
    /// </summary>
    private sealed record PaletteGestureReceipt(EditorSession Owner, IntPtr ParcelPointer);

    [HarmonyPatch(typeof(NodeParcel), nameof(NodeParcel.OnDragStart))]
    private static class PaletteDragStart
    {
        static void Prefix(NodeParcel __instance, out PaletteGestureReceipt? __state) => __state = CapturePaletteGesture(__instance);
        static void Postfix(NodeParcel __instance, PaletteGestureReceipt? __state) => TryHook("palette drag start", () =>
        {
            if (OwnsPaletteGesture(__state, __instance)) __state!.Owner.BeginPaletteDrag(__instance);
        });
        static Exception? Finalizer(Exception? __exception, PaletteGestureReceipt? __state)
        {
            if (__exception != null) ClearPaletteGesture(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(NodeParcel), nameof(NodeParcel.OnDrag))]
    private static class PaletteDragMove
    {
        static void Prefix(NodeParcel __instance, out PaletteGestureReceipt? __state) => __state = CapturePaletteGesture(__instance);
        static void Postfix(NodeParcel __instance, PaletteGestureReceipt? __state) => TryHook("palette drag preview", () =>
        {
            if (OwnsPaletteGesture(__state, __instance)) __state!.Owner.PreviewPaletteDrag(__instance);
        });
        static Exception? Finalizer(Exception? __exception, PaletteGestureReceipt? __state)
        {
            if (__exception != null) ClearPaletteGesture(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(NodeParcel), nameof(NodeParcel.OnDragEnd))]
    private static class PaletteDragEnd
    {
        static void Prefix(NodeParcel __instance, out PaletteGestureReceipt? __state) => __state = CapturePaletteGesture(__instance);
        // 创建成功或抛异常都会进入 Finalizer；清理反馈后原样返回原生异常。
        static Exception? Finalizer(Exception? __exception, PaletteGestureReceipt? __state)
        {
            ClearPaletteGesture(__state);
            return __exception;
        }
    }

    private static PaletteGestureReceipt? CapturePaletteGesture(NodeParcel parcel)
    {
        PaletteGestureReceipt? receipt = null;
        TryHook("palette gesture ownership", () =>
        {
            var owner = GroupHost.Session;
            if (owner != null && !owner.IsDisposed) receipt = new PaletteGestureReceipt(owner, parcel.Pointer);
        });
        return receipt;
    }

    private static bool OwnsPaletteGesture(PaletteGestureReceipt? receipt, NodeParcel parcel) => receipt != null &&
        !receipt.Owner.IsDisposed && ReferenceEquals(GroupHost.Session, receipt.Owner) && receipt.ParcelPointer == parcel.Pointer;

    private static void ClearPaletteGesture(PaletteGestureReceipt? receipt) => TryHook("palette drag cleanup", () =>
    {
        if (receipt != null) receipt.Owner.EndPaletteDrag(receipt.ParcelPointer);
    });

    /// <summary>
    /// 捕获尚未注册的预览节点及其编辑器、历史归属。
    /// 当前应用中，菜单释放使用 Unpack()，程序化放置使用 Unpack(Vector3)，两者互不调用。
    /// 两条路径都只在原生成功结束后补充分组，创建过程仍由原生实现负责。
    /// </summary>
    private sealed record PaletteReceipt(EditorSession Owner, Node Preview, int HistoryCount, IntPtr PreviousTop,
        WorkspaceSnapshot Before, GroupHitRegion[] Regions);

    [HarmonyPatch(typeof(NodeParcel), nameof(NodeParcel.Unpack), new Type[] { })]
    private static class PaletteMenu
    {
        static void Prefix(NodeParcel __instance, out PaletteReceipt? __state) => __state = CapturePalette(__instance);
        static void Postfix(PaletteReceipt? __state) => CompletePalette(__state);
    }

    [HarmonyPatch(typeof(NodeParcel), nameof(NodeParcel.Unpack), new[] { typeof(UnityEngine.Vector3) })]
    private static class PalettePlacement
    {
        static void Prefix(NodeParcel __instance, out PaletteReceipt? __state) => __state = CapturePalette(__instance);
        static void Postfix(PaletteReceipt? __state) => CompletePalette(__state);
    }

    /// <summary>预览对象查找失败不能阻止原生 Unpack 执行。</summary>
    private static PaletteReceipt? CapturePalette(NodeParcel parcel)
    {
        PaletteReceipt? receipt = null;
        TryHook("palette preview capture", () =>
        {
            var owner = GroupHost.Session;
            if (NativeHistory.Applying || owner == null || owner.IsDisposed || owner.Workspace.Snapshot.Groups.Count == 0) return;
            var preview = parcel.content?.GetComponentInChildren<Node>(true);
            if (preview == null || owner.Board.nodes.ToArray().Any(node => node.Pointer == preview.Pointer)) return;
            owner.Sync();
            receipt = new PaletteReceipt(owner, preview, OperationManager.history.Count,
                OperationManager.history.Count == 0 ? IntPtr.Zero : OperationManager.history.Peek().Pointer,
                owner.Workspace.Snapshot, owner.CaptureReturnRegions());
        });
        return receipt;
    }

    /// <summary>
    /// 只把本次调用新注册的节点关联到对应创建操作；复用预览、场景切换、撤销重做、
    /// 创建失败或无关历史都不能获得成员关系。成功关联后历史栈顶身份会改变，避免重复入组。
    /// </summary>
    private static void CompletePalette(PaletteReceipt? receipt)
    {
        if (receipt == null) return;
        TryHook("palette group membership", () =>
        {
            var owner = receipt.Owner;
            if (NativeHistory.Applying || owner.IsDisposed || !ReferenceEquals(GroupHost.Session, owner) ||
                OperationManager.history.Count <= receipt.HistoryCount) return;
            var id = EditorSession.Id(receipt.Preview);
            if (owner.Find(id)?.Pointer != receipt.Preview.Pointer) return;
            var operation = OperationManager.history.Peek();
            if (operation.Pointer == receipt.PreviousTop) return;
            var creation = operation.TryCast<NodeCreateOperation>();
            if (creation != null && creation.guid.ToString() == id) owner.PaletteCreated(receipt.Preview);
            else
            {
                var restored = operation.TryCast<NodeMoveOutTrashOperation>();
                if (restored != null && restored.guid.ToString() == id)
                    owner.TrashReturned(receipt.Preview, restored, receipt.Before, receipt.Regions);
            }
        });
    }

    [HarmonyPatch(typeof(StudioCommon), nameof(StudioCommon.OnDestroy))]
    private static class End
    {
        static void Prefix(StudioCommon __instance)
        {
            TryHook("editor destroy", () =>
            {
                var session = GroupHost.Session;
                if (session == null || session.Editor.Pointer != __instance.Pointer) return;
                Detach(session);
            });
        }
    }
    [HarmonyPatch(typeof(StudioCommon), nameof(StudioCommon.Load))]
    private static class Reload
    {
        static void Prefix() => TryHook("editor load", () =>
        {
            var session = GroupHost.Session;
            if (session != null) Detach(session);
        });
    }

    // ProjectFileStore.Save 可能在后台延迟写入较早的剧情快照。
    // 必须配对当时冻结的分组回执，不能把当前组状态写给旧剧情。
    [HarmonyPatch(typeof(ProjectFileStore), nameof(ProjectFileStore.Save))]
    private static class Save
    {
        static void Prefix(string __0, ProjectData __1, out EditorSession.SaveReceipt? __state)
        {
            __state = null;
            try
            {
                var session = GroupHost.Session;
                if (session == null || session.IsDisposed || __1 == null || session.ProjectId != __1.ProjectId) return;
                // 输入 ProjectData 是原生保存冻结的数据，可在此序列化；实时 CaptureProject 仅限主线程。
                var nativePayload = ProjectFileStore.Serialize(__1);
                var hasHighLevelSave = false;
                var matches = new List<EditorSession.SaveReceipt>();
                if (GuiPending.TryGetValue(session, out var gui))
                {
                    hasHighLevelSave = true;
                    if (string.Equals(gui.NativeSnapshot, nativePayload, StringComparison.Ordinal)) matches.Add(gui.Receipt);
                }
                foreach (var pending in Pending.Values)
                {
                    if (!ReferenceEquals(pending.Receipt.Owner, session)) continue;
                    hasHighLevelSave = true;
                    if (string.Equals(pending.NativeSnapshot, nativePayload, StringComparison.Ordinal)) matches.Add(pending.Receipt);
                }
                if (matches.Count > 0)
                {
                    // 同一剧情内容可能对应多次并发分组保存，选择已冻结的最大顺序号，不能临时重编号。
                    __state = matches.OrderByDescending(receipt => receipt.Sequence).First() with { Path = __0 };
                    return;
                }
                if (hasHighLevelSave) return; // 剧情不匹配时不能借用该会话当前的组状态。
                if (Environment.CurrentManagedThreadId != session.UnityThreadId) return;
                if (string.Equals(ProjectFileStore.Serialize(session.Editor.CaptureProject()), nativePayload, StringComparison.Ordinal))
                    __state = session.FreezeSave(__0);
            }
            catch (Exception error) { LogHookError("project save start", error); }
        }
        static void Postfix(string __result, EditorSession.SaveReceipt? __state)
        {
            // 后台写入结束时可能已切换工程，甚至同一路径已被再次保存。
            // 只用本次原生返回的版本标识本次写入内容。
            if (__state != null) TryHook("project save completion", () => AcknowledgeNativeSave(__state, __result));
        }
    }

    [HarmonyPatch(typeof(AuthoringEditorSession), nameof(AuthoringEditorSession.BeginSave))]
    private static class SaveStart
    {
        static void Prefix(out EditorSession.SaveReceipt? __state)
        {
            __state = null;
            try
            {
                var session = GroupHost.Session;
                if (session != null && !session.IsDisposed)
                    __state = session.FreezeSave(PersistentData.projectPath ?? "");
            }
            catch (Exception error) { LogHookError("authoring save start", error); }
        }
        static void Postfix(AuthoringSaveOperation __result, EditorSession.SaveReceipt? __state)
        {
            TryHook("authoring save registration", () =>
            {
                if (__result == null || __state == null || __state.Owner.IsDisposed) return;
                var pointer = __result.Pointer;
                var pending = new PendingReceipt(__state, ProjectFileStore.Serialize(__result.Snapshot));
                Pending[pointer] = pending;
                // Dispose 可能发生在首次检查与插入之间；插入后再次检查，避免销毁会话残留迟到任务。
                if (__state.Owner.IsDisposed) RemoveMatching(pointer, pending);
            });
        }
    }
    /// <summary>同一次高层保存请求冻结的原生剧情与分组回执。</summary>
    private sealed record PendingReceipt(EditorSession.SaveReceipt Receipt, string NativeSnapshot);
    private static readonly ConcurrentDictionary<IntPtr, PendingReceipt> Pending = new();
    private sealed record CompletionReceipt(EditorSession.SaveReceipt Receipt, string? NativeVersion);
    private sealed record GuiSaveReceipt(long Sequence, EditorSession.SaveReceipt Receipt,
        string NativeSnapshot, bool CompletionObserved = false, int PollAttempts = 0, bool WaitingLogged = false);
    private static readonly ConcurrentDictionary<EditorSession, GuiSaveReceipt> GuiPending = new();
    private static long guiSaveSequence;

    /// <summary>
    /// GUI 在没有 AuthoringEditorSession.Current 时也可保存，因此调用前需同时冻结剧情与分组。
    /// 原生 void 保存方法返回不代表写入完成，需等待原生成功通知。
    /// </summary>
    [HarmonyPatch(typeof(StudioCommon), nameof(StudioCommon.Save))]
    private static class GuiSaveStart
    {
        static void Prefix(StudioCommon __instance, out GuiSaveReceipt? __state)
        {
            __state = null;
            try
            {
                var owner = GroupHost.Session;
                if (owner == null || owner.IsDisposed || owner.Editor.Pointer != __instance.Pointer) return;
                var snapshot = ProjectFileStore.Serialize(__instance.CaptureProject());
                __state = new GuiSaveReceipt(Interlocked.Increment(ref guiSaveSequence),
                    owner.FreezeSave(PersistentData.projectPath ?? ""), snapshot);
                GuiPending[owner] = __state;
                LogSaveDiagnostic($"GUI save frozen: seq={__state.Receipt.Sequence}; path={__state.Receipt.Path}; owner={owner.ProjectId}; " +
                    $"authoringSession={AuthoringEditorSession.Current != null}; initializationError={owner.Editor.sessionInitializationError}");
            }
            catch (Exception error) { LogHookError("GUI save snapshot", error); }
        }

        static Exception? Finalizer(Exception? __exception, GuiSaveReceipt? __state)
        {
            if (__exception != null && __state != null) RemoveGuiMatching(__state);
            return __exception; // 保持原生错误处理和异常语义。
        }
    }

    /// <summary>
    /// 原生历史在此通知 GUI 写入成功；路径可能随后才更新，因此文件校验延后到 Tick。
    /// </summary>
    [HarmonyPatch(typeof(OperationManager), nameof(OperationManager.OnSave))]
    private static class GuiSaveEnd
    {
        static void Postfix() => TryHook("GUI save success notification", () =>
        {
            var owner = GroupHost.Session;
            if (owner == null || owner.IsDisposed || !GuiPending.TryGetValue(owner, out var pending)) return;
            LogSaveDiagnostic($"GUI native OnSave observed: seq={pending.Receipt.Sequence}; owner={owner.ProjectId}; path={PersistentData.projectPath}");
            GuiPending.TryUpdate(owner, pending with { CompletionObserved = true }, pending);
        });
    }

    /// <summary>
    /// 原生 OnSave 通知后在 Unity 主线程轮询。只有剧情序列化内容完全一致、
    /// 且读取前后版本稳定时才确认回执；后来的保存不能替旧组快照证明写入成功。
    /// </summary>
    internal static void PollGuiSaves(EditorSession owner)
    {
        if (owner.IsDisposed || !ReferenceEquals(GroupHost.Session, owner) ||
            !GuiPending.TryGetValue(owner, out var pending)) return;
        if (!pending.CompletionObserved)
        {
            if (!pending.WaitingLogged)
            {
                TryHook("GUI completion diagnostic", () => LogSaveDiagnostic(
                    $"GUI waiting for native completion: seq={pending.Receipt.Sequence}; path={PersistentData.projectPath}; " +
                    $"owner={owner.ProjectId}; coreDirty={OperationManager.HasChangesSinceLastSave}; " +
                    $"authoringSession={AuthoringEditorSession.Current != null}; initializationError={owner.Editor.sessionInitializationError}"));
                GuiPending.TryUpdate(owner, pending with { WaitingLogged = true }, pending);
            }
            return;
        }

        Exception? failure = null;
        try
        {
            var path = PersistentData.projectPath;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                var version = ProjectFileStore.Version(path);
                var read = ProjectFileStore.Read(path);
                var serialized = ProjectFileStore.Serialize(read.Project);
                var versionAfterRead = ProjectFileStore.Version(path);
                if (pending.PollAttempts == 0)
                    LogSaveDiagnostic($"GUI completion first poll: seq={pending.Receipt.Sequence}; path={path}; " +
                        $"nativeVersionBefore={version}; nativeVersionAfter={versionAfterRead}; " +
                        $"readProjectId={read.Project.ProjectId}; ownerId={pending.Receipt.ProjectId}; " +
                        $"sameSerialize={string.Equals(serialized, pending.NativeSnapshot, StringComparison.Ordinal)}; " +
                        $"sameVersion={string.Equals(versionAfterRead, version, StringComparison.Ordinal)}");
                if (pending.PollAttempts == 0 && !string.Equals(serialized, pending.NativeSnapshot, StringComparison.Ordinal))
                {
                    Plugin.Logger.LogWarning($"GUI saved payload differs: {pending.NativeSnapshot.Length} -> {serialized.Length}, path={path}");
#if NODEGROUPS_RUNTIME_TESTS
                    var report = Environment.GetEnvironmentVariable("NODEGROUPS_TEST_REPORT");
                    if (!string.IsNullOrWhiteSpace(report))
                    {
                        File.WriteAllText(Path.Combine(Path.GetDirectoryName(report)!, "gui-frozen.txt"), pending.NativeSnapshot);
                        File.WriteAllText(Path.Combine(Path.GetDirectoryName(report)!, "gui-saved.txt"), serialized);
                    }
#endif
                }
                if (read.Project.ProjectId == pending.Receipt.ProjectId &&
                    string.Equals(serialized, pending.NativeSnapshot, StringComparison.Ordinal) &&
                    string.Equals(versionAfterRead, version, StringComparison.Ordinal))
                {
                    // 确认前先移除待办，每个手势最多写一个侧车；随后还会在哈希计算前后复核版本。
                    if (RemoveGuiMatching(pending))
                        AcknowledgeNativeSave(pending.Receipt with { Path = path }, version);
                    return;
                }
            }
        }
        catch (Exception error)
        {
            failure = error;
            if (pending.PollAttempts == 0) LogSaveDiagnostic($"GUI first poll failed: seq={pending.Receipt.Sequence}; path={PersistentData.projectPath}; {error}");
        }

        // 成功通知可能早于路径赋值，或遇到临时文件锁。允许有限次主线程重试，
        // 但不能无限保留未验证通知，更不能把它绑定到无关保存。
        if (pending.PollAttempts >= 20)
        {
            if (RemoveGuiMatching(pending))
            {
                LogSaveDiagnostic($"GUI acknowledgement rejected after {pending.PollAttempts + 1} polls: seq={pending.Receipt.Sequence}; path={PersistentData.projectPath}");
                owner.Saved(pending.Receipt with { SavedHash = null, SavedVersion = null });
                if (failure != null) LogHookError("GUI saved snapshot verification", failure);
            }
        }
        else GuiPending.TryUpdate(owner, pending with { PollAttempts = pending.PollAttempts + 1 }, pending);
    }

    private static bool RemoveGuiMatching(GuiSaveReceipt receipt) =>
        ((ICollection<KeyValuePair<EditorSession, GuiSaveReceipt>>)GuiPending).Remove(new(receipt.Receipt.Owner, receipt));

    /// <summary>释放指定会话及已销毁会话的待办，覆盖与场景拆除并发到达的回调。</summary>
    internal static void ClearPending(EditorSession owner)
    {
        foreach (var pair in Pending)
            if (ReferenceEquals(pair.Value.Receipt.Owner, owner) || pair.Value.Receipt.Owner.IsDisposed)
                RemoveMatching(pair.Key, pair.Value);
        GuiPending.TryRemove(owner, out _);
    }

    private static void RemoveMatching(IntPtr pointer, PendingReceipt receipt) =>
        ((ICollection<KeyValuePair<IntPtr, PendingReceipt>>)Pending).Remove(new(pointer, receipt));
    [HarmonyPatch(typeof(AuthoringEditorSession), nameof(AuthoringEditorSession.CompleteSave))]
    private static class SaveEnd
    {
        static void Prefix(AuthoringSaveOperation __0, out CompletionReceipt? __state)
        {
            __state = null;
            try
            {
                if (__0 == null || !Pending.TryGetValue(__0.Pointer, out var pending)) return;
                // FileVersion 属于原生准备写入的确切内容，在 CompleteSave 释放 candidate 前捕获。
                // 不能猜测 legacyBaseline 后重序列化 Snapshot，否则版本字段与实际字节可能不同。
                __state = new(pending.Receipt, __0.candidate?.FileVersion);
            }
            catch (Exception error) { LogHookError("authoring save version capture", error); }
        }

        static void Postfix(AuthoringSaveOperation __0, CompletionReceipt? __state)
        {
            TryHook("authoring save completion", () =>
            {
                if (__0 == null || !Pending.TryRemove(__0.Pointer, out var pending)) return;
                var snapshot = pending.Receipt;
                if (__0.IsCompleted && __0.Error == null)
                {
                    var version = __state != null && ReferenceEquals(__state.Receipt, snapshot) ? __state.NativeVersion : null;
                    AcknowledgeNativeSave(snapshot with { Path = __0.FilePath }, version);
                }
            });
        }
    }

    /// <summary>只有文件读取前后的版本均匹配原生确认版本，才冻结哈希并确认保存。</summary>
    private static void AcknowledgeNativeSave(EditorSession.SaveReceipt receipt, string? nativeVersion)
    {
        if (receipt.Owner.IsDisposed) return;
        try
        {
            if (string.IsNullOrEmpty(nativeVersion))
            {
                LogSaveDiagnostic($"Save acknowledgement rejected: native version missing; seq={receipt.Sequence}; path={receipt.Path}; owner={receipt.ProjectId}");
                receipt.Owner.Saved(receipt with { SavedHash = null, SavedVersion = null }); // 保留未保存状态。
                return;
            }
            if (!File.Exists(receipt.Path))
            {
                LogSaveDiagnostic($"Save acknowledgement rejected: destination missing; seq={receipt.Sequence}; path={receipt.Path}; expectedVersion={nativeVersion}");
                receipt.Owner.Saved(receipt with { SavedHash = null, SavedVersion = null });
                return;
            }
            var actualVersion = ProjectFileStore.Version(receipt.Path);
            if (!string.Equals(actualVersion, nativeVersion, StringComparison.Ordinal))
            {
                LogSaveDiagnostic($"Save acknowledgement rejected: native version mismatch; seq={receipt.Sequence}; path={receipt.Path}; " +
                    $"expectedVersion={nativeVersion}; actualVersion={actualVersion}; physicalVersion={ProjectFileStore.PhysicalVersion(receipt.Path)}");
                receipt.Owner.Saved(receipt with { SavedHash = null, SavedVersion = null });
                return;
            }
            var hash = EditorSession.Hash(receipt.Path);
            var afterHashVersion = ProjectFileStore.Version(receipt.Path);
            if (!string.Equals(afterHashVersion, nativeVersion, StringComparison.Ordinal))
            {
                LogSaveDiagnostic($"Save acknowledgement rejected: version changed while hashing; seq={receipt.Sequence}; path={receipt.Path}; " +
                    $"expectedVersion={nativeVersion}; afterHashVersion={afterHashVersion}");
                receipt.Owner.Saved(receipt with { SavedHash = null, SavedVersion = null });
                return;
            }
            LogSaveDiagnostic($"Save acknowledgement verified: seq={receipt.Sequence}; path={receipt.Path}; nativeVersion={nativeVersion}; hash={hash}");
            receipt.Owner.Saved(receipt with { SavedHash = hash, SavedVersion = nativeVersion });
        }
        catch (Exception error)
        {
            LogSaveDiagnostic($"Save acknowledgement failed: seq={receipt.Sequence}; path={receipt.Path}; {error}");
            receipt.Owner.Saved(receipt with { SavedHash = null, SavedVersion = null }); // 先保留未保存状态，再由隔离边界记录异常。
            throw;
        }
    }

    private static void Detach(EditorSession session)
    {
        // 先清除全局会话引用，即使 Unity 失效对象在清理时抛错，原生流程也不会再找到半销毁会话。
        if (ReferenceEquals(GroupHost.Session, session)) GroupHost.Session = null;
        try { session.Dispose(); }
        finally { ClearPending(session); }
    }

    private static void TryHook(string activity, Action action)
    {
        try { action(); }
        catch (Exception error) { LogHookError(activity, error); }
    }

    private static void LogHookError(string activity, Exception error)
    {
        try { Plugin.Logger.LogError($"NodeGroups hook failed ({activity}); native editor continues: {error}"); }
        catch { }
    }

    private static void LogSaveDiagnostic(string message)
    {
        // 隔离测试进程显示详细时序与身份诊断；日常运行仅记 Debug，避免把正常等待当作警告。
        try
        {
#if NODEGROUPS_RUNTIME_TESTS
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NODEGROUPS_TEST_REPORT")))
                Plugin.Logger.LogInfo(message);
            else
#endif
                Plugin.Logger.LogDebug(message);
        }
        catch { }
    }
}
