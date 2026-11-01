extern alias Il2Cpp;

using UnityEngine;
using NativeAction = Il2Cpp::Il2CppSystem.Action;

namespace AzureArchive.NodeGroups.Runtime;

/// <summary>
/// 当前编辑器会话的一次性恢复确认。只借用原生 PopupManager 创建窗口，
/// 不克隆编辑器对象，也不改变恢复文件或原生剧情。
/// </summary>
internal sealed class RecoveryPrompt : IDisposable
{
    private readonly EditorSession owner;
    private GameObject? popup;
    // 保持委托包装器存活到窗口关闭；原生按钮回调可能晚于会话退出到达。
    private NativeAction? confirm;
    private NativeAction? cancel;
    private bool offered;
    private bool completed;
    private bool disposed;

    internal RecoveryPrompt(EditorSession owner) => this.owner = owner;

    /// <summary>原生窗口当前可见时阻断 Mod 快捷键与创建按钮输入。</summary>
    internal bool IsOpen => popup != null && popup.activeInHierarchy;

    /// <summary>等编辑器和原生弹窗管理器就绪后再提示；同一会话最多展示一次。</summary>
    /// <param name="canOpen">调用方尚无编辑、拖动或其他局部弹出操作。</param>
    internal void Tick(bool canOpen)
    {
        if (disposed) return;
        if (offered)
        {
            // 外部关闭或销毁也视为本次已处理，不能因每帧刷新而反复弹出。
            if (!IsOpen && !completed) Complete(false);
            return;
        }
        if (!canOpen || !OwnsSession() || !owner.RecoveryAvailable) return;
        var manager = PopupManager.Instance;
        if (manager == null || manager.systemPopupPrefab == null) return;
        // 不覆盖应用已经显示的导入、保存等原生弹窗，等待其自然结束。
        if (UnityEngine.Object.FindObjectsOfType<UI.UIPopup>(true).Any(window => window.gameObject.activeInHierarchy)) return;
        offered = true;
        try
        {
            confirm = (Action)(() => Complete(true, nativeAlreadyClosing: true));
            cancel = (Action)(() => Complete(false, nativeAlreadyClosing: true));
            var instruction = manager.ShowPopupOkCancel("恢复未保存的节点组",
                "检测到未保存的节点组改动，是否恢复？\n仅恢复节点组信息，不修改节点和剧情内容。恢复后请保存工程。",
                "恢复", confirm, "暂不恢复", cancel);
            popup = instruction.popup;
        }
        catch (Exception error)
        {
            completed = true;
            Close();
            Plugin.Logger.LogError("节点组恢复提示未能打开；恢复文件已保留：" + error);
        }
    }

    /// <summary>按钮及关闭回调只作用于创建窗口时的会话；先关窗再导入，避免重入。</summary>
    private void Complete(bool restore, bool nativeAlreadyClosing = false)
    {
        if (completed || disposed) return;
        completed = true;
        var mayRestore = restore && OwnsSession();
        // 原生 Initialize 已将传入的按钮动作与 HidePopup 组合。
        // 此处仅立即停用并释放引用，避免重复触发原生 onClose；后续原生委托负责销毁。
        Close(nativeAlreadyClosing);
        if (mayRestore) owner.RestoreRecovery();
    }

    private bool OwnsSession() => !owner.IsDisposed && ReferenceEquals(GroupHost.Session, owner);

    /// <summary>只关闭本对象拥有的窗口，不使用关闭全部原生弹窗的全局操作。</summary>
    private void Close(bool nativeAlreadyClosing = false)
    {
        var window = popup;
        popup = null;
        confirm = null;
        cancel = null;
        if (window == null) return;
        // Destroy 可能延至帧末；立即停用，避免旧场景仍留下可点击的确认窗口。
        window.SetActive(false);
        if (nativeAlreadyClosing) return;
        var native = window.GetComponent<UI.UIPopup>();
        if (native != null) native.HidePopup();
        else UnityEngine.Object.Destroy(window);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        completed = true;
        Close();
    }
}
