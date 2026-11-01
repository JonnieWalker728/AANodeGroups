using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AzureArchive.Automation;
using NodeGroups.Core;
using NodeGroups.Storage;
using Studio.Scripts;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AzureArchive.NodeGroups.Runtime.Tests;

/// <summary>
/// Opt-in native persistence regressions for the report-directory fixture only. One session exists
/// at a time; native scene reload disposes the editor and GroupHost attaches its replacement.
/// </summary>
/// <remarks>Call Run once after the base fixture saves, then Tick each main-thread test frame until true.</remarks>
internal static class PersistenceLifecycleTests
{
    private enum Phase { GuiSave, ReopenValid, InvalidJson, ForeignProject, Unreadable, ValidAgain, RecoveryCancel, RecoveryClose, RecoveryAbandon, Recovery, Done }
    private sealed class State
    {
        internal readonly string Path;
        internal readonly string Sidecar;
        internal readonly string RecoveryFile;
        internal readonly byte[]? PreviousRecovery;
        internal readonly Action<bool, string> Check;
        internal readonly string NativeInvariant;
        internal readonly byte[] CompiledInvariant;
        internal EditorSession Owner;
        internal EditorSession.SaveReceipt? OldReceipt;
        internal Phase Phase;
        internal DateTime Deadline;
        internal byte[] SavedSidecar = Array.Empty<byte>();
        internal byte[] CaseSidecar = Array.Empty<byte>();
        internal string GroupsInvariant = "";
        internal string GuiGroupId = "";
        internal string OriginalName = "";
        internal string GuiName = "";
        internal string GuiUnsavedName = "";
        internal string RecoveryName = "";
        internal byte[] RecoveryBytes = Array.Empty<byte>();
        internal UI.UIPopupSystem? RecoveryDialog;
        internal float RecoveryDialogOpenedAt;
        internal bool RecoveryActionSent;
        internal int RecoveryActionFrame;
        internal float RecoveryActionAt;
        internal Dictionary<string, Point2> Positions = new();
        internal FileStream? ReadLock;
        internal bool ProtectionSaveStarted;
        internal string ProtectionNativeSnapshot = "";

