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
/// Explicit opt-in integration tests for an independent game process. Uses only
/// a temporary project beneath the report directory and quits that process.
/// Normal launches never create or load the fixture.
/// </summary>
internal sealed class RuntimeTestRunner
{
    private readonly string report;
    private readonly List<string> passed = new();
    private int stage;
    private int frames;
    private string groupId = "";
    private string secondId = "";
    private string firstNode = "";
    private string projectPath = "";
    private string invariant = "";
    private byte[] compiledInvariant = Array.Empty<byte>();
    private float waitUntil;
    private bool done;

    private RuntimeTestRunner(string report) => this.report = Path.GetFullPath(report);
    internal static RuntimeTestRunner? FromEnvironment()
    {
        // AzureArchive routes unknown --arguments through its authoring CLI.
        // Use an inherited variable instead, scoped to the child test process.
        var report = Environment.GetEnvironmentVariable("NODEGROUPS_TEST_REPORT");
        return string.IsNullOrWhiteSpace(report) ? null : new RuntimeTestRunner(report);
    }
    internal void BeforeEditor()
    {
        if (stage != 0 || Time.unscaledTime < 3) return;
        var resources = ScenarioResourceManager.Instance;
        if (resources == null || !resources.AreDbsLoaded) return;
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        projectPath = Path.Combine(Path.GetDirectoryName(report)!, "runtime-fixture" + ProjectFileStore.ProjectExtension);
        var data = NativeProjectFixture.Build();
        // The normal editor initializes its authoring/resource session from an existing
        // project file. Loading only an in-memory DTO leaves GUI saving unavailable.
        ProjectFileStore.Save(projectPath, data, null, false, null);
        PersistentData.projectData = data; PersistentData.projectPath = projectPath;
        stage = 1; SceneManager.LoadScene("ScenarioEditorScene");
        Plugin.Logger.LogInfo("NodeGroups integration fixture scene requested.");
    }
    private void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("Integration assertion failed: " + name);
        passed.Add(name); Plugin.Logger.LogInfo("PASS " + name);
    }
    private static bool Near(Point2 a, Point2 b) => Math.Abs(a.X-b.X) < .02 && Math.Abs(a.Y-b.Y) < .02;
    internal void Tick(EditorSession? session)
    {
        if (done || session == null || stage == 0) return;
        if (++frames < 15) return;
        if (stage == 1)
        {
            var fixture = session.Editor.CaptureProject();
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(report)!, "native-captured.json"), ProjectFileStore.Serialize(fixture));
            Plugin.Logger.LogInfo("Native fixture validation: " + AuthoringGraph.Validate(fixture, "compile").ToString());
            compiledInvariant = AuthoringCompiler.CompileBytes(fixture).ToArray();
            Check(compiledInvariant.Length > 0, "connected fixture compiles through native authoring compiler");
            NativeStyleInspector.Inspect(session, Check);
            GroupCanvasTests.CheckInitialUi(session, Check);
            var branch = session.Board.nodes.ToArray().Select(n => n.TryCast<SelectionNode>()).FirstOrDefault(n => n != null)!;
            Plugin.Logger.LogInfo($"Native options: node={EditorSession.Id(branch)}, count={branch.Options.Count}; " + string.Join("; ", branch.Options.ToArray().Select(o => o.OptionId + " -> " + ChoiceInterop.TargetId(o))));
            Check(branch.Options.Count == 2 && branch.Options[0].OptionId != branch.Options[1].OptionId &&
                string.Equals(ChoiceInterop.TargetId(branch.Options[0]), ChoiceInterop.TargetId(branch.Options[1]), StringComparison.Ordinal), "two option identities share one native target");
            var initialCount = session.Board.nodes.Count;
            var id = Guid.NewGuid().ToString("D");
            var native = new NodeCreateOperation(Il2CppType.Of<ScriptNode>(), NativeGuid.Parse(id), new Vector2(-220, 0));
            Check(session.Board.nodes.Count == initialCount, "native creation constructor has no side effects");
            native.Redo();
            Check(session.Find(id) != null && session.Board.nodes.Count == initialCount + 1, "native Redo creates one GUID");
            OperationManager.WriteIsolated(native);
            Check(session.Board.nodes.Count == initialCount + 1, "native history insertion does not repeat Redo");
            var otherId = Guid.NewGuid().ToString("D");
            var other = new NodeCreateOperation(Il2CppType.Of<ScriptNode>(), NativeGuid.Parse(otherId), new Vector2(120, -200));
            other.Redo(); OperationManager.WriteIsolated(other);
            firstNode = EditorSession.Id(session.Board.nodes.ToArray().First(n => n.TryCast<ScriptNode>()?.scripts.Count > 0));
            Blackboard.selected.Clear(); Blackboard.selected.Add(session.Find(firstNode)!); Blackboard.selected.Add(branch);
            session.CreateGroup(false); groupId = session.SelectedGroup!;
            Check(session.Workspace.Snapshot.GetGroup(groupId).MemberNodeIds.Count == 2, "create group from selected native nodes");
            invariant = ProjectFileStore.Serialize(session.Editor.CaptureProject());
            session.Toggle(groupId); session.EnforceVisibility();
            Check(ProjectFileStore.Serialize(session.Editor.CaptureProject()) == invariant, "collapse preserves exact native project snapshot");
            Check(session.Find(firstNode)!.gameObject.activeSelf, "collapse retains active native node GameObject");
            Check(session.Find(firstNode)!.GetComponentsInChildren<UIWidget>(true).All(w => !w.enabled), "collapsed member widgets hidden");
            Check(AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray().SequenceEqual(compiledInvariant), "collapsed connected graph compiles to identical bytes");
            var outgoing = branch.connectionsTo[0].line;
            Check(session.IsHidden(outgoing), "external original line is hidden and excluded from cutter");
            Check(branch.Options.ToArray().All(option => session.Board.treeRoot.GetComponentsInChildren<Line>(true)
                .Any(line => line.gameObject.activeInHierarchy && !line.enabled &&
                    line.gameObject.name.EndsWith("/" + option.OptionId, StringComparison.Ordinal))),
                "same-target choice options retain separate projected identities without extra labels");
            OperationManager.Undo();
            Check(!session.Workspace.Snapshot.GetGroup(groupId).Collapsed, "native Undo executes managed group command");
            OperationManager.Redo();
            Check(session.Workspace.Snapshot.GetGroup(groupId).Collapsed, "native Redo executes managed group command");
            session.Toggle(groupId); session.EnforceVisibility();
            Check(ProjectFileStore.Serialize(session.Editor.CaptureProject()) == invariant, "expand preserves exact native project snapshot");
            Check(!session.IsHidden(outgoing), "expand restores original line interaction");
            var untouched = session.Find(otherId)!;
            untouched.Move(new Vector3(24, 0, 0), false, true); var untouchedPosition = session.Position(untouched);
            session.Rename(groupId, "测试节点组 / 序章");
            Check(Near(session.Position(untouched), untouchedPosition), "metadata rename preserves a native move before geometry polling");
            session.CreateGroup(true); secondId = session.SelectedGroup!;
            var secondAnchor = session.Workspace.Snapshot.GetGroup(secondId).Anchor;
            session.Commit(session.Workspace.MoveGroup(secondId, new Point2(520, 160) - secondAnchor));
            var node = session.CreateMember(secondId, 0); var createdId = EditorSession.Id(node);
            Check(session.Workspace.Snapshot.GetGroup(secondId).MemberNodeIds.Contains(createdId), "create native node directly in group");
            OperationManager.Undo();
            Check(session.Find(createdId) == null && !session.Workspace.Snapshot.GetGroup(secondId).MemberNodeIds.Contains(createdId), "one Undo removes native creation and membership");
            OperationManager.Redo();
            Check(session.Find(createdId) != null && session.Workspace.Snapshot.GetGroup(secondId).MemberNodeIds.Contains(createdId), "one Redo restores creation with same GUID");
            var original = session.Position(session.Find(firstNode)!);
            var historyCount = OperationManager.history.Count;
            session.BeginNodeDrag(session.Find(firstNode)!);
            session.Find(firstNode)!.Move(new Vector3(1600, 0, 0), false, true);
            session.PreviewNodeDrag(); session.EndNodeDrag();
            Check(session.Workspace.Snapshot.FindGroupForNode(firstNode) == null, "drag out of frozen auto-bounds removes membership");
            Check(OperationManager.history.Count == historyCount + 1, "drag position and membership use one history entry");
            OperationManager.Undo();
            Plugin.Logger.LogInfo($"Drag undo position: {session.Position(session.Find(firstNode)!)}, expected: {original}, group: {session.Workspace.Snapshot.FindGroupForNode(firstNode)?.Id}, expected group: {groupId}");
            Check(session.Workspace.Snapshot.FindGroupForNode(firstNode)?.Id == groupId && Near(session.Position(session.Find(firstNode)!), original), "one Undo restores drag position and ownership");
            var targetBounds = session.DropBounds(session.Workspace.Snapshot.GetGroup(secondId));
            var target = new Point2(targetBounds.X + targetBounds.Width / 2, targetBounds.Y + targetBounds.Height / 2);
            Blackboard.selected.Clear(); Blackboard.selected.Add(session.Find(firstNode)!);
            session.BeginNodeDrag(session.Find(firstNode)!); var delta = target - session.Position(session.Find(firstNode)!);
            session.Find(firstNode)!.Move(new Vector3((float)delta.X, (float)delta.Y, 0), false, true); session.EndNodeDrag();
            Check(session.Workspace.Snapshot.FindGroupForNode(firstNode)?.Id == secondId, "drag across groups transfers ownership");
            session.BeginNodeDrag(session.Find(firstNode)!); session.Find(firstNode)!.Move(new Vector3(1400,0,0),false,true); session.CancelDrag();
            Check(session.Workspace.Snapshot.FindGroupForNode(firstNode)?.Id == secondId && Near(session.Position(session.Find(firstNode)!), target), "cancel restores native drag preview and ownership");
            session.EndNodeDrag();
            var beforeMove = session.Position(session.Find(firstNode)!);
            var compileDeltaBeforeMove = OperationManager.compileDelta;
            session.Commit(session.Workspace.MoveGroup(secondId, new Point2(40,-30)));
            Check(Near(session.Position(session.Find(firstNode)!), beforeMove + new Point2(40,-30)), "group move routes through native Node.Move");
            Check(OperationManager.compileDelta == compileDeltaBeforeMove, "group geometry command does not mark native compilation dirty");
            OperationManager.Undo();
            Check(Near(session.Position(session.Find(firstNode)!), beforeMove), "native Undo restores group move");
            // A live native authoring session owns the project lock. Exercise its
            // original GUI save instead of calling a competing low-level write.
            session.Toggle(groupId); session.Editor.Save(true);
            // A native save may finish asynchronously. Verify its completed receipt in a later stage.
            stage = 2; frames = 0; waitUntil = Time.unscaledTime + 1;
        }
        else if (stage == 2 && Time.unscaledTime > waitUntil)
        {
            session.Tick();
            if (session.Dirty && Time.unscaledTime < waitUntil + 10) return;
            var store = new global::NodeGroups.Storage.GroupStore();
            var load = store.Load(projectPath, session.ProjectId, EditorSession.Hash(projectPath), session.ReadNodes().Select(n => n.Id));
            Plugin.Logger.LogInfo($"Sidecar load: {load.Status}; {string.Join("; ", load.Diagnostics.Select(d => d.Code + ": " + d.Message))}");
            Check(load.Snapshot != null && load.Snapshot.Groups.Count == 2, "saved groups reopen with matching native project identity");
            Check(load.Snapshot!.Groups.Any(g => g.Name == "测试节点组 / 序章"), "renamed group survives save");
            Plugin.Logger.LogInfo($"Group save state: dirty={session.Dirty}; coreDirty={OperationManager.HasChangesSinceLastSave}; path={PersistentData.projectPath}; authoringPath={AuthoringEditorSession.Current?.FilePath}; pending={AuthoringEditorSession.Current?.SaveInProgress}");
            Check(load.Snapshot.Groups.Single(g => g.Id == groupId).Collapsed == session.Workspace.Snapshot.GetGroup(groupId).Collapsed && !session.Dirty, "group-only save persists final collapse state and clears dirty");
            Check(load.Status == global::NodeGroups.Storage.GroupLoadStatus.Loaded, "sidecar hash matches successfully saved native version");
            Check(AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray().SequenceEqual(compiledInvariant), "native compile bytes unchanged after transfers and saves");
            if (Environment.GetEnvironmentVariable("NODEGROUPS_TEST_FOCUS") == "trash")
            {
                TrashMembershipTests.Begin(session, Check); stage = 13;
            }
            else if (Environment.GetEnvironmentVariable("NODEGROUPS_TEST_FOCUS") == "quitconfirm")
            {
                stage = 12; QuitConfirmationTests.Begin(session, Check, FinishQuitConfirmation, report);
            }
            else if (Environment.GetEnvironmentVariable("NODEGROUPS_TEST_FOCUS") == "savestate")
            {
                SaveStateIntegrationTests.Begin(session, Check); stage = 11;
            }
            else if (Environment.GetEnvironmentVariable("NODEGROUPS_TEST_FOCUS") == "stress")
            {
                RuntimeStressTests.Begin(session, Check); stage = 10;
            }
            else if (Environment.GetEnvironmentVariable("NODEGROUPS_TEST_FOCUS") == "hover")
            {
                DropTargetFeedbackTests.Run(session, Check); stage = 3;
            }
            else if (Environment.GetEnvironmentVariable("NODEGROUPS_TEST_FOCUS") == "visual")
            {
                BorderHitTargetTests.BeginVisual(session); stage = 8;
            }
            else if (Environment.GetEnvironmentVariable("NODEGROUPS_TEST_FOCUS") == "palette")
            {
                // Diagnose the actual palette gesture independently of UI-only RED checks.
                PaletteDragTests.Begin(session, Check); stage = 9;
            }
            else if (Environment.GetEnvironmentVariable("NODEGROUPS_TEST_FOCUS") == "gestures")
            {
                DropTargetFeedbackTests.Run(session, Check);
                BorderHitTargetTests.Run(session, Check);
                GroupControlsTests.Run(session, Check);
                NativeCommandIntegrationTests.Run(session, Check); GroupCanvasTests.Run(session, Check);
                GroupCanvasTests.BeginRaycasts(session, Check); stage = 6;
            }
            else { PersistenceLifecycleTests.Run(session, projectPath, Check); stage = 4; }
            frames = 0;
        }
        else if (stage == 4 && PersistenceLifecycleTests.Tick(session))
        {
            DropTargetFeedbackTests.Run(session, Check);
            BorderHitTargetTests.Run(session, Check);
            GroupControlsTests.Run(session, Check);
            NativeCommandIntegrationTests.Run(session, Check);
            GroupCanvasTests.Run(session, Check);
            GroupCanvasTests.BeginRaycasts(session, Check);
            stage = 6; frames = 0;
        }
        else if (stage == 6 && GroupCanvasTests.TickRaycasts(session, Check))
        { GroupCanvasTests.BeginLifecycle(session, Check); stage = 5; frames = 0; }
        else if (stage == 5 && GroupCanvasTests.TickLifecycle(session, Check))
        { GroupControlsTests.BeginMotion(session, Check); stage = 7; frames = 0; }
        else if (stage == 7 && GroupControlsTests.TickMotion(session, Check, name => CapturePreview(name)))
        { BorderHitTargetTests.BeginVisual(session); stage = 8; frames = 0; }
        else if (stage == 8 && BorderHitTargetTests.TickVisual(session, Check, name => CapturePreview(name)))
        {
            if (Environment.GetEnvironmentVariable("NODEGROUPS_TEST_FOCUS") == "visual") stage = 3;
            else { PaletteDragTests.Begin(session, Check); stage = 9; }
            frames = 0;
        }
        else if (stage == 9 && PaletteDragTests.Tick(session, Check, name => CapturePreview(name)))
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NODEGROUPS_TEST_FOCUS")))
            { RuntimeStressTests.Begin(session, Check); stage = 10; }
            else stage = 3;
            frames = 0;
        }
        else if (stage == 10 && RuntimeStressTests.Tick(session, Check))
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NODEGROUPS_TEST_FOCUS")))
            { SaveStateIntegrationTests.Begin(session, Check); stage = 11; }
            else stage = 3;
            frames = 0;
        }
        else if (stage == 11 && SaveStateIntegrationTests.Tick(session, Check))
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NODEGROUPS_TEST_FOCUS")))
            { TrashMembershipTests.Begin(session, Check); stage = 13; }
            else stage = 3;
            frames = 0;
        }
        else if (stage == 12) QuitConfirmationTests.Tick(session);
        else if (stage == 13 && TrashMembershipTests.Tick(session, Check)) { stage = 3; frames = 0; }
        else if (stage == 3 && frames > 20) { CapturePreview(); Finish(true, null); }
    }

    /// <summary>
    /// Hidden test windows can have a black back buffer. Render the actual native
    /// cameras into an offscreen target, preserving all camera/texture state.
    /// </summary>
    private void CapturePreview(string? suffix = null)
    {
        var board = GroupHost.Session?.Board;
        if (board != null) Plugin.Logger.LogInfo($"Preview tree: position={board.treeRoot.position}, scale={board.treeRoot.lossyScale}, layer={board.treeRoot.gameObject.layer}; " + string.Join("; ", board.treeRoot.GetComponentsInParent<UIPanel>(true).Select(p => p.name + ": alpha=" + p.alpha)));
        Plugin.Logger.LogInfo("All native cameras: " + string.Join("; ", Camera.allCameras.ToArray().Select(c => $"{c.name}, depth={c.depth}, mask={c.cullingMask}, target={c.targetTexture?.name}")));
        // Preview/character cameras also clear their targets and must not erase the UI image.
        var uiCamera = board == null ? UICamera.mainCamera : UICamera.FindCameraForLayer(board.treeRoot.gameObject.layer)?.GetComponent<Camera>();
        var cameras = uiCamera == null ? Array.Empty<Camera>() : new[] { uiCamera };
        Plugin.Logger.LogInfo("Preview cameras: " + string.Join("; ", cameras.Select(camera => $"{camera.name}, depth={camera.depth}, mask={camera.cullingMask}, rect={camera.rect}, clear={camera.clearFlags}")));
        var target = new RenderTexture(1920, 1080, 24);
        var previous = RenderTexture.active;
        var texture = new Texture2D(1920, 1080, TextureFormat.RGB24, false);
        try
        {
            target.Create();
            foreach (var camera in cameras)
            {
                var original = camera.targetTexture;
                try { camera.targetTexture = target; camera.Render(); }
                finally { camera.targetTexture = original; }
            }
            RenderTexture.active = target;
            texture.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0); texture.Apply();
            var previewPath = suffix == null ? Path.ChangeExtension(report, ".png") :
                Path.Combine(Path.GetDirectoryName(report)!, Path.GetFileNameWithoutExtension(report) + "-" + suffix + ".png");
            File.WriteAllBytes(previewPath, ImageConversion.EncodeToPNG(texture).ToArray());
        }
        finally
        {
            RenderTexture.active = previous;
            target.Release(); UnityEngine.Object.Destroy(target); UnityEngine.Object.Destroy(texture);
        }
    }

    internal void Fail(Exception error)
    {
        if (done) return;
        TrashMembershipTests.Abort(GroupHost.Session);
        QuitConfirmationTests.Abort();
        SaveStateIntegrationTests.Abort(GroupHost.Session);
        RuntimeStressTests.Abort(GroupHost.Session);
        PersistenceLifecycleTests.Abort();
        GroupCanvasTests.AbortLifecycle();
        GroupControlsTests.AbortMotion(GroupHost.Session);
        PaletteDragTests.Abort(GroupHost.Session);
        BorderHitTargetTests.AbortVisual(GroupHost.Session);
        try { CapturePreview(); }
        catch (Exception previewError) { Plugin.Logger.LogWarning("Test preview failed: " + previewError.Message); }
        Finish(false, error.ToString());
    }
    private void Finish(bool success, string? error)
    {
        done = true;
        WriteReport(success, error);
        // 测试结束销毁自己拥有的夹具会话，再退出隔离进程；产品的关闭前确认已在专项中单独验证。
        // 失败时可能仍有未保存数据，不能让正式保护逻辑把测试退出改成等待人工点击。
        try { GroupHost.Session?.Dispose(); }
        finally { GroupHost.Session = null; }
        Application.Quit(success ? 0 : 1);
    }

    /// <summary>确认退出专项记录关闭批准，再由父进程核对真实退出；不能提前 Dispose 绕过产品保护。</summary>
    private void FinishQuitConfirmation(bool success, string? error)
    {
        done = true;
        WriteReport(success, error);
    }

    private void WriteReport(bool success, string? error)
    {
        // Identify the actual loaded build, including the SDK's source revision;
        // a plugin version alone cannot distinguish two builds of the same release.
        var buildVersion = typeof(Plugin).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().SingleOrDefault()?.InformationalVersion;
        File.WriteAllText(report, JsonSerializer.Serialize(new { success, passed, error, pluginVersion = Plugin.Version, buildVersion, testBuild = true }, new JsonSerializerOptions { WriteIndented = true }));
        Plugin.Logger.LogInfo($"NodeGroups integration finished: {success}, {passed.Count} assertions.");
    }
}
