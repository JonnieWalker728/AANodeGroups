using System.Text;
using System.Text.Json;
using AzureArchive.Automation;
using HarmonyLib;
using Il2CppInterop.Runtime;
using NodeGroups.Core;
using Studio.Scripts;
using Studio.Scripts.Nodes;
using Studio.Scripts.OperationManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AzureArchive.NodeGroups.Runtime.Tests;

/// <summary>
/// 保存状态的真实编辑器回归。所有变更仅发生于报告目录中的独立工程，
/// 原生保存按钮、退出确认和关闭前回调都直接调用原实现，不替换其结果。
/// </summary>
internal static class SaveStateIntegrationTests
{
    private enum Phase { SaveMain, AwaitFixture, InitialSave, ButtonSave, PreviewSave, ConcurrentSave, CtrlSSave,
        ExitReady, ExitCancelled, QuitReady, QuitCancelled, QuitCloseReady, QuitClosed,
        FinalSave, ReopenForLateQuit, QuitAbandonReady, AwaitMain }
    private sealed class State
    {
        internal EditorSession Owner = null!;
        internal Phase Phase;
        internal DateTime Deadline;
        internal int Frames, CompileDelta;
        internal float OpenedAt, ClickedAt;
        internal string MainPath = "", MainProjectId = "", MainStory = "", MainGroups = "", MainHash = "";
        internal byte[] MainCompiled = Array.Empty<byte>(), MainSidecar = Array.Empty<byte>();
        internal NodeState[] MainNodes = Array.Empty<NodeState>();
        internal bool MainSidecarExisted;
        internal string FixturePath = "", FixtureProjectId = "", FixtureStory = "", GroupId = "", SecondId = "";
        internal string ConcurrentSavedName = "", ConcurrentLiveName = "", RecoveryPath = "";
        internal string ReloadedSavedGroups = "";
        internal byte[] FixtureCompiled = Array.Empty<byte>();
        internal NodeState[] FixtureNodes = Array.Empty<NodeState>();
        internal Rect2 PreviewSavedRegion;
        internal DateTime PreviewSidecarWriteTime;
        internal UI.UIPopupSystem? Dialog;
    }
    private static State? active;
    private static EditorSession? shortcutOwner;
    private static int shortcutSaveCalls;

