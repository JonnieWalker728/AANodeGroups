using AzureArchive.Automation;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using NodeGroups.Core;
using Studio.Scripts;
using Studio.Scripts.Nodes;
using Studio.Scripts.OperationManagement;
using System.Text;
using System.Text.Json;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AzureArchive.NodeGroups.Runtime.Tests;

/// <summary>
/// Exercises real save and scene-reload lifecycles, empty-editor font fallback and destruction
/// of borrowed style sources. Tracks replacement sessions and restores the original saved fixture.
/// </summary>
internal static partial class GroupCanvasTests
{
    private enum LifecyclePhase { SaveMain, AwaitMinimal, EmptyGroup, HelperStart, SourceDestroyed, OwnedViews, SaveMinimal, AwaitMain }

    /// <summary>Owns only the independent test scene and its UI resources; main-project values are frozen before leaving its saved scene.</summary>
    private sealed class LifecycleState
    {
        internal EditorSession Owner = null!;
        internal string MainPath = "", MainProjectId = "", MainStory = "", MainGroups = "";
        internal NodeState[] MainNodes = Array.Empty<NodeState>();
        internal byte[] MainCompiled = Array.Empty<byte>();
        internal string? MainSelectedGroup;
        internal Vector3 MainTreePosition, MainTreeScale;
        internal LifecyclePhase Phase;
        internal DateTime Deadline = DateTime.UtcNow.AddSeconds(40);
        internal int Frames;
        internal string MinimalPath = "", MinimalProjectId = "", MinimalStory = "", HelperId = "", GroupId = "";
        internal NodeState[] MinimalNodes = Array.Empty<NodeState>();
        internal byte[] MinimalCompiled = Array.Empty<byte>();
        internal int MinimalHistory;
        internal ScriptNode? Source;
        internal NativeVisualStyle? Style;
        internal GameObject? Views, Effect;
        internal UISprite? Sprite;
        internal UILabel? Label;
    }
    private static LifecycleState? lifecycle;