        internal State(EditorSession owner, string path, Action<bool, string> check)
        {
            Owner = owner; Path = path; Sidecar = GroupStore.GetSidecarPath(path); Check = check;
            NativeInvariant = ProjectFileStore.Serialize(owner.Editor.CaptureProject());
            CompiledInvariant = AuthoringCompiler.CompileBytes(owner.Editor.CaptureProject()).ToArray();
            var projectKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(owner.ProjectId)));
            RecoveryFile = System.IO.Path.Combine(BepInEx.Paths.GameRootPath, "ModDev", "NodeGroups", "artifacts", "recovery", projectKey + ".project.nodegroups.json");
            PreviousRecovery = File.Exists(RecoveryFile) ? File.ReadAllBytes(RecoveryFile) : null;
            Deadline = DateTime.UtcNow.AddSeconds(20);
        }
    }

    private static State? active;

    /// <summary>
    /// Verifies receipt rejection synchronously, then starts a real forced native GUI save and edits
    /// its metadata immediately afterward. Tick verifies the saved and live snapshots independently.
    /// </summary>
    internal static void Run(EditorSession session, string projectPath, Action<bool, string> check)
    {
        if (active != null) throw new InvalidOperationException("Persistence regressions are already running.");
        RequireTemporaryFixture(session, projectPath);
        var state = new State(session, System.IO.Path.GetFullPath(projectPath), check);
        active = state;
        try
        {
            check(ReferenceEquals(GroupHost.Session, session) && !session.IsDisposed, "storage regression starts with the single live fixture session");
            check(File.Exists(state.Sidecar) && !session.Dirty, "storage regression starts after a completed matching sidecar save");
            var originalGroup = session.Workspace.Snapshot.Groups.First();
            // Save a deliberately larger region rather than an automatically fitted
            // one: scene reload must preserve the user's empty canvas space and layers.
            var region = originalGroup.ExpandedBounds ?? session.Bounds(originalGroup);
            session.Commit(session.Workspace.SetExpandedBounds(originalGroup.Id,
                new Rect2(region.X, region.Y - 80, region.Width + 120, region.Height + 80)));
            foreach (var other in session.Workspace.Snapshot.Groups.Where(group => group.Id != originalGroup.Id))
                session.SelectGroup(other.Id);
            session.SelectGroup(originalGroup.Id);
            session.Save(); session.Tick();
            check(!session.Dirty && GroupSignature(Load(session, state.Path).Snapshot!.Groups) == GroupSignature(session.Workspace.Snapshot.Groups),
                "iteration 2 group-only save persists a deliberately enlarged region and selection-driven layer order");
            CheckNativeInvariant(state, session, "iteration 2 region and layer persistence leaves the original story and compile bytes unchanged");
            var delayed = VerifiedReceipt(session, state.Path);
            var laterName = "同版本较新元数据 / " + Guid.NewGuid().ToString("N");
            session.Rename(originalGroup.Id, laterName); session.Save(); session.Tick();
            check(!session.Dirty, "newer group-only metadata save completes on the same native core version");
            var laterSidecar = File.ReadAllBytes(state.Sidecar);
            session.Saved(delayed); session.Tick();
            check(!session.Dirty && File.ReadAllBytes(state.Sidecar).SequenceEqual(laterSidecar) &&
                Load(session, state.Path).Snapshot!.Groups.Any(g => g.Id == originalGroup.Id && g.Name == laterName),
                "older metadata receipt cannot overwrite a newer sidecar or dirty the clean owner on the same core version");
            session.Rename(originalGroup.Id, originalGroup.Name); session.Save(); session.Tick();
            check(!session.Dirty, "same-version receipt regression restores its original group name");
            var before = File.ReadAllBytes(state.Sidecar);
            var receipt = VerifiedReceipt(session, state.Path);
            session.Saved(receipt with { SavedHash = new string('0', 64) });
            session.Tick();
            check(session.Dirty && File.ReadAllBytes(state.Sidecar).SequenceEqual(before), "incorrect receipt hash is rejected without replacing sidecar");
            session.Saved(receipt with { SavedVersion = "obsolete-fixture-version-" + Guid.NewGuid().ToString("N") });
            session.Tick();
            check(session.Dirty && File.ReadAllBytes(state.Sidecar).SequenceEqual(before), "obsolete native save version is rejected without replacing sidecar");
            session.Saved(session.FreezeSave(state.Path));
            session.Tick();
            check(session.Dirty && File.ReadAllBytes(state.Sidecar).SequenceEqual(before), "unknown native save version is rejected rather than rebound to current file hash");

            var group = session.Workspace.Snapshot.Groups.First();
            state.GuiGroupId = group.Id; state.OriginalName = group.Name;
            state.GuiName = "GUI 保存回归 / " + Guid.NewGuid().ToString("N");
            state.GuiUnsavedName = "GUI 保存中的新编辑 / " + Guid.NewGuid().ToString("N");
            session.Rename(group.Id, state.GuiName);
            state.Phase = Phase.GuiSave;
            session.Editor.Save(true); // Exercise the original GUI path, not the group-only Save shortcut.
            Plugin.Logger.LogInfo("GUI receipt freeze regression: native SaveInProgress immediately after Save=" +
                (AuthoringEditorSession.Current?.SaveInProgress == true));
            // Both metadata revisions have the same native story payload. A lower-level worker
            // that freezes the current groups late would incorrectly bind G1 to the G0 request.
            session.Rename(group.Id, state.GuiUnsavedName);
        }
        catch { Abort(); throw; }
    }

    /// <summary>
    /// Advances real native reload/save/recovery phases. Returns true only after every assertion.
    /// A null/old session is allowed while GroupHost rebuilds after the native editor scene reload.
    /// </summary>
    internal static bool Tick(EditorSession? session)
    {
        var state = active ?? throw new InvalidOperationException("Call Run before Tick.");
        if (state.Phase == Phase.Done) return true;
        try
        {
            if (DateTime.UtcNow > state.Deadline)
                throw new TimeoutException($"Storage native regression timed out in {state.Phase}; current dirty state: {session?.Dirty}");
            if (session == null || session.IsDisposed) return false;
            if (state.Phase == Phase.GuiSave)
            {
                if (!ReferenceEquals(state.Owner, session)) return false;
                session.Tick();
                var load = Load(session, state.Path);
                var savedLaterEdit = load.Snapshot?.Groups.Any(g => g.Id == state.GuiGroupId && g.Name == state.GuiUnsavedName) == true;
                if (savedLaterEdit)
                    state.Check(false, "original GUI request never saves metadata edited after its snapshot was frozen");
                if (AuthoringEditorSession.Current?.SaveInProgress == true ||
                    load.Snapshot?.Groups.All(g => g.Id != state.GuiGroupId || g.Name != state.GuiName) != false)
                    return false;
                state.Check(load.Status == GroupLoadStatus.Loaded, "forced original GUI save eventually commits a matching metadata sidecar");
                state.Check(!savedLaterEdit, "original GUI request never saves metadata edited after its snapshot was frozen");
                state.Check(session.Dirty && session.Workspace.Snapshot.GetGroup(state.GuiGroupId).Name == state.GuiUnsavedName,
                    "completed G0 GUI receipt preserves the newer live G1 metadata and keeps it dirty");
                CheckNativeInvariant(state, session, "forced GUI save preserves fixture native graph and compiled bytes");
                session.Save(); session.Tick();
                state.Check(!session.Dirty && Load(session, state.Path).Snapshot!.Groups.Any(g => g.Id == state.GuiGroupId && g.Name == state.GuiUnsavedName),
                    "newer G1 metadata saves through a separate request and clears dirty");
                session.Rename(state.GuiGroupId, state.OriginalName);
                session.Save(); session.Tick();
                state.Check(!session.Dirty, "group-only save restores fixture group name after forced GUI save");
                state.SavedSidecar = File.ReadAllBytes(state.Sidecar);
                state.GroupsInvariant = GroupSignature(session.Workspace.Snapshot.Groups);
                RequestReload(state, session, Phase.ReopenValid, probeVisibility: true);
                return false;
            }

            // A fresh owner proves reload semantics. The protection phases also need later frames
            // on that same owner while its original GUI save completes under the authoring lease.
            if (ReferenceEquals(state.Owner, session))
            {
                if (!state.ProtectionSaveStarted && state.Phase is not
                    (Phase.RecoveryCancel or Phase.RecoveryClose or Phase.RecoveryAbandon or Phase.Recovery)) return false;
            }
            else
            {
                state.Owner = session;
                state.Check(ReferenceEquals(GroupHost.Session, session), "native reload has one newly attached owner");
            }
            if (state.Phase == Phase.ReopenValid)
            {
                CheckReload(state, session, "new EditorSession restores saved groups through its constructor");
                CheckOldOwnerReceipt(state, session);
                state.CaseSidecar = Encoding.UTF8.GetBytes("{ deliberately broken fixture JSON");
                File.WriteAllBytes(state.Sidecar, state.CaseSidecar);
                RequestReload(state, session, Phase.InvalidJson);
            }
            else if (state.Phase == Phase.InvalidJson)
            {
                if (!CheckProtectedSidecar(state, session, "corrupt sidecar survives original native save and group save")) return false;
                var foreign = JsonNode.Parse(Encoding.UTF8.GetString(state.SavedSidecar))!.AsObject();
                foreign["projectId"] = "different-fixture-project-" + Guid.NewGuid().ToString("N");
                state.CaseSidecar = Encoding.UTF8.GetBytes(foreign.ToJsonString());
                File.WriteAllBytes(state.Sidecar, state.CaseSidecar);
                RequestReload(state, session, Phase.ForeignProject);
            }
            else if (state.Phase == Phase.ForeignProject)
            {
                if (!CheckProtectedSidecar(state, session, "foreign ProjectId sidecar survives original native save and group save")) return false;
                File.WriteAllBytes(state.Sidecar, state.SavedSidecar);
                state.CaseSidecar = state.SavedSidecar;
                state.ReadLock = new FileStream(state.Sidecar, FileMode.Open, FileAccess.Read, FileShare.None);
                RequestReload(state, session, Phase.Unreadable);
            }
            else if (state.Phase == Phase.Unreadable)
            {
                if (!state.ProtectionSaveStarted)
                {
                    state.Check(session.SidecarLoadBlocked, "unreadable sidecar is blocked during new session construction");
                    state.ReadLock!.Dispose(); state.ReadLock = null;
                }
                if (!CheckProtectedSidecar(state, session, "read-failed sidecar remains protected after the file lock is released")) return false;
                File.WriteAllBytes(state.Sidecar, state.SavedSidecar);
                RequestReload(state, session, Phase.ValidAgain);
            }
            else if (state.Phase == Phase.ValidAgain)
            {
                CheckReload(state, session, "repair plus native reload restores valid fixture sidecar");
                state.Positions = session.ReadNodes().ToDictionary(n => n.Id, n => n.Position, StringComparer.Ordinal);
                state.RecoveryName = "显式恢复回归 / " + Guid.NewGuid().ToString("N");
                session.Rename(state.GuiGroupId, state.RecoveryName);
                state.Check(session.Dirty, "unsaved group rename marks recovery fixture dirty");
                RequestReload(state, session, Phase.RecoveryCancel, keepRecovery: true);
                state.RecoveryBytes = File.ReadAllBytes(state.RecoveryFile);
            }
            else if (state.Phase == Phase.RecoveryCancel || state.Phase == Phase.RecoveryClose)
            {
                var prompt = state.RecoveryActionSent ? null : ReadyRecoveryPrompt(state, session);
                if (!state.RecoveryActionSent && prompt == null) return false;
                // 使用原生控件的真实点击路径；取消按钮和关闭按钮均应仅关闭本次提示。
                if (!state.RecoveryActionSent)
                {
                    state.Check(!session.Dirty && session.Workspace.Snapshot.GetGroup(state.GuiGroupId).Name == state.OriginalName,
                        "recovery confirmation waits for an explicit choice without importing metadata");
                    var buttons = prompt!.buttonInfoList.ToArray().Single(info => info.type == UI.UIPopupSystem.ButtonType.OkCancelButton);
                    LogRecoveryDialog(state, session, "before cancel/close");
                    if (state.Phase == Phase.RecoveryCancel) buttons.btnB.SimulateClick();
                    else prompt!.xButton.SimulateClick();
                    state.RecoveryActionSent = true; state.RecoveryActionFrame = Time.frameCount; state.RecoveryActionAt = Time.unscaledTime;
                    return false;
                }
                // 原生按钮与窗口各自有 Start/动画及帧末销毁时序，不能用同一帧的多次 Tick 代替。
                if (Time.frameCount <= state.RecoveryActionFrame + 1) return false;
                // SimulateClick 内部会启动带 WaitForSeconds 的按压协程；等真实关闭，超时后仍执行原断言。
                if (RecoveryPrompts().Length > 0 && Time.unscaledTime - state.RecoveryActionAt < 2) return false;
                session.Tick();
                LogRecoveryDialog(state, session, "after cancel/close");
                state.Check(RecoveryPrompts().Length == 0 && session.RecoveryAvailable && !session.Dirty &&
                    session.Workspace.Snapshot.GetGroup(state.GuiGroupId).Name == state.OriginalName,
                    "cancel or close dismisses recovery once without import or repeated prompts in the same session");
                state.Check(File.ReadAllBytes(state.RecoveryFile).SequenceEqual(state.RecoveryBytes),
                    "declining recovery preserves the exact recovery file bytes");
                CheckNativeInvariant(state, session, "declining recovery retains the saved native graph and compiled bytes");
                RequestReload(state, session, state.Phase == Phase.RecoveryCancel ? Phase.RecoveryClose : Phase.RecoveryAbandon, keepRecovery: true);
            }
            else if (state.Phase == Phase.RecoveryAbandon)
            {
                var prompt = ReadyRecoveryPrompt(state, session);
                if (prompt == null) return false;
                var buttons = prompt.buttonInfoList.ToArray().Single(info => info.type == UI.UIPopupSystem.ButtonType.OkCancelButton);
                var oldConfirm = buttons.btnA.onClick;
                var before = GroupSignature(session.Workspace.Snapshot.Groups);
                // 保留原生确认事件，在会话销毁后模拟一个已排队、迟到的回调。
                RequestReload(state, session, Phase.Recovery, keepRecovery: true, afterDispose: () =>
                {
                    state.Check(prompt == null || !prompt.gameObject.activeInHierarchy,
                        "disposing the editor dismisses only its owned native recovery prompt");
                    oldConfirm.Invoke();
                    state.Check(GroupSignature(session.Workspace.Snapshot.Groups) == before && !session.Dirty,
                        "late native recovery confirmation cannot import metadata into a disposed owner");
                });
            }
            else if (state.Phase == Phase.Recovery)
            {
                var prompt = state.RecoveryActionSent ? null : ReadyRecoveryPrompt(state, session);
                if (!state.RecoveryActionSent && prompt == null) return false;
                if (!state.RecoveryActionSent)
                {
                    state.Check(session.RecoveryAvailable && session.Workspace.Snapshot.GetGroup(state.GuiGroupId).Name == state.OriginalName,
                        "dirty-exit recovery is offered without automatic import on native reload");
                    var buttons = prompt!.buttonInfoList.ToArray().Single(info => info.type == UI.UIPopupSystem.ButtonType.OkCancelButton);
                    LogRecoveryDialog(state, session, "before confirm");
                    buttons.btnA.SimulateClick();
                    state.RecoveryActionSent = true; state.RecoveryActionFrame = Time.frameCount; state.RecoveryActionAt = Time.unscaledTime;
                    return false;
                }
                if (Time.frameCount <= state.RecoveryActionFrame + 1) return false;
                if (RecoveryPrompts().Length > 0 && Time.unscaledTime - state.RecoveryActionAt < 2) return false;
                session.Tick();
                LogRecoveryDialog(state, session, "after confirm");
                state.Check(RecoveryPrompts().Length == 0 && !session.RecoveryAvailable && session.Dirty &&
                    session.Workspace.Snapshot.GetGroup(state.GuiGroupId).Name == state.RecoveryName,
                    "confirming the native recovery dialog imports only group metadata and keeps it dirty");
                state.Check(session.ReadNodes().All(n => state.Positions.TryGetValue(n.Id, out var old) && Near(n.Position, old)),
                    "recovery preserves the saved native node positions and identities");
                CheckNativeInvariant(state, session, "session reload and recovery retain original graph and compiled bytes");
                session.Save(); session.Tick();
                state.Check(!session.Dirty && Load(session, state.Path).Snapshot!.Groups.Any(g => g.Id == state.GuiGroupId && g.Name == state.RecoveryName),
                    "explicitly restored metadata can be saved without native graph changes");
                session.Rename(state.GuiGroupId, state.OriginalName); session.Save(); session.Tick();
                state.Check(!session.Dirty && GroupSignature(session.Workspace.Snapshot.Groups) == state.GroupsInvariant,
                    "persistence regressions restore the original saved fixture grouping");
                RestorePreviousRecovery(state);
                state.Phase = Phase.Done;
                return true;
            }
            return false;
        }
        catch { Abort(); throw; }
    }

    /// <summary>Releases the test-only lock and restores its recovery file on an aborted test process.</summary>
    internal static void Abort()
    {
        var state = active;
        if (state == null) return;
        try { state.ReadLock?.Dispose(); }
        finally
        {
            state.ReadLock = null;
            try { RestorePreviousRecovery(state); }
            catch (Exception error)
            {
                // Preserve the original assertion failure; cleanup diagnostics must not replace it.
                try { Plugin.Logger.LogWarning("Fixture recovery cleanup failed: " + error.Message); }
                catch { }
            }
            active = null;
        }
    }

    private static void RequestReload(State state, EditorSession session, Phase next, bool probeVisibility = false, bool keepRecovery = false,
        Action? afterDispose = null)
    {
        var data = ProjectFileStore.Read(state.Path).Project;
        state.OldReceipt = VerifiedReceipt(session, state.Path);
        var widgets = new List<(UIWidget Widget, bool Enabled)>();
        if (probeVisibility)
        {
            var group = session.Workspace.Snapshot.Groups.FirstOrDefault(g => !g.Collapsed && g.MemberNodeIds.Count > 0);
            if (group != null)
            {
                var member = session.Find(group.MemberNodeIds[0])!;
                widgets.AddRange(member.GetComponentsInChildren<UIWidget>(true).Select(w => (w, w.enabled)));
                session.Toggle(group.Id); session.EnforceVisibility();
                state.Check(widgets.Count > 0 && widgets.All(w => !w.Widget.enabled), "dispose regression hides a real native member before cleanup");
            }
        }
        session.Dispose();
        state.Check(session.IsDisposed && !NativeHistory.SuppressNativeMovement, "session dispose closes callback ingress and native movement suppression");
        if (widgets.Count > 0)
            state.Check(widgets.All(w => w.Widget != null && w.Widget.enabled == w.Enabled), "session dispose restores exact native widget enabled states");
        GroupHost.Session = null;
        afterDispose?.Invoke();
        // Test late callbacks after Dispose before the fresh owner is attached; these may not enqueue.
        session.Saved(state.OldReceipt);
        if (!keepRecovery) RestorePreviousRecovery(state);
        else
        {
            state.Check(File.Exists(state.RecoveryFile), "dirty session dispose writes an explicit recovery sidecar");
            File.SetLastWriteTimeUtc(state.RecoveryFile, File.GetLastWriteTimeUtc(state.Sidecar).AddSeconds(1));
        }
        state.Phase = next; state.Deadline = DateTime.UtcNow.AddSeconds(20);
        state.RecoveryDialog = null; state.RecoveryActionSent = false;
        state.ProtectionSaveStarted = false; state.ProtectionNativeSnapshot = "";
        // StudioCommon.Load fills a new Board and does not clear an existing one. The real scene
        // lifecycle destroys the old Board and ends its authoring lease before the next editor
        // starts. GroupHost must attach only after the replacement scene is ready.
        PersistentData.projectData = data;
        PersistentData.projectPath = state.Path;
        SceneManager.LoadScene("ScenarioEditorScene");
    }

    /// <summary>
    /// Starts one native GUI save, then waits on later frames for its authoring save to finish.
    /// The live native editor owns the file lease; a competing ProjectFileStore.Save is invalid.
    /// </summary>
    private static bool CheckProtectedSidecar(State state, EditorSession session, string name)
    {
        if (!state.ProtectionSaveStarted)
        {
            state.Check(session.SidecarLoadBlocked, name + " (load protection)");
            session.Save(); session.Tick();
            state.Check(session.Dirty && File.ReadAllBytes(state.Sidecar).SequenceEqual(state.CaseSidecar), name + " (group-only save)");
            var native = AuthoringEditorSession.Current;
            state.Check(native != null && native.ProjectId == session.ProjectId &&
                string.Equals(System.IO.Path.GetFullPath(native.FilePath), state.Path, StringComparison.OrdinalIgnoreCase) && !native.SaveInProgress,
                name + " (live native authoring owner is ready)");
            state.ProtectionNativeSnapshot = ProjectFileStore.Serialize(session.Editor.CaptureProject());
            state.ProtectionSaveStarted = true;
            state.Deadline = DateTime.UtcNow.AddSeconds(20);
            session.Editor.Save(true);
            return false; // A void Save return is never used as its completion signal.
        }

        var authoring = AuthoringEditorSession.Current;
        if (authoring == null || authoring.SaveInProgress) return false;
        state.Check(authoring.ProjectId == session.ProjectId &&
            ProjectFileStore.Serialize(ProjectFileStore.Read(state.Path).Project) == state.ProtectionNativeSnapshot,
            name + " (native authoring save completed with the frozen story snapshot)");
        session.Tick();
        state.Check(session.Dirty && File.ReadAllBytes(state.Sidecar).SequenceEqual(state.CaseSidecar), name + " (native successful save)");
        // Native save may update its own format/source-version bytes. The eventual explicit repair
        // retains original groups but binds their test backup to those actual current project bytes.
        var valid = JsonNode.Parse(Encoding.UTF8.GetString(state.SavedSidecar))!.AsObject();
        valid["coreSnapshotHash"] = EditorSession.Hash(state.Path);
        state.SavedSidecar = Encoding.UTF8.GetBytes(valid.ToJsonString());
        state.ProtectionSaveStarted = false;
        return true;
    }

    private static void CheckReload(State state, EditorSession session, string name)
    {
        state.Check(!session.SidecarLoadBlocked && !session.Dirty && GroupSignature(session.Workspace.Snapshot.Groups) == state.GroupsInvariant, name);
    }

    private static void CheckOldOwnerReceipt(State state, EditorSession session)
    {
        var before = File.ReadAllBytes(state.Sidecar);
        state.OldReceipt!.Owner.Saved(state.OldReceipt);
        session.Saved(state.OldReceipt); session.Tick();
        state.Check(!session.Dirty && File.ReadAllBytes(state.Sidecar).SequenceEqual(before),
            "late old-owner receipt cannot write or dirty a new same-ProjectId editor session");
    }

    private static void CheckNativeInvariant(State state, EditorSession session, string name) => state.Check(
        ProjectFileStore.Serialize(session.Editor.CaptureProject()) == state.NativeInvariant &&
        AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray().SequenceEqual(state.CompiledInvariant), name);

    /// <summary>只查询原生确认窗口，不依赖尚待实现的产品辅助类型或内部字段。</summary>
    private static UI.UIPopupSystem[] RecoveryPrompts() => UnityEngine.Object.FindObjectsOfType<UI.UIPopupSystem>(true)
        .Where(popup => popup.gameObject.activeInHierarchy && popup.titleLabel != null &&
            popup.titleLabel.text == "恢复未保存的节点组").ToArray();

    private static UI.UIPopupSystem RequireRecoveryPrompt(State state, EditorSession session)
    {
        session.Tick();
        var prompts = RecoveryPrompts();
        state.Check(prompts.Length == 1, "a newer unsaved group recovery opens exactly one native confirmation dialog per editor session");
        var prompt = prompts[0];
        var buttons = prompt.buttonInfoList.ToArray().Single(info => info.type == UI.UIPopupSystem.ButtonType.OkCancelButton);
        state.Check(buttons.buttonSet.activeInHierarchy && buttons.btnA != null && buttons.btnB != null && prompt.xButton != null,
            "recovery dialog reuses native confirm, cancel and close controls");
        var create = session.Board.treeRoot.parent.Find("NodeGroups toolbar/创建组");
        state.Check(create != null && UIEventListener.Get(create.gameObject).onClick != null,
            "recovery fixture keeps the single creation control available behind the native modal");
        var before = session.Workspace.Snapshot;
        var history = Studio.Scripts.OperationManagement.OperationManager.history.Count;
        UIEventListener.Get(create!.gameObject).onClick.Invoke(create.gameObject);
        state.Check(ReferenceEquals(before, session.Workspace.Snapshot) &&
            Studio.Scripts.OperationManagement.OperationManager.history.Count == history,
            "native recovery modal blocks an underlying creation callback without changing metadata or history");
        return prompt;
    }

    /// <summary>等待原生 Start 及开窗动画完成；后续点击与校验仍走不同 Unity 帧。</summary>
    private static UI.UIPopupSystem? ReadyRecoveryPrompt(State state, EditorSession session)
    {
        if (state.RecoveryDialog == null)
        {
            state.RecoveryDialog = RequireRecoveryPrompt(state, session);
            state.RecoveryDialogOpenedAt = Time.unscaledTime;
            return null;
        }
        return Time.unscaledTime - state.RecoveryDialogOpenedAt >= .5f ? state.RecoveryDialog : null;
    }

    private static void LogRecoveryDialog(State state, EditorSession session, string phase)
    {
        var popup = state.RecoveryDialog;
        var buttons = popup == null ? null : popup.buttonInfoList.ToArray()
            .FirstOrDefault(info => info.type == UI.UIPopupSystem.ButtonType.OkCancelButton);
        string Button(UI.MXButton? button) => button == null ? "missing" :
            $"text={button.label?.text}, active={button.gameObject.activeInHierarchy}, enabled={button.enabled}, disabled={button.disabled}, " +
            $"checkCursor={button.checkCursor}, pressed={button.pressed}, dragged={button.dragged}, onClicked={button.onClicked != null}";
        var details = $"{state.Phase}/{phase}: frame={Time.frameCount}; actionElapsed={Time.unscaledTime - state.RecoveryActionAt:F3}; prompts={RecoveryPrompts().Length}; " +
            $"available={session.RecoveryAvailable}; dirty={session.Dirty}; name={session.Workspace.Snapshot.GetGroup(state.GuiGroupId).Name}; " +
            $"ok=[{Button(buttons?.btnA)}]; cancel=[{Button(buttons?.btnB)}]; x=[{Button(popup?.xButton)}]";
        Plugin.Logger.LogInfo(details);
        var report = Environment.GetEnvironmentVariable("NODEGROUPS_TEST_REPORT")!;
        File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(report)!, "recovery-dialog-diagnostics.txt"), details + Environment.NewLine);
    }

    private static EditorSession.SaveReceipt VerifiedReceipt(EditorSession session, string path) =>
        session.FreezeSave(path) with { SavedHash = EditorSession.Hash(path), SavedVersion = ProjectFileStore.Version(path) };

    private static GroupLoadResult Load(EditorSession session, string path) =>
        new GroupStore().Load(path, session.ProjectId, EditorSession.Hash(path), session.ReadNodes().Select(n => n.Id));

    private static string GroupSignature(IEnumerable<GroupState> groups) => JsonSerializer.Serialize(groups.OrderBy(g => g.Id).Select(g => new
    {
        g.Id, g.Name, Members = g.MemberNodeIds.ToArray(), g.Collapsed, g.Anchor, g.Color, g.ZIndex, g.ExpandedBounds
    }));

    private static bool Near(Point2 a, Point2 b) => Math.Abs(a.X - b.X) < .02 && Math.Abs(a.Y - b.Y) < .02;

    private static void RestorePreviousRecovery(State state)
    {
        if (state.PreviousRecovery != null)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(state.RecoveryFile)!);
            File.WriteAllBytes(state.RecoveryFile, state.PreviousRecovery);
        }
        else if (File.Exists(state.RecoveryFile)) File.Delete(state.RecoveryFile);
    }

    private static void RequireTemporaryFixture(EditorSession session, string path)
    {
        var report = Environment.GetEnvironmentVariable("NODEGROUPS_TEST_REPORT");
        if (string.IsNullOrWhiteSpace(report)) throw new InvalidOperationException("Native storage regressions require the explicit test-process report opt-in.");
        var expected = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(report))!, "runtime-fixture" + ProjectFileStore.ProjectExtension);
        if (!string.Equals(System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase) ||
            ProjectFileStore.Read(path).Project.ProjectName != "NodeGroups connected integration fixture" ||
            ProjectFileStore.Read(path).Project.ProjectId != session.ProjectId)
            throw new InvalidOperationException("Refusing persistence regressions outside the explicit connected temporary fixture.");
    }
}