    /// <summary>先保存已完成的主夹具，再打开独立工程；不借用主夹具的撤销和重做分支。</summary>
    internal static void Begin(EditorSession session, Action<bool, string> check)
    {
        var report = Environment.GetEnvironmentVariable("NODEGROUPS_TEST_REPORT");
        if (string.IsNullOrWhiteSpace(report) || active != null) throw new InvalidOperationException("Save-state tests require one isolated report process.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(report))!;
        var main = Path.GetFullPath(PersistentData.projectPath);
        if (!main.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Save-state tests cannot save a user project.");
        active = new State
        {
            Owner = session, MainPath = main, MainProjectId = session.ProjectId,
            MainStory = Story(session.Editor.CaptureProject()), MainNodes = session.ReadNodes(),
            MainCompiled = AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray(),
            FixturePath = Path.Combine(directory, "save-state-fixture" + ProjectFileStore.ProjectExtension)
        };
        check(active.MainCompiled.Length > 0, "save-state regression starts from a compilable isolated main fixture");
        SetPhase(active, Phase.SaveMain); session.Editor.Save(true);
    }

    /// <summary>跨自然帧等待原生按钮协程、保存完成和场景生命周期；返回 true 表示所有校验及还原完成。</summary>
    internal static bool Tick(EditorSession session, Action<bool, string> check)
    {
        var state = active ?? throw new InvalidOperationException("Save-state tests were not started.");
        if (DateTime.UtcNow > state.Deadline) throw new TimeoutException("Save-state tests timed out in " + state.Phase);
        if (session.IsDisposed) return false;
        state.Frames++;
        if (state.Phase == Phase.SaveMain)
        {
            if (!ReferenceEquals(state.Owner, session) || !Saved(session)) return false;
            state.MainGroups = Groups(session); state.MainHash = EditorSession.Hash(state.MainPath);
            var sidecar = Sidecar(state.MainPath); state.MainSidecarExisted = File.Exists(sidecar);
            state.MainSidecar = state.MainSidecarExisted ? File.ReadAllBytes(sidecar) : Array.Empty<byte>();
            var data = NativeProjectFixture.Build(); state.FixtureProjectId = data.ProjectId;
            state.RecoveryPath = RecoveryPath(data.ProjectId);
            if (File.Exists(state.FixturePath) || File.Exists(state.RecoveryPath)) throw new InvalidOperationException("Save-state fixture identity already exists.");
            ProjectFileStore.Save(state.FixturePath, data, null, false, null);
            SwitchScene(state, session, state.FixturePath, Phase.AwaitFixture); return false;
        }
        if (state.Phase == Phase.AwaitMain)
        {
            if (ReferenceEquals(state.Owner, session) || session.ProjectId != state.MainProjectId || state.Frames < 5) return false;
            session.Tick();
            check(Story(session.Editor.CaptureProject()) == state.MainStory && NodesEqual(session.ReadNodes(), state.MainNodes) &&
                AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray().SequenceEqual(state.MainCompiled),
                "save-state regression reopens the original main native graph, positions and compiled bytes");
            check(Groups(session) == state.MainGroups && EditorSession.Hash(state.MainPath) == state.MainHash &&
                File.Exists(Sidecar(state.MainPath)) == state.MainSidecarExisted &&
                (!state.MainSidecarExisted || File.ReadAllBytes(Sidecar(state.MainPath)).SequenceEqual(state.MainSidecar)),
                "save-state regression preserves the saved main groups and original project/sidecar bytes");
            check(!session.Dirty && OperationManager.history.Count == 0 && OperationManager.future.Count == 0,
                "save-state normal scene reload ends with clean main groups and fresh native history");
            DeleteOwnedRecovery(state); active = null; return true;
        }
        if (state.Phase == Phase.AwaitFixture)
        {
            if (ReferenceEquals(state.Owner, session) || session.ProjectId != state.FixtureProjectId || state.Frames < 5) return false;
            state.Owner = session; state.FixtureStory = Story(session.Editor.CaptureProject());
            state.FixtureNodes = session.ReadNodes(); state.FixtureCompiled = AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray();
            state.CompileDelta = OperationManager.compileDelta;
            check(session.Workspace.Snapshot.Groups.Count == 0 && OperationManager.history.Count == 0 && OperationManager.future.Count == 0,
                "save-state fixture owns a fresh empty group collection and native command history");
            // 新场景的原生 Authoring 会话也可能含载入规范化改动；先完成一次真正的原生保存建立共同基准。
            session.Editor.Save(true); SetPhase(state, Phase.InitialSave); return false;
        }
        if (state.Phase == Phase.ReopenForLateQuit)
        {
            if (ReferenceEquals(state.Owner, session) || session.ProjectId != state.FixtureProjectId || state.Frames < 5) return false;
            session.Tick(); state.Owner = session;
            check(!session.Dirty && Groups(session) == state.ReloadedSavedGroups &&
                OperationManager.history.Count == 0 && OperationManager.future.Count == 0,
                "save-state native scene reload restores the saved fixture after disposing its previous clean owner");
            session.Rename(state.GroupId, "旧会话迟到退出确认");
            check(!Application.Internal_ApplicationWantsToQuit(), "a disposed-owner regression starts with a genuinely unsaved close prompt");
            SetPhase(state, Phase.QuitAbandonReady); return false;
        }
        if (!ReferenceEquals(state.Owner, session) || session.ProjectId != state.FixtureProjectId)
            throw new InvalidOperationException("Save-state fixture changed owner unexpectedly.");
        if (state.Phase == Phase.InitialSave)
        {
            if (!Saved(session) || OperationManager.HasChangesSinceLastSave) return false;
            ExpectState(session, false, false, "initial saved fixture", check);
            Blackboard.selected.Clear(); session.CreateGroup(true); state.GroupId = session.SelectedGroup!;
            check(session.Workspace.Snapshot.GetGroup(state.GroupId).MemberNodeIds.Count == 0,
                "save-state creation fixture contains no native node members");
            ExpectState(session, true, true, "creating only an empty group", check);
            CheckNativeInvariant(state, session, check, "empty group creation");
            OperationManager.Undo(); ExpectState(session, false, false, "undoing empty creation to the saved baseline", check);
            OperationManager.Redo(); ExpectState(session, true, true, "redoing empty creation", check);
            // 真实按钮包含按压/释放协程，不能把调用返回当成写入成功。
            session.Editor.saveBtn.SimulateClick(); SetPhase(state, Phase.ButtonSave); return false;
        }
        if (state.Phase == Phase.ButtonSave)
        {
            if (!Saved(session) || LoadName(state) == null) return false;
            ExpectState(session, false, false, "saving an empty group through the native save button", check);
            CheckCommittedEdits(state, session, check);
            CheckIndependentSaveDestination(state, session, check);
            state.PreviewSavedRegion = session.Workspace.Snapshot.GetGroup(state.GroupId).ExpandedBounds!.Value;
            state.PreviewSidecarWriteTime = File.GetLastWriteTimeUtc(Sidecar(state.FixturePath));
            session.BeginResize(state.GroupId, 2);
            var region = state.PreviewSavedRegion;
            session.Workspace.SetExpandedBounds(state.GroupId, new Rect2(region.X, region.Y, region.Width + 180, region.Height));
            ExpectState(session, false, false, "an active resize preview when native save begins", check);
            var frozen = session.FreezeSave(state.FixturePath);
            check(frozen.Snapshot.GetGroup(state.GroupId).ExpandedBounds == state.PreviewSavedRegion,
                "save-state FreezeSave excludes an uncommitted resize region");
            session.Editor.Save(true); session.CancelDrag();
            ExpectState(session, false, false, "cancelling the preview immediately after native save begins", check);
            SetPhase(state, Phase.PreviewSave); return false;
        }
        if (state.Phase == Phase.PreviewSave)
        {
            if (!Saved(session) || File.GetLastWriteTimeUtc(Sidecar(state.FixturePath)) <= state.PreviewSidecarWriteTime) return false;
            using (var document = JsonDocument.Parse(File.ReadAllBytes(Sidecar(state.FixturePath))))
            {
                var group = document.RootElement.GetProperty("groups").EnumerateArray()
                    .Single(group => group.GetProperty("groupId").GetString() == state.GroupId);
                var region = group.GetProperty("expandedBounds");
                var saved = new Rect2(region.GetProperty("x").GetDouble(), region.GetProperty("y").GetDouble(),
                    region.GetProperty("width").GetDouble(), region.GetProperty("height").GetDouble());
                check(saved == state.PreviewSavedRegion && session.Workspace.Snapshot.GetGroup(state.GroupId).ExpandedBounds == state.PreviewSavedRegion,
                    "save-state native save during a cancelled preview persists only the prior committed region");
            }
            ExpectState(session, false, false, "the completed native save of a cancelled resize preview", check);
            state.ConcurrentSavedName = "保存回执 G0"; state.ConcurrentLiveName = "保存期间 G1";
            session.Rename(state.GroupId, state.ConcurrentSavedName); session.Editor.Save(true);
            session.Rename(state.GroupId, state.ConcurrentLiveName);
            SetPhase(state, Phase.ConcurrentSave); return false;
        }
        if (state.Phase == Phase.ConcurrentSave)
        {
            session.Tick();
            if (AuthoringEditorSession.Current?.SaveInProgress == true || LoadName(state) != state.ConcurrentSavedName) return false;
            ExpectState(session, true, true, "a G0 save receipt while newer G1 group edits remain live", check);
            check(session.Workspace.Snapshot.GetGroup(state.GroupId).Name == state.ConcurrentLiveName,
                "save-state old save receipt preserves the newer live group name");
            CheckFailedSave(state, session, check);
            session.Save(); session.Tick();
            ExpectState(session, false, false, "retrying the successful G1 group save", check);
            OperationManager.Undo(); ExpectState(session, true, true, "undoing away from the latest successful save", check);
            OperationManager.Redo(); ExpectState(session, false, false, "redoing back to the latest successful save", check);
            CheckNativeOnlyEdit(state, session, check);
            session.Rename(state.GroupId, "仅分组 Ctrl+S 回归");
            ExpectState(session, true, true, "group-only edits before the Mod Ctrl+S fallback", check);
            check(!UICamera.inputHasFocus && Node.activeNode == null,
                "the group-only Ctrl+S fallback fixture has no active native node or input focus");
            InvokeGroupOnlyShortcut(session, check);
            SetPhase(state, Phase.CtrlSSave); return false;
        }
        if (state.Phase == Phase.CtrlSSave)
        {
            if (!Saved(session) || LoadName(state) != "仅分组 Ctrl+S 回归") return false;
            ExpectState(session, false, false, "the completed Mod group-only Ctrl+S fallback", check);
            session.Rename(state.GroupId, "离开前未保存的空组");
            ExpectState(session, true, true, "an unsaved group before native editor exit", check);
            check(Popups().Length == 0, "save-state editor-exit fixture has no unrelated native popup");
            session.Editor.Exit(); SetPhase(state, Phase.ExitReady); return false;
        }
        if (state.Phase == Phase.ExitReady)
        {
            var popup = ReadyDialog(state, check, "native editor exit"); if (popup == null) return false;
            PopupButtons(popup).btnB.SimulateClick(); state.ClickedAt = Time.unscaledTime;
            SetPhase(state, Phase.ExitCancelled); return false;
        }
        if (state.Phase == Phase.ExitCancelled)
        {
            if (Popups().Length > 0 && Time.unscaledTime - state.ClickedAt < 2) return false;
            check(Popups().Length == 0 && ReferenceEquals(GroupHost.Session, session) && SceneManager.GetActiveScene().name == "ScenarioEditorScene",
                "cancelling the native unsaved exit dialog keeps the same editor session open");
            ExpectState(session, true, true, "cancelling an unsaved editor exit", check);
            check(!Application.Internal_ApplicationWantsToQuit(), "unsaved empty groups veto the real application-wants-to-quit callback");
            check(!Application.Internal_ApplicationWantsToQuit() && Popups().Length == 1,
                "repeated application close requests keep exactly one unsaved confirmation window");
            SetPhase(state, Phase.QuitReady); return false;
        }
        if (state.Phase == Phase.QuitReady)
        {
            var popup = ReadyDialog(state, check, "application close"); if (popup == null) return false;
            var before = session.Workspace.Snapshot; var history = OperationManager.history.Count;
            var create = session.Board.treeRoot.parent.Find("NodeGroups toolbar/创建组")!;
            UIEventListener.Get(create.gameObject).onClick.Invoke(create.gameObject);
            check(ReferenceEquals(before, session.Workspace.Snapshot) && OperationManager.history.Count == history,
                "the native application-close modal blocks an underlying group-create callback");
            PopupButtons(popup).btnB.SimulateClick(); state.ClickedAt = Time.unscaledTime;
            SetPhase(state, Phase.QuitCancelled); return false;
        }
        if (state.Phase == Phase.QuitCancelled)
        {
            if (Popups().Length > 0 && Time.unscaledTime - state.ClickedAt < 2) return false;
            check(Popups().Length == 0 && ReferenceEquals(GroupHost.Session, session) && !session.IsDisposed,
                "cancelling application close preserves the live editor and dismisses its owned popup");
            ExpectState(session, true, true, "cancelling application close", check);
            check(!Application.Internal_ApplicationWantsToQuit(), "a later close request checks unsaved groups again after cancellation");
            SetPhase(state, Phase.QuitCloseReady); return false;
        }
        if (state.Phase == Phase.QuitCloseReady)
        {
            var popup = ReadyDialog(state, check, "application close button"); if (popup == null) return false;
            popup.xButton.SimulateClick(); state.ClickedAt = Time.unscaledTime;
            SetPhase(state, Phase.QuitClosed); return false;
        }
        if (state.Phase == Phase.QuitClosed)
        {
            if (Popups().Length > 0 && Time.unscaledTime - state.ClickedAt < 2) return false;
            check(Popups().Length == 0 && ReferenceEquals(GroupHost.Session, session),
                "closing the unsaved popup with its native X cancels the application close request");
            ExpectState(session, true, true, "closing the unsaved confirmation window", check);
            session.Save(); SetPhase(state, Phase.FinalSave); return false;
        }
        if (state.Phase == Phase.FinalSave)
        {
            if (!Saved(session)) return false;
            ExpectState(session, false, false, "final successful save", check);
            check(Application.Internal_ApplicationWantsToQuit() && Popups().Length == 0,
                "clean groups allow the real application close query without creating a confirmation popup");
            CheckNativeInvariant(state, session, check, "save and close checks");
            state.ReloadedSavedGroups = Groups(session);
            check(!File.Exists(state.RecoveryPath), "the clean-owner disposal fixture starts without a recovery file");
            var originalPath = PersistentData.projectPath;
            try
            {
                // 原生打开工程可以先更新全局路径，再销毁旧编辑器；旧会话不得切换到新工程的保存点。
                PersistentData.projectPath = state.MainPath;
                session.Dispose();
                check(session.IsDisposed && !session.Dirty && !File.Exists(state.RecoveryPath),
                    "disposing a clean old owner after the global project path changes stays clean and writes no recovery file");
            }
            finally
            {
                // 即使断言失败也恢复本夹具路径，避免旧原生编辑器的失败清理引用主工程路径。
                PersistentData.projectPath = originalPath;
            }
            SwitchScene(state, session, state.FixturePath, Phase.ReopenForLateQuit); return false;
        }
        if (state.Phase == Phase.QuitAbandonReady)
        {
            var popup = ReadyDialog(state, check, "disposed owner"); if (popup == null) return false;
            var oldConfirm = PopupButtons(popup).btnA.onClick;
            var before = Groups(session);
            SwitchScene(state, session, state.MainPath, Phase.AwaitMain, () =>
            {
                check(session.IsDisposed && !session.QuitPromptOpen && !popup.gameObject.activeInHierarchy,
                    "disposing the editor closes its owned quit dialog and releases pending close state");
                // 与原生恢复确认回归相同：销毁排在帧末，同帧模拟已经排队的旧按钮事件。
                oldConfirm.Invoke();
                check(!session.QuitPromptOpen && Groups(session) == before && Application.Internal_ApplicationWantsToQuit(),
                    "a late native quit-confirm event cannot authorize a disposed owner or retain its application-close subscription");
            });
            return false;
        }
        return false;
    }

    private static void CheckCommittedEdits(State state, EditorSession session, Action<bool, string> check)
    {
        void Exercise(string name, Action change)
        {
            change(); ExpectState(session, true, true, name, check);
            OperationManager.Undo(); ExpectState(session, false, false, "undoing " + name + " to the saved group state", check);
            OperationManager.Redo(); ExpectState(session, true, true, "redoing " + name, check);
            OperationManager.Undo(); ExpectState(session, false, false, "restoring the saved state after " + name, check);
        }
        Exercise("renaming an empty group", () => session.Rename(state.GroupId, "重命名空组"));
        Exercise("moving an empty group", () => session.Commit(session.Workspace.MoveGroup(state.GroupId, new Point2(53, -17))));
        var bounds = session.Bounds(session.Workspace.Snapshot.GetGroup(state.GroupId));
        Exercise("resizing an empty group", () => session.Commit(session.Workspace.SetExpandedBounds(state.GroupId,
            new Rect2(bounds.X - 40, bounds.Y - 50, bounds.Width + 80, bounds.Height + 50))));
        Exercise("collapsing an empty group", () => session.Toggle(state.GroupId));
        Exercise("dissolving an empty group", () => session.Dissolve(state.GroupId));
        var member = EditorSession.Id(session.Board.nodes.ToArray().First(node => node.TryCast<ScriptNode>() != null));
        Exercise("assigning a native member to a group", () => session.Commit(session.Workspace.TransferNodes(new[] { member }, state.GroupId)));
        var before = session.Workspace.Snapshot;
        session.BeginResize(state.GroupId, 2);
        session.Workspace.SetExpandedBounds(state.GroupId, new Rect2(bounds.X, bounds.Y, bounds.Width + 120, bounds.Height));
        ExpectState(session, false, false, "an uncommitted resize preview", check);
        session.CancelDrag();
        check(Groups(session) == GroupSignature(before.Groups), "cancelled resize restores its group metadata before save-state checks");
        ExpectState(session, false, false, "a cancelled resize", check);
        session.CreateGroup(true); state.SecondId = session.SelectedGroup!; session.Save(); session.Tick();
        ExpectState(session, false, false, "saving a second empty group", check);
        session.SelectGroup(state.GroupId); ExpectState(session, true, true, "persisting an actual group layer promotion", check);
        session.Save(); session.Tick();
        session.SelectGroup(state.GroupId); session.DeselectGroup();
        ExpectState(session, false, false, "selection without a further layer change", check);
        CheckNativeInvariant(state, session, check, "empty metadata commands and their Undo/Redo");
    }

    private static void CheckFailedSave(State state, EditorSession session, Action<bool, string> check)
    {
        var sidecar = Sidecar(state.FixturePath); var bytes = File.ReadAllBytes(sidecar);
        // Windows 替换目标文件需要删除共享权限；只允许读取即可真实阻止原子替换，不改产品文件系统。
        using (var locked = new FileStream(sidecar, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            session.Save(); session.Tick();
            ExpectState(session, true, true, "a failed sidecar commit after native data is already saved", check);
            check(File.ReadAllBytes(sidecar).SequenceEqual(bytes), "failed group save preserves the previous sidecar bytes");
        }
    }

    /// <summary>旧备份路径的有效回执只能推进该路径的保存点，不能污染当前工程的保存基准。</summary>
    private static void CheckIndependentSaveDestination(State state, EditorSession session, Action<bool, string> check)
    {
        var alternate = Path.Combine(Path.GetDirectoryName(state.FixturePath)!, "save-state-alternate" + ProjectFileStore.ProjectExtension);
        if (File.Exists(alternate) || File.Exists(Sidecar(alternate))) throw new InvalidOperationException("Alternate save fixture already exists.");
        File.Copy(state.FixturePath, alternate);
        var olderName = session.Workspace.Snapshot.GetGroup(state.GroupId).Name;
        var alternateReceipt = session.FreezeSave(alternate) with
        {
            SavedHash = EditorSession.Hash(alternate), SavedVersion = ProjectFileStore.Version(alternate)
        };
        const string currentName = "当前工程的独立保存点";
        session.Rename(state.GroupId, currentName); session.Save(); session.Tick();
        ExpectState(session, false, false, "saving newer groups at the active project destination", check);
        session.Saved(alternateReceipt); session.Tick();
        using (var document = JsonDocument.Parse(File.ReadAllBytes(Sidecar(alternate))))
        {
            var savedName = document.RootElement.GetProperty("groups").EnumerateArray()
                .Single(group => group.GetProperty("groupId").GetString() == state.GroupId).GetProperty("name").GetString();
            check(savedName == olderName, "an older alternate-destination receipt writes only its own frozen group snapshot");
        }
        check(Path.GetFullPath(PersistentData.projectPath) == Path.GetFullPath(state.FixturePath) && LoadName(state) == currentName &&
            session.Workspace.Snapshot.GetGroup(state.GroupId).Name == currentName,
            "alternate-destination acknowledgment preserves the active native path, current sidecar and live group name");
        ExpectState(session, false, false, "acknowledging an older successful save to another destination", check);
        var activePath = PersistentData.projectPath;
        try
        {
            // 仅验证活动保存基准的立即激活接口；不冒充原生 SaveAs 文件对话框测试。
            // 不推进 Tick：退出判断必须即时采用路径基准，不能等宿主的 0.2 秒轮询。
            PersistentData.projectPath = alternate;
            check(OperationManager.HasChangesSinceLastSave && session.Dirty,
                "the native unsaved getter immediately activates the alternate G0 baseline before the next host Tick");
            PersistentData.projectPath = activePath;
            check(!OperationManager.HasChangesSinceLastSave && !session.Dirty,
                "the native unsaved getter immediately restores the active G1 save point without a host Tick");
        }
        finally
        {
            PersistentData.projectPath = activePath;
            _ = OperationManager.HasChangesSinceLastSave;
        }
    }

    private static void CheckNativeOnlyEdit(State state, EditorSession session, Action<bool, string> check)
    {
        var id = Guid.NewGuid().ToString("D");
        var operation = new NodeCreateOperation(Il2CppType.Of<ScriptNode>(), NativeGuid.Parse(id), new Vector2(8000, 8000));
        operation.Redo(); OperationManager.WriteIsolated(operation); session.Tick();
        ExpectState(session, false, true, "an original native node creation while groups remain saved", check);
        OperationManager.Undo(); session.Tick();
        ExpectState(session, false, false, "undoing the original native node creation", check);
        check(session.Find(id) == null, "save-state native-only regression removes its temporary native node through Undo");
    }

    /// <summary>
    /// 仅在 HandleKeys 一次同步调用内模拟 Control+S，并观察真实 Mod 保存入口次数。
    /// 不驱动原生 Workbench 的其他按键流程，也不替换保存实现或返回结果。
    /// </summary>
    private static void InvokeGroupOnlyShortcut(EditorSession session, Action<bool, string> check)
    {
        var harmony = new Harmony("azurearchive.nodegroups.tests.group-save-shortcut." + Guid.NewGuid().ToString("N"));
        shortcutOwner = session; shortcutSaveCalls = 0;
        try
        {
            harmony.Patch(AccessTools.Method(typeof(Input), nameof(Input.GetKey), new[] { typeof(KeyCode) }),
                prefix: new HarmonyMethod(typeof(SaveStateIntegrationTests), nameof(ShortcutKey)));
            harmony.Patch(AccessTools.Method(typeof(Input), nameof(Input.GetKeyDown), new[] { typeof(KeyCode) }),
                prefix: new HarmonyMethod(typeof(SaveStateIntegrationTests), nameof(ShortcutKeyDown)));
            harmony.Patch(AccessTools.Method(typeof(EditorSession), nameof(EditorSession.Save)),
                prefix: new HarmonyMethod(typeof(SaveStateIntegrationTests), nameof(CountShortcutSave)));
            session.HandleKeys();
            check(shortcutSaveCalls == 1,
                "the Mod group-only Ctrl+S fallback calls its real save entry exactly once while the merged dirty predicate is true");
        }
        finally
        {
            try { harmony.UnpatchSelf(); }
            finally { shortcutOwner = null; shortcutSaveCalls = 0; }
        }
    }
    private static bool ShortcutKey(KeyCode __0, ref bool __result)
    {
        if (shortcutOwner == null || __0 is not (KeyCode.LeftControl or KeyCode.RightControl)) return true;
        __result = true; return false;
    }
    private static bool ShortcutKeyDown(KeyCode __0, ref bool __result)
    {
        if (shortcutOwner == null || __0 is not (KeyCode.S or KeyCode.Escape)) return true;
        __result = __0 == KeyCode.S; return false;
    }
    private static void CountShortcutSave(EditorSession __instance)
    { if (ReferenceEquals(shortcutOwner, __instance)) shortcutSaveCalls++; }

    private static void ExpectState(EditorSession session, bool groupDirty, bool combinedDirty, string context, Action<bool, string> check)
    {
        OperationManager.UpdateCompileSaveBtn();
        check(session.Dirty == groupDirty, "save-state group baseline: " + context);
        check(OperationManager.HasChangesSinceLastSave == combinedDirty, "save-state combined native predicate: " + context);
        var expected = combinedDirty ? OperationManager.enabledColor : OperationManager.disabledColor;
        check(NearColor(session.Editor.saveBtn.color, expected), "save-state native save button color: " + context);
    }
    private static bool NearColor(Color a, Color b) => Math.Abs(a.r - b.r) < .0001f && Math.Abs(a.g - b.g) < .0001f &&
        Math.Abs(a.b - b.b) < .0001f && Math.Abs(a.a - b.a) < .0001f;
    private static bool Saved(EditorSession session)
    { session.Tick(); return AuthoringEditorSession.Current?.SaveInProgress != true && !session.Dirty; }
    private static string? LoadName(State state)
    {
        if (!File.Exists(Sidecar(state.FixturePath))) return null;
        using var document = JsonDocument.Parse(File.ReadAllBytes(Sidecar(state.FixturePath)));
        foreach (var group in document.RootElement.GetProperty("groups").EnumerateArray())
            if (group.GetProperty("groupId").GetString() == state.GroupId) return group.GetProperty("name").GetString();
        return null;
    }
    private static UI.UIPopupSystem[] Popups() => UnityEngine.Object.FindObjectsOfType<UI.UIPopupSystem>(true)
        .Where(window => window.gameObject.activeInHierarchy).ToArray();
    private static UI.UIPopupSystem.ButtonTypeInfo PopupButtons(UI.UIPopupSystem popup) => popup.buttonInfoList.ToArray()
        .Single(info => info.type == UI.UIPopupSystem.ButtonType.OkCancelButton);
    private static UI.UIPopupSystem? ReadyDialog(State state, Action<bool, string> check, string context)
    {
        if (state.Dialog == null)
        {
            var windows = Popups(); check(windows.Length == 1, "save-state " + context + " opens exactly one native confirmation window");
            state.Dialog = windows[0]; state.OpenedAt = Time.unscaledTime; return null;
        }
        return Time.unscaledTime - state.OpenedAt >= .5f ? state.Dialog : null;
    }
    private static void SetPhase(State state, Phase phase)
    { state.Phase = phase; state.Frames = 0; state.Deadline = DateTime.UtcNow.AddSeconds(40); state.Dialog = null; }
    private static void SwitchScene(State state, EditorSession session, string path, Phase phase, Action? afterDispose = null)
    {
        var data = ProjectFileStore.Read(path).Project;
        session.Dispose(); GroupHost.Session = null;
        afterDispose?.Invoke();
        PersistentData.projectPath = path; PersistentData.projectData = data;
        SetPhase(state, phase); SceneManager.LoadScene("ScenarioEditorScene");
    }
    private static string Sidecar(string path) => global::NodeGroups.Storage.GroupStore.GetSidecarPath(path);
    private static void CheckNativeInvariant(State state, EditorSession session, Action<bool, string> check, string context) => check(
        Story(session.Editor.CaptureProject()) == state.FixtureStory && NodesEqual(session.ReadNodes(), state.FixtureNodes) &&
        AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray().SequenceEqual(state.FixtureCompiled) &&
        OperationManager.compileDelta == state.CompileDelta, "save-state preserves native graph, positions, compile bytes and compile dirty state: " + context);
    private static bool NodesEqual(NodeState[] first, NodeState[] second) => first.Length == second.Length && first.All(node => second.Any(other =>
        node.Id == other.Id && Math.Abs(node.Position.X - other.Position.X) < .001 && Math.Abs(node.Position.Y - other.Position.Y) < .001));
    private static string Groups(EditorSession session) => GroupSignature(session.Workspace.Snapshot.Groups);
    private static string GroupSignature(IEnumerable<GroupState> groups) => JsonSerializer.Serialize(groups.Select(group => new
        { group.Id, group.Name, group.MemberNodeIds, group.Collapsed, group.Anchor, group.Color, group.ZIndex, group.ExpandedBounds }));
    private static string RecoveryPath(string projectId) => Path.Combine(BepInEx.Paths.GameRootPath, "ModDev", "NodeGroups", "artifacts", "recovery",
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(projectId))) + ".project.nodegroups.json");
    private static void DeleteOwnedRecovery(State state)
    { if (state.RecoveryPath.Length > 0 && File.Exists(state.RecoveryPath)) File.Delete(state.RecoveryPath); }

    /// <summary>只清理本次独立工程拥有的会话和恢复文件，失败不能影响用户工程或其他弹窗。</summary>
    internal static void Abort(EditorSession? session)
    {
        var state = active; if (state == null) return;
        try
        {
            if (state.Dialog != null) state.Dialog.HidePopup();
            if (session != null && !session.IsDisposed && session.ProjectId == state.FixtureProjectId) session.Dispose();
            DeleteOwnedRecovery(state);
        }
        catch (Exception error) { Plugin.Logger.LogWarning("Save-state fixture cleanup: " + error); }
        finally { active = null; }
    }

    private static string Story(ProjectData project)
    {
        var payload = ProjectFileStore.Serialize(project); var start = payload.IndexOf('{');
        if (start < 0) throw new InvalidOperationException("Native project has no JSON payload.");
        using var document = JsonDocument.Parse(payload[start..]); using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, document.RootElement);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value, string? property = null)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var child in value.EnumerateObject().OrderBy(child => child.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(child.Name); WriteCanonical(writer, child.Value, child.Name); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray(); var values = value.EnumerateArray().ToArray();
            if (property == "nodes") values = values.OrderBy(node => node.GetProperty("Guid").GetString(), StringComparer.Ordinal).ToArray();
            foreach (var child in values) WriteCanonical(writer, child); writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
}