    /// <summary>Saves the main isolated fixture before testing empty-editor fonts and genuine source destruction in a separate minimal scene.</summary>
    /// <param name="session">The isolated test project's live session.</param>
    /// <param name="check">Assertion reporter which throws on failure.</param>
    /// <remarks>TickLifecycle must continue across replacement EditorSession owners. Normal scene reload clears native history; only the minimal scene's temporary commands are undone.</remarks>
    internal static void BeginLifecycle(EditorSession session, Action<bool, string> check)
    {
        var report = Environment.GetEnvironmentVariable("NODEGROUPS_TEST_REPORT");
        if (string.IsNullOrWhiteSpace(report) || lifecycle != null)
            throw new InvalidOperationException("Native style lifetime checks require one explicitly isolated test process.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(report))!;
        var path = Path.GetFullPath(PersistentData.projectPath);
        var expected = Path.Combine(directory, "runtime-fixture" + ProjectFileStore.ProjectExtension);
        if (!string.Equals(path, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Lifecycle scenes are restricted to the explicitly generated temporary main fixture.");
        CheckFrozenTemplates(session, check);
        lifecycle = new LifecycleState
        {
            Owner = session, MainPath = path, MainProjectId = session.ProjectId,
            MainNodes = session.ReadNodes(), MainStory = CanonicalStory(session),
            MainCompiled = AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray(),
            MainSelectedGroup = session.SelectedGroup,
            MainTreePosition = session.Board.treeRoot.position, MainTreeScale = session.Board.treeRoot.localScale,
            MinimalPath = Path.Combine(directory, "runtime-minimal-lifecycle" + ProjectFileStore.ProjectExtension),
            Phase = LifecyclePhase.SaveMain
        };
        session.Editor.Save(true); // The live native authoring session owns this file's lease.
    }

    /// <summary>Advances real save, scene-load, source-creation and destruction frames without editing the main graph's registry.</summary>
    /// <returns>True only after the saved main scene has been normally reopened and every original graph/group invariant has passed.</returns>
    internal static bool TickLifecycle(EditorSession session, Action<bool, string> check)
    {
        var state = lifecycle ?? throw new InvalidOperationException("Native style lifetime checks were not started.");
        if (DateTime.UtcNow > state.Deadline) throw new TimeoutException("Native lifecycle fixture timed out in " + state.Phase);
        if (session.IsDisposed) return false;
        state.Frames++;
        if (state.Phase == LifecyclePhase.SaveMain)
        {
            if (!ReferenceEquals(state.Owner, session) || !LifecycleSaveCompleted(session)) return false;
            var saved = new global::NodeGroups.Storage.GroupStore().Load(state.MainPath, state.MainProjectId,
                EditorSession.Hash(state.MainPath), state.MainNodes.Select(node => node.Id));
            var currentGroups = LifecycleGroupSignature(session.Workspace.Snapshot.Groups);
            check(saved.Status == global::NodeGroups.Storage.GroupLoadStatus.Loaded && saved.Snapshot != null &&
                LifecycleGroupSignature(saved.Snapshot.Groups) == currentGroups &&
                CanonicalStory(ProjectFileStore.Read(state.MainPath).Project) == state.MainStory &&
                CanonicalStory(session) == state.MainStory,
                "iteration 2 main fixture native save and matching groups finish before its independent lifecycle scene");
            state.MainGroups = currentGroups; // Freeze the completed current save, including later selection-driven layers.
            state.MainSelectedGroup = session.SelectedGroup;
            var minimal = NativeProjectFixture.BuildMinimal(); state.MinimalProjectId = minimal.ProjectId;
            if (File.Exists(state.MinimalPath)) throw new InvalidOperationException("The lifecycle's independent minimal path already exists.");
            ProjectFileStore.Save(state.MinimalPath, minimal, null, false, null);
            RequestLifecycleScene(state, session, state.MinimalPath, LifecyclePhase.AwaitMinimal);
            return false;
        }
        if (state.Phase == LifecyclePhase.AwaitMinimal)
        {
            if (ReferenceEquals(state.Owner, session) || session.ProjectId != state.MinimalProjectId) return false;
            state.Owner = session;
            state.MinimalNodes = session.ReadNodes(); state.MinimalStory = CanonicalStory(session);
            state.MinimalCompiled = AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray();
            state.MinimalHistory = OperationManager.history.Count;
            check(state.MinimalNodes.Length == 2 && session.Board.nodes.ToArray().All(node => node.TryCast<ScriptNode>() == null) &&
                state.MinimalCompiled.Length > 0 && session.Workspace.Snapshot.Groups.Count == 0,
                "iteration 2 normal scene load creates an independent Entry-to-Exit editor with no live ScriptNode");
            CheckNoScriptFontFallback(session, check);
            Blackboard.selected.Clear(); session.CreateGroup(false); state.GroupId = session.SelectedGroup!;
            session.Rename(state.GroupId, "无脚本中文节点组"); session.Tick();
            SetLifecyclePhase(state, LifecyclePhase.EmptyGroup);
            return false;
        }
        if (state.Phase == LifecyclePhase.AwaitMain)
        {
            if (ReferenceEquals(state.Owner, session) || session.ProjectId != state.MainProjectId) return false;
            session.Tick();
            check(session.Board.nodes.Count == state.MainNodes.Length && NodesUnchanged(session, state.MainNodes) &&
                CanonicalStory(session) == state.MainStory &&
                AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray().SequenceEqual(state.MainCompiled),
                "iteration 2 normal main-scene reopen restores every original GUID, position, connection and compile byte");
            check(!session.Dirty && LifecycleGroupSignature(session.Workspace.Snapshot.Groups) == state.MainGroups &&
                string.Equals(Path.GetFullPath(PersistentData.projectPath), state.MainPath, StringComparison.OrdinalIgnoreCase),
                "iteration 2 main-scene reopen restores saved group membership, names, regions, layers and original project path");
            session.Board.treeRoot.position = state.MainTreePosition; session.Board.treeRoot.localScale = state.MainTreeScale;
            session.SelectedGroup = state.MainSelectedGroup; session.Tick();
            lifecycle = null;
            return true;
        }
        if (!ReferenceEquals(state.Owner, session)) return false;
        if (state.Frames < 2) return false;
        if (state.Phase == LifecyclePhase.EmptyGroup)
        {
            CheckLocaleGroupTitle(session, state.GroupId, check, "empty editor");
            UndoClickOperations(session, state.MinimalHistory);
            state.HelperId = Guid.NewGuid().ToString("D");
            var creation = new NodeCreateOperation(Il2CppType.Of<ScriptNode>(), NativeGuid.Parse(state.HelperId), new Vector2(-100, 0));
            creation.Redo(); OperationManager.WriteIsolated(creation); session.Tick();
            state.Source = session.Find(state.HelperId)?.TryCast<ScriptNode>();
            check(state.Source != null && OperationManager.history.Count == state.MinimalHistory + 1,
                "iteration 2 original native creation introduces one unconnected helper in the independent scene");
            SetLifecyclePhase(state, LifecyclePhase.HelperStart);
        }
        else if (state.Phase == LifecyclePhase.HelperStart)
        {
            var source = state.Source ?? throw new InvalidOperationException("Native source fixture lost its initialized helper.");
            var liveScripts = session.Board.nodes.ToArray().Select(node => node.TryCast<ScriptNode>()).Where(node => node != null).ToArray();
            check(liveScripts.Length == 1 && liveScripts[0]!.Pointer == source.Pointer && source.isDeletable &&
                source.connectionsFrom.Count == 0 && source.connectionsTo.Count == 0,
                "iteration 2 fresh-style construction has exactly the intended initialized helper as its sole live ScriptNode font source");
            state.Style = new NativeVisualStyle(session);
            check(state.Style.NodeFont.Pointer != source.nameLabel.Pointer &&
                SameObject(state.Style.NodeFont.trueTypeFont, source.nameLabel.trueTypeFont) &&
                SameObject(state.Style.NodeFont.material, source.nameLabel.material),
                "iteration 2 helper typography is frozen into an independently owned label before source destruction");
            OperationManager.Undo(); session.Tick(); // NodeCreateOperation.Undo uses the original DeleteImmediate entry point.
            check(session.Find(state.HelperId) == null && OperationManager.history.Count == state.MinimalHistory,
                "iteration 2 original creation Undo unregisters its helper through native DeleteImmediate");
            SetLifecyclePhase(state, LifecyclePhase.SourceDestroyed);
        }
        else if (state.Phase == LifecyclePhase.SourceDestroyed)
        {
            check(state.Source == null && session.Find(state.HelperId) == null && session.Board.nodes.Count == 2,
                "iteration 2 the fresh style's actual helper source is destroyed after genuine Unity frame boundaries");
            state.Views = new GameObject("Iteration 2 post-destruction owned visuals") { layer = session.Board.treeRoot.gameObject.layer };
            state.Views.transform.SetParent(session.Board.treeRoot, false); state.Views.transform.localPosition = new Vector3(-150, 250, 0);
            state.Sprite = NativeVisualStyle.Sprite(state.Views, state.Style!.Body, 658, 140, 400);
            var title = new GameObject("Post-destruction Chinese title") { layer = state.Views.layer };
            title.transform.SetParent(state.Views.transform, false);
            state.Label = NativeVisualStyle.Label(title, state.Style.NodeFont, 658, 140, 402, "销毁后节点组");
            state.Effect = state.Style.SelectionEffect(state.Views.transform);
            Blackboard.selected.Clear(); session.CreateGroup(false); state.GroupId = session.SelectedGroup!;
            session.Rename(state.GroupId, "销毁后中文节点组"); session.Tick();
            SetLifecyclePhase(state, LifecyclePhase.OwnedViews);
        }
        else if (state.Phase == LifecyclePhase.OwnedViews)
        {
            check(state.Sprite!.isValid && state.Label!.isVisible && state.Label.geometry.hasVertices && state.Label.geometry.verts.Count > 0 &&
                state.Label.trueTypeFont.HasCharacter('组') && Math.Abs(state.Label.color.a - 1) < .001 &&
                state.Effect!.GetComponent<UISprite>().isValid && state.Effect.GetComponentsInChildren<UIWidget>(true).All(widget => widget.enabled),
                "iteration 2 the source-frozen fresh style renders native sprite, Chinese geometry and selection resources after actual source destruction");
            CheckLocaleGroupTitle(session, state.GroupId, check, "post-destruction editor");
            UndoClickOperations(session, state.MinimalHistory);
            DisposeLifecycleViews(state);
            check(OperationManager.history.Count == state.MinimalHistory && session.Workspace.Snapshot.Groups.Count == 0 &&
                NodesUnchanged(session, state.MinimalNodes) && CanonicalStory(session) == state.MinimalStory &&
                AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray().SequenceEqual(state.MinimalCompiled),
                "iteration 2 minimal lifecycle commands undo to the original Entry-to-Exit graph, compile bytes and local history boundary");
            session.Save(); SetLifecyclePhase(state, LifecyclePhase.SaveMinimal);
        }
        else if (state.Phase == LifecyclePhase.SaveMinimal)
        {
            if (!LifecycleSaveCompleted(session)) return false;
            RequestLifecycleScene(state, session, state.MainPath, LifecyclePhase.AwaitMain);
        }
        return false;
    }

    private static bool LifecycleSaveCompleted(EditorSession session)
    {
        session.Tick();
        return AuthoringEditorSession.Current is { SaveInProgress: false } authoring &&
            authoring.ProjectId == session.ProjectId && !session.Dirty;
    }

    private static void SetLifecyclePhase(LifecycleState state, LifecyclePhase phase)
    { state.Phase = phase; state.Frames = 0; state.Deadline = DateTime.UtcNow.AddSeconds(40); }

    private static void RequestLifecycleScene(LifecycleState state, EditorSession session, string path, LifecyclePhase phase)
    {
        var data = ProjectFileStore.Read(path).Project;
        session.Dispose(); GroupHost.Session = null;
        SetLifecyclePhase(state, phase);
        PersistentData.projectData = data; PersistentData.projectPath = path;
        SceneManager.LoadScene("ScenarioEditorScene");
    }

    private static void DisposeLifecycleViews(LifecycleState state)
    {
        if (state.Views != null) { state.Views.SetActive(false); UnityEngine.Object.Destroy(state.Views); state.Views = null; }
        state.Style?.Dispose(); state.Style = null;
    }

    /// <summary>Releases fixture-owned UI resources if the isolated runtime assertion reporter aborts a lifecycle phase.</summary>
    internal static void AbortLifecycle()
    {
        if (lifecycle is { } state) DisposeLifecycleViews(state);
        lifecycle = null;
    }

    private static string LifecycleGroupSignature(IEnumerable<GroupState> groups) => JsonSerializer.Serialize(groups.OrderBy(group => group.Id).Select(group => new
    { group.Id, group.Name, Members = group.MemberNodeIds.ToArray(), group.Collapsed, group.Anchor, group.Color, group.ZIndex, group.ExpandedBounds }));

}
