using HarmonyLib;
using Studio.Scripts;
using Studio.Scripts.OperationManagement;

namespace AzureArchive.NodeGroups.Runtime;

/// <summary>将组元数据的未保存状态接入原生保存提示，不改变原生历史、剧情或编译标记。</summary>
internal static class NativeSaveState
{
    [ThreadStatic] private static int nativeReadDepth;

    /// <summary>读取原应用自身的保存判断，供已有 Ctrl+S 兜底区分原生改动与仅分组改动。</summary>
    /// <remarks>使用当前线程的临时旁路，不能全局关闭补丁或清空原生保存计数。</remarks>
    internal static bool OriginalDirty
    {
        get
        {
            nativeReadDepth++;
            try { return OperationManager.HasChangesSinceLastSave; }
            finally { nativeReadDepth--; }
        }
    }

    private static EditorSession? Current
    {
        get
        {
            var owner = GroupHost.Session;
            if (owner == null || owner.IsDisposed || Environment.CurrentManagedThreadId != owner.UnityThreadId) return null;
            var editor = StudioCommon.instance;
            if (editor == null || editor.Pointer != owner.Editor.Pointer) return null;
            owner.EnsureActiveSaveState();
            return owner;
        }
    }

    /// <summary>在已提交状态或保存结果改变后刷新原生按钮；尚未附着的构造期返回 false，供后续重试。</summary>
    internal static bool Refresh(EditorSession owner)
    {
        if (!ReferenceEquals(Current, owner) || OperationManager.Instance == null) return false;
        try { OperationManager.UpdateCompileSaveBtn(); return true; }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning("节点组保存提示刷新失败，将在后续刷新重试：" + error.Message);
            return false;
        }
    }

    /// <summary>原生退出入口读取这个属性；只补充 true，绝不清除原应用自己的未保存状态。</summary>
    [HarmonyPatch(typeof(OperationManager), nameof(OperationManager.HasChangesSinceLastSave), MethodType.Getter)]
    private static class UnsavedChanges
    {
        static void Postfix(ref bool __result)
        {
            if (!__result && nativeReadDepth == 0 && Current?.Dirty == true) __result = true;
        }
    }

    /// <summary>
    /// 当前原生按钮函数内联了原有保存判断，单独补丁 getter 不能改变其颜色。
    /// 先让原应用完成刷新，再仅补充未保存分组的保存按钮颜色；编译按钮保持原生结果。
    /// </summary>
    [HarmonyPatch(typeof(OperationManager), nameof(OperationManager.UpdateCompileSaveBtn))]
    private static class SaveIndicator
    {
        static void Postfix()
        {
            var owner = Current;
            if (owner?.Dirty == true && owner.Editor.saveBtn != null)
                owner.Editor.saveBtn.color = OperationManager.enabledColor;
        }
    }
}
