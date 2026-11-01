extern alias Il2Cpp;

using UnityEngine;
using NativeAction = Il2Cpp::Il2CppSystem.Action;
using NativeQuitHandler = Il2Cpp::Il2CppSystem.Func<bool>;

namespace AzureArchive.NodeGroups.Runtime;

/// <summary>
/// 在正常应用关闭前保护当前会话尚未保存的组信息。
/// 原生节点自己的关闭判断保持不变；此对象只补充 Mod 元数据的确认，并借用原生弹窗样式。
/// </summary>
internal sealed class UnsavedQuitPrompt : IDisposable
{
    private readonly EditorSession owner;
    private NativeQuitHandler? quitHandler;
    private GameObject? popup;
    // 原生窗口及事件持有自己的委托；本对象也保持包装器存活，直到对应窗口或订阅结束。
    private NativeAction? confirm;
    private NativeAction? cancel;
    private int promptSequence;
    private int pendingQuitFrame = -1;
    private int allowedQuitThroughFrame = -1;
    private bool allowNextQuit;
    private bool disposed;

    /// <summary>创建绑定具体编辑器会话的保护器；调用 Attach 后才接收应用关闭事件。</summary>
    internal UnsavedQuitPrompt(EditorSession owner) => this.owner = owner ?? throw new ArgumentNullException(nameof(owner));

    /// <summary>提示显示期间及确认后的延迟退出期间，均阻止 Mod 的底层编辑输入。</summary>
    internal bool IsOpen => pendingQuitFrame >= 0 || popup != null && popup.activeInHierarchy;

    /// <summary>在 Unity 主线程订阅正常关闭事件；重复调用不会增加重复订阅。</summary>
    /// <remarks>释放时仅移除本对象的委托，不能清空其他插件或原应用的关闭事件。</remarks>
    internal void Attach()
    {
        if (disposed) throw new ObjectDisposedException(nameof(UnsavedQuitPrompt));
        if (quitHandler != null) return;
        quitHandler = (Func<bool>)RequestQuit;
        try { Application.add_wantsToQuit(quitHandler); }
        catch { quitHandler = null; throw; }
    }

    /// <summary>应用请求关闭时返回是否放行；仅阻止当前会话未保存的分组，不改变保存状态。</summary>
    /// <returns>没有本会话未保存组，或消费当前会话的一次有效退出确认时为 true；否则为 false。</returns>
    /// <remarks>由 Unity 关闭前回调调用；错误必须留在 Mod 内，不能泄漏到原生事件分发。</remarks>
    internal bool RequestQuit()
    {
        try
        {
            if (!OwnsSession()) return true;
            owner.EnsureActiveSaveState();
            if (allowNextQuit)
            {
                var allowed = Time.frameCount <= allowedQuitThroughFrame;
                allowNextQuit = false;
                allowedQuitThroughFrame = -1;
                if (allowed) return true;
            }
            if (!owner.Dirty) return true;
            if (IsOpen) return false;
            // 关闭请求不提交名称或拖动预览，也不能越过已有恢复/保存等模态窗口。
            if (owner.QuitPromptInputBlocked || owner.RecoveryPromptOpen || UICamera.inputHasFocus || UIPopupList.isOpen ||
                owner.Editor.exitPopup != null && owner.Editor.exitPopup.activeInHierarchy ||
                UnityEngine.Object.FindObjectsOfType<UI.UIPopup>(true).Any(window => window.gameObject.activeInHierarchy))
            {
                Plugin.Logger.LogWarning("节点组尚未保存，请先结束当前编辑或处理已打开的窗口，再尝试退出。");
                return false;
            }
            var manager = PopupManager.Instance;
            if (manager == null || manager.systemPopupPrefab == null)
            {
                Plugin.Logger.LogWarning("节点组尚未保存，暂时无法显示退出确认；请先保存后再退出。");
                return false;
            }
            Open(manager);
            return false;
        }
        catch (Exception error)
        {
            LogError("节点组退出确认失败，未保存的会话将继续保留。", error);
            return !OwnsSession() || !owner.Dirty;
        }
    }

