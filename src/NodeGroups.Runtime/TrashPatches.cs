using HarmonyLib;
using NodeGroups.Core;
using Studio.Scripts;
using Studio.Scripts.Nodes;
using Studio.Scripts.OperationManagement;

namespace AzureArchive.NodeGroups.Runtime;

/// <summary>
/// 观察原生回收和回放的完成边界。保留原生暂存对象、GUID、历史与异常，
/// 只附着分组快照并在完整操作结束后更新有效成员显示。
/// </summary>
internal static class TrashPatches
{
    private sealed record Receipt(EditorSession Owner, string NodeId, WorkspaceSnapshot Before, int HistoryCount, IntPtr PreviousTop);

    [HarmonyPatch(typeof(Trashcan), nameof(Trashcan.MoveToTrash))]
    private static class Recycle
    {
        static void Prefix(Node __0, out Receipt? __state)
        {
            __state = null;
            try
            {
                var owner = GroupHost.Session;
                if (NativeHistory.Applying || owner == null || owner.IsDisposed || __0 == null || owner.Workspace.Snapshot.Groups.Count == 0) return;
                var id = EditorSession.Id(__0);
                if (owner.Find(id)?.Pointer != __0.Pointer) return;
                __state = new Receipt(owner, id, owner.CaptureTrashBefore(), OperationManager.history.Count,
                    OperationManager.history.Count == 0 ? IntPtr.Zero : OperationManager.history.Peek().Pointer);
            }
            catch (Exception error) { Log("capture", error); }
        }

        static void Postfix(Receipt? __state)
        {
            if (__state == null) return;
            Try("completed recycle", () =>
            {
                var owner = __state.Owner;
                if (owner.IsDisposed || !ReferenceEquals(GroupHost.Session, owner) || owner.Find(__state.NodeId) != null ||
                    OperationManager.history.Count <= __state.HistoryCount) return;
                var operation = OperationManager.history.Peek().TryCast<NodeMoveToTrashOperation>();
                if (operation == null || operation.Pointer == __state.PreviousTop || operation.guid.ToString() != __state.NodeId) return;
                owner.RecordTrashed(operation, __state.Before);
            });
        }
    }

    // 仅补丁这四个具有独立实现的具体方法；不补丁可能被 IL2CPP 折叠的构造函数或基类虚方法。
    [HarmonyPatch(typeof(NodeMoveToTrashOperation), nameof(NodeMoveToTrashOperation.Undo))]
    private static class RecycleUndo
    {
        static void Prefix(out bool __state) => Enter(out __state);
        static void Postfix(NodeMoveToTrashOperation __instance) => Restore(__instance, false);
        static Exception? Finalizer(Exception? __exception, bool __state) => Leave(__exception, __state);
    }

    [HarmonyPatch(typeof(NodeMoveToTrashOperation), nameof(NodeMoveToTrashOperation.Redo))]
    private static class RecycleRedo
    {
        static void Prefix(out bool __state) => Enter(out __state);
        static void Postfix(NodeMoveToTrashOperation __instance) => Restore(__instance, true);
        static Exception? Finalizer(Exception? __exception, bool __state) => Leave(__exception, __state);
    }

    [HarmonyPatch(typeof(NodeMoveOutTrashOperation), nameof(NodeMoveOutTrashOperation.Undo))]
    private static class ReturnUndo
    {
        static void Prefix(out bool __state) => Enter(out __state);
        static void Postfix(NodeMoveOutTrashOperation __instance) => Restore(__instance, false);
        static Exception? Finalizer(Exception? __exception, bool __state) => Leave(__exception, __state);
    }

    [HarmonyPatch(typeof(NodeMoveOutTrashOperation), nameof(NodeMoveOutTrashOperation.Redo))]
    private static class ReturnRedo
    {
        static void Prefix(out bool __state) => Enter(out __state);
        static void Postfix(NodeMoveOutTrashOperation __instance) => Restore(__instance, true);
        static Exception? Finalizer(Exception? __exception, bool __state) => Leave(__exception, __state);
    }

    private static void Enter(out bool previous) { previous = NativeHistory.Applying; NativeHistory.Applying = true; }
    private static Exception? Leave(Exception? error, bool previous) { NativeHistory.Applying = previous; return error; }
    private static void Restore(Operation operation, bool redo) => Try("replay metadata", () => NativeHistory.RestoreNative(operation, redo));

    [HarmonyPatch(typeof(OperationManager), nameof(OperationManager.Undo))]
    private static class UndoComplete { static void Postfix() => Refresh(); }
    [HarmonyPatch(typeof(OperationManager), nameof(OperationManager.Redo))]
    private static class RedoComplete { static void Postfix() => Refresh(); }

    private static void Refresh() => Try("refresh registration", () =>
    {
        // 原生链接历史在 Working=true 时逐项执行；等待整次 Undo/Redo 完成，不能用中间位置扩边框。
        if (!NativeHistory.Applying && !OperationManager.Working) GroupHost.Session?.RefreshNativeRegistration();
    });

    private static void Try(string activity, Action action)
    { try { action(); } catch (Exception error) { Log(activity, error); } }
    private static void Log(string activity, Exception error)
    { try { Plugin.Logger.LogError($"回收站分组适配失败 ({activity})；原生流程继续：{error}"); } catch { } }
}
