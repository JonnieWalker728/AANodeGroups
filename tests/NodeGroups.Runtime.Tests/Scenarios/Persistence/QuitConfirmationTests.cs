extern alias Il2Cpp;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnityEngine;
using NativeQuitHandler = Il2Cpp::Il2CppSystem.Func<bool>;

namespace AzureArchive.NodeGroups.Runtime.Tests;

/// <summary>
/// 只在独立 QuitConfirm 进程末尾验证真实“仍然退出”。不替换 Quit 或关闭判断；
/// 关闭前回调记录最后批准；外层必须再确认同一进程正常退出，才将专项记为通过。
/// 当前应用裁剪了 quitting 事件的添加方法，因此不依赖该不可用接口。
/// </summary>
internal static class QuitConfirmationTests
{
    private enum Phase { AwaitVeto, AwaitButton, AwaitProcessExit }
    private sealed class State
    {
        internal EditorSession Owner = null!;
        internal Action<bool, string> Check = null!;
        internal Action<bool, string?> Publish = null!;
        internal Phase Phase;
        internal DateTime Deadline;
        internal int RequestedAt, ConfirmedAt;
        internal bool VetoObserved, ConfirmClicked, ApprovedQueryObserved;
        internal string MarkerPath = "", RecoveryPath = "", GroupId = "", DllHash = "";
        internal Exception? ObserverFailure;
        internal UI.UIPopupSystem? Popup;
        internal float OpenedAt;
        internal NativeQuitHandler? QuitObserver;
    }
    private static State? active;