    /// <summary>在自然帧处理中回收外部关闭的窗口，并在确认后的下一帧重新请求正常退出。</summary>
    /// <remarks>不在原生按钮的 HidePopup 回调栈内退出，以免先销毁会话再执行其余窗口回调。</remarks>
    internal void Tick()
    {
        if (disposed) return;
        if (!OwnsSession())
        {
            ResetApproval();
            Close();
            return;
        }
        if (allowNextQuit && Time.frameCount > allowedQuitThroughFrame)
        {
            // 其他关闭订阅者可能拒绝退出。过期批准不能被后来的另一轮关闭请求复用。
            allowNextQuit = false;
            allowedQuitThroughFrame = -1;
        }
        if (popup != null && !popup.activeInHierarchy)
        {
            // 关闭按钮、外部 HidePopup 或禁用窗口都等价于取消；下一次请求可以重新提示。
            promptSequence++;
            Close();
        }
        if (pendingQuitFrame < 0 || Time.frameCount <= pendingQuitFrame) return;
        pendingQuitFrame = -1;
        // 正常 Unity 退出检查在本帧或下帧执行。批准只消费一次，并且始终绑定原 owner。
        allowNextQuit = true;
        allowedQuitThroughFrame = Time.frameCount + 1;
        try { Application.Quit(); }
        catch (Exception error)
        {
            ResetApproval();
            LogError("已确认退出，但原生关闭请求失败；请重试。", error);
        }
    }

    private void Open(PopupManager manager)
    {
        var sequence = ++promptSequence;
        try
        {
            confirm = (Action)(() => Complete(sequence, leave: true));
            cancel = (Action)(() => Complete(sequence, leave: false));
            var instruction = manager.ShowPopupOkCancel("节点组尚未保存",
                "当前节点组信息尚未保存。请取消退出，并使用原生保存按钮或 Ctrl+S 保存。\n是否仍然退出应用？",
                "仍然退出", confirm, "取消", cancel);
            popup = instruction.popup;
        }
        catch
        {
            promptSequence++;
            Close();
            throw;
        }
    }

    /// <summary>原生按钮动作只处理对应序号；旧窗口的迟到确认不能关闭新窗口或另一个工程。</summary>
    private void Complete(int sequence, bool leave)
    {
        if (disposed || sequence != promptSequence || !OwnsSession()) return;
        promptSequence++;
        // 原生按钮已经组合了 HidePopup；此处只停用并释放引用，不能再次调用关闭回调。
        Close(nativeAlreadyClosing: true);
        if (leave) pendingQuitFrame = Time.frameCount;
        else ResetApproval();
    }

    private bool OwnsSession() => !disposed && !owner.IsDisposed && ReferenceEquals(GroupHost.Session, owner);

    private void ResetApproval()
    {
        pendingQuitFrame = -1;
        allowNextQuit = false;
        allowedQuitThroughFrame = -1;
    }

    /// <summary>只关闭自己创建的窗口；已被外部隐藏的窗口直接销毁，避免重复发送原生 onClose。</summary>
    private void Close(bool nativeAlreadyClosing = false)
    {
        var window = popup;
        popup = null;
        confirm = null;
        cancel = null;
        if (window == null) return;
        var wasActive = window.activeInHierarchy;
        window.SetActive(false);
        if (nativeAlreadyClosing) return;
        var native = window.GetComponent<UI.UIPopup>();
        if (native != null && wasActive) native.HidePopup();
        else UnityEngine.Object.Destroy(window);
    }

    /// <summary>移除本会话的关闭回调、清理本对象窗口，并使所有迟到按钮回调失效。</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        promptSequence++;
        ResetApproval();
        var handler = quitHandler;
        quitHandler = null;
        try { if (handler != null) Application.remove_wantsToQuit(handler); }
        finally { Close(); }
    }

    private static void LogError(string message, Exception error)
    {
        try { Plugin.Logger.LogError(message + " " + error); }
        catch { }
    }
}