    /// <summary>建立未保存空组并发起真实关闭；调用者不得在本专项结束前销毁编辑器会话。</summary>
    internal static void Begin(EditorSession owner, Action<bool, string> check, Action<bool, string?> publish, string report)
    {
        if (active != null || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NODEGROUPS_TEST_REPORT")))
            throw new InvalidOperationException("Quit confirmation requires one explicitly isolated test process.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(report))!;
        var project = Path.GetFullPath(PersistentData.projectPath);
        if (!project.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Quit confirmation cannot use a user project.");
        var state = new State
        {
            Owner = owner, Check = check, Publish = publish, MarkerPath = Path.Combine(directory, "quit-confirmation.json"),
            RecoveryPath = Path.Combine(BepInEx.Paths.GameRootPath, "ModDev", "NodeGroups", "artifacts", "recovery",
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner.ProjectId))) + ".project.nodegroups.json"),
            DllHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Plugin).Assembly.Location))),
            Deadline = DateTime.UtcNow.AddSeconds(20)
        };
        if (File.Exists(state.RecoveryPath) || File.Exists(state.MarkerPath))
            throw new InvalidOperationException("Quit confirmation test recovery or marker already exists.");
        active = state;
        check(!owner.Dirty, "quit-confirmation fixture starts from saved groups");
        owner.CreateGroup(true); state.GroupId = owner.SelectedGroup!;
        check(owner.Dirty && owner.Workspace.Snapshot.GetGroup(state.GroupId).MemberNodeIds.Count == 0,
            "quit-confirmation uses a genuinely unsaved empty group");
        // 后注册观察者，既不改变返回值，也不清空原应用或产品订阅。
        // Unity 可能在第一个拒绝者处短路，因此只要求观察到确认后的最终放行。
        state.QuitObserver = (Func<bool>)(() => ObserveApprovedQuery(state));
        Application.add_wantsToQuit(state.QuitObserver);
        WriteMarker(state, false, "Awaiting the actual native quit confirmation flow.");
        state.RequestedAt = Time.frameCount; state.Phase = Phase.AwaitVeto;
        Application.Quit();
    }

    /// <summary>等待真实关闭被拦截、点击原生确认按钮，再由产品下一帧退出；本方法不自行发布成功。</summary>
    internal static void Tick(EditorSession owner)
    {
        var state = active ?? throw new InvalidOperationException("Quit confirmation was not started.");
        if (DateTime.UtcNow > state.Deadline) throw new TimeoutException("Quit confirmation timed out in " + state.Phase);
        if (state.ObserverFailure != null) throw new InvalidOperationException("Quit confirmation observer failed.", state.ObserverFailure);
        if (!ReferenceEquals(owner, state.Owner) || owner.IsDisposed) throw new InvalidOperationException("Quit confirmation lost its editor owner.");
        if (state.Phase == Phase.AwaitVeto)
        {
            if (Time.frameCount <= state.RequestedAt + 2) return;
            var windows = Popups();
            state.Check(windows.Length == 1 && owner.Dirty,
                "the first real Application.Quit is vetoed and opens one native unsaved-group confirmation");
            state.VetoObserved = true; state.Popup = windows[0]; state.OpenedAt = Time.unscaledTime;
            state.Phase = Phase.AwaitButton;
        }
        else if (state.Phase == Phase.AwaitButton && Time.unscaledTime - state.OpenedAt >= .5f)
        {
            state.Check(state.Popup != null && state.Popup.gameObject.activeInHierarchy,
                "quit-confirmation native popup stays alive before its real confirmation click");
            var buttons = state.Popup!.buttonInfoList.ToArray().Single(info => info.type == UI.UIPopupSystem.ButtonType.OkCancelButton);
            state.ConfirmClicked = true; state.ConfirmedAt = Time.frameCount; state.Phase = Phase.AwaitProcessExit;
            buttons.btnA.SimulateClick();
        }
    }

    private static bool ObserveApprovedQuery(State state)
    {
        if (!ReferenceEquals(active, state)) return true;
        // 某些 Unity 版本会枚举全部观察者后再合并结果；首轮未确认时仅旁观，绝不把它误记为批准。
        if (!state.ConfirmClicked) return true;
        try
        {
            state.Check(state.VetoObserved && state.ConfirmClicked && Time.frameCount > state.ConfirmedAt,
                "the confirmed real quit request reaches downstream observers only after the native button callback");
            state.Check(!state.Owner.IsDisposed && ReferenceEquals(GroupHost.Session, state.Owner) && state.Owner.Dirty,
                "quit confirmation grants one close while preserving the unsaved owner until normal application teardown");
            state.ApprovedQueryObserved = true;
            // 子进程只证明批准；父脚本还必须等该 PID 正常退出，不能仅凭这份报告提前通过。
            WriteMarker(state, false, "The approved native query was observed; awaiting parent verification of process exit.");
            state.Publish(true, null);
            return true;
        }
        catch (Exception error)
        {
            WriteMarker(state, false, error.ToString()); state.ObserverFailure = error;
            return false;
        }
    }

    private static UI.UIPopupSystem[] Popups() => UnityEngine.Object.FindObjectsOfType<UI.UIPopupSystem>(true)
        .Where(popup => popup.gameObject.activeInHierarchy && popup.titleLabel != null && popup.titleLabel.text == "节点组尚未保存").ToArray();

    private static void WriteMarker(State state, bool success, string? error) => File.WriteAllText(state.MarkerPath,
        JsonSerializer.Serialize(new
        {
            success, error, projectId = state.Owner.ProjectId, processId = Environment.ProcessId,
            pluginVersion = Plugin.Version, dllSha256 = state.DllHash,
            state.VetoObserved, state.ConfirmClicked, state.ApprovedQueryObserved,
            processExitRequired = true, recoveryInitiallyAbsent = true, recoveryPath = state.RecoveryPath, groupId = state.GroupId
        }, new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>失败仅移除测试自己注册的观察者；正常确认退出交给应用生命周期完成。</summary>
    internal static void Abort()
    {
        var state = active; active = null; if (state == null) return;
        if (state.QuitObserver != null) Application.remove_wantsToQuit(state.QuitObserver);
    }
}
