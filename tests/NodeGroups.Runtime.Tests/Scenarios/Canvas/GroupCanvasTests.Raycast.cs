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
/// Observes actual NGUI raycasts after normal Unity frames, including overlapping groups
/// and inspector closure. Restores borrowed camera, pointer, canvas and selection state.
/// </summary>
internal static partial class GroupCanvasTests
{
    private sealed record RaycastState(EditorSession Owner, int HistoryBoundary, NodeState[] Nodes, string Story,
        Vector3 TreePosition, Node[] SelectedNodes, string? SelectedGroup, GameObject? SelectedObject,
        Camera? Camera, UICamera? UiCamera, GameObject? Hovered, RaycastHit Hit, Vector3 WorldPosition,
        string TopGroup, Collider[] Targets, int Index = 0, int Frames = 0, bool CanvasReady = false);
    private static RaycastState? raycasts;

    /// <summary>Creates overlapping groups and begins real NGUI hit checks after their normal Start/LateUpdate lifecycle.</summary>
    internal static void BeginRaycasts(EditorSession session, Action<bool, string> check)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NODEGROUPS_TEST_REPORT")) || raycasts != null)
            throw new InvalidOperationException("Cross-frame native raycasts require one explicitly isolated fixture.");
        ReturnToNativeCanvas(session, check);
        var boundary = OperationManager.history.Count; var nodes = session.ReadNodes(); var story = CanonicalStory(session);
        var treePosition = session.Board.treeRoot.position; var selectedNodes = Blackboard.selected.ToArray();
        var group = session.SelectedGroup; var selectedObject = UICamera.selectedObject;
        var camera = UICamera.currentCamera; var uiCamera = UICamera.current;
        var hovered = UICamera.hoveredObject; var hit = UICamera.lastHit; var world = UICamera.lastWorldPosition;
        var source = session.Board.nodes.ToArray().First(node => node.TryCast<ScriptNode>() != null);
        Blackboard.selected.Clear(); Blackboard.selected.Add(source); session.CreateGroup(false);
        var topId = session.SelectedGroup!; var bounds = InitialRegion(session, source);
        session.Commit(session.Workspace.SetExpandedBounds(topId, bounds));
        Blackboard.selected.Clear(); session.CreateGroup(false);
        session.Commit(session.Workspace.SetExpandedBounds(session.SelectedGroup!, bounds));
        session.SelectGroup(topId); session.Tick();
        var root = GroupRoot(session, topId);
        var targets = new[] { root.Find("Header")!.GetComponent<Collider>() }
            .Concat(new[] { 1, 2, 4, 5, 6 }.Select(mask => root.Find("Resize " + mask)!.GetComponent<Collider>()))
            .Append(source.GetComponent<Collider>()).ToArray();
        check(targets.All(target => target != null && target.enabled),
            "retained native hit fixture has a live header, five current resize handles and the original node collider");
        raycasts = new RaycastState(session, boundary, nodes, story, treePosition, selectedNodes, group, selectedObject,
            camera, uiCamera, hovered, hit, world, topId, targets);
        PanToRaycastTarget(session, targets[0], treePosition);
    }

    /// <summary>Waits two genuine Unity frames after each pan, then checks native hit sorting and frame-interior passthrough.</summary>
    internal static bool TickRaycasts(EditorSession session, Action<bool, string> check)
    {
        var state = raycasts ?? throw new InvalidOperationException("Cross-frame native raycasts were not started.");
        if (!ReferenceEquals(state.Owner, session) || session.IsDisposed) throw new InvalidOperationException("Native raycast fixture changed editor owner.");
        state = state with { Frames = state.Frames + 1 }; raycasts = state;
        if (!state.CanvasReady)
        {
            var closed = NativeInspectorClosed();
            if (!closed)
            {
                if (state.Frames >= 45) check(false,
                    "iteration 2 native inspector finishes its normal close before canvas hit testing");
                return false;
            }
            check(closed, "iteration 2 canvas raycasts start after the native inspector has normally closed");
            state = state with { CanvasReady = true, Frames = 0 }; raycasts = state;
        }
        if (state.Frames < 2) return false;
        var target = state.Targets[state.Index]; var hit = ObserveRaycast(session, target);
        if (state.Index == 0)
            check(hit != null && hit.transform.IsChildOf(GroupRoot(session, state.TopGroup)),
                "iteration 2 actual NGUI raycast resolves overlapping group headers to the selected top group");
        else if (state.Index < state.Targets.Length - 1)
            check(hit != null && hit.Pointer == target.gameObject.Pointer,
                "iteration 2 actual NGUI raycast resolves top-group " + target.name);
        else check(hit != null && (hit.Pointer == target.gameObject.Pointer || hit.transform.IsChildOf(target.transform)),
                "iteration 2 expanded frame interior passes the actual NGUI raycast to its original native node");
        var next = state.Index + 1;
        if (next < state.Targets.Length)
        {
            raycasts = state with { Index = next, Frames = 0 };
            PanToRaycastTarget(session, state.Targets[next], state.TreePosition);
            return false;
        }

        CheckCreateEntryEmptyArea(session, check);
        session.Board.treeRoot.position = state.TreePosition; Physics.SyncTransforms();
        var guard = OperationManager.history.Count - state.HistoryBoundary + 8;
        while (OperationManager.history.Count > state.HistoryBoundary && guard-- > 0) OperationManager.Undo();
        Blackboard.selected.Clear();
        foreach (var selected in state.SelectedNodes)
            if (selected != null && session.Find(EditorSession.Id(selected)) != null) Blackboard.selected.Add(selected);
        session.SelectedGroup = state.SelectedGroup; session.Tick();
        UICamera.selectedObject = state.SelectedObject; UICamera.currentCamera = state.Camera; UICamera.current = state.UiCamera;
        UICamera.hoveredObject = state.Hovered; UICamera.lastHit = state.Hit; UICamera.lastWorldPosition = state.WorldPosition;
        check(OperationManager.history.Count == state.HistoryBoundary && NodesUnchanged(session, state.Nodes) && CanonicalStory(session) == state.Story,
            "iteration 2 cross-frame raycasts restore the canvas, native nodes, exact story content and history boundary");
        raycasts = null;
        return true;
    }

    /// <summary>在真实 UI 帧就绪后检查按钮下方空白，防止删除的面板继续拦截原生命中。</summary>
    private static void CheckCreateEntryEmptyArea(EditorSession session, Action<bool, string> check)
    {
        var toolbar = session.Board.treeRoot.parent.Find("NodeGroups toolbar")!;
        var button = toolbar.Find("创建组")!.GetComponent<UISprite>();
        var camera = UICamera.FindCameraForLayer(button.gameObject.layer)?.GetComponent<Camera>()
            ?? throw new InvalidOperationException("Single-create raycast requires the native UI camera.");
        var oldCamera = UICamera.currentCamera; var oldCurrent = UICamera.current;
        var oldHover = UICamera.hoveredObject; var oldHit = UICamera.lastHit; var oldWorld = UICamera.lastWorldPosition;
        try
        {
            Physics.SyncTransforms();
            var corners = button.worldCorners.ToArray().Select(camera.WorldToScreenPoint).ToArray();
            var screen = new Vector3((corners.Min(point => point.x) + corners.Max(point => point.x)) / 2,
                corners.Min(point => point.y) - 12, 0);
            var found = UICamera.Raycast(screen);
            var target = found ? UICamera.lastHit.collider?.transform : null;
            check(target == null || !target.IsChildOf(toolbar),
                "single-create empty space below its button is not intercepted by any residual toolbar panel");
        }
        finally
        {
            UICamera.currentCamera = oldCamera; UICamera.current = oldCurrent; UICamera.hoveredObject = oldHover;
            UICamera.lastHit = oldHit; UICamera.lastWorldPosition = oldWorld;
        }
    }

    /// <summary>
    /// The original native click/selection Undo can reopen its inspector. Close that view through
    /// the original container API before testing the canvas; do not edit colliders or UI lifecycle fields.
    /// </summary>
    private static void ReturnToNativeCanvas(EditorSession session, Action<bool, string> check)
    {
        var container = InspectorContainer.instance
            ?? throw new InvalidOperationException("Native canvas restoration requires its inspector container.");
        var history = OperationManager.history.Count;
        var nodes = session.ReadNodes(); var story = CanonicalStory(session);
        var compiled = AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray();
        Plugin.Logger.LogInfo($"Iteration 2 native canvas return: activeInspector={container.activeInspector?.GetType().Name}; " +
            $"objects=[{string.Join(",", container.inspectorObjects.ToArray().Select(item => item.name + ":" + item.activeInHierarchy))}]; history={history}");
        container.UnloadInspector();
        check(OperationManager.history.Count == history &&
            LogNativeStoryState(session, "native inspector close", nodes, story, compiled),
            "iteration 2 original inspector-close API restores the canvas without changing native history, story or compile bytes");
    }

    private static bool NativeInspectorClosed()
    {
        var container = InspectorContainer.instance;
        return container != null && container.activeInspector == null &&
            HiddenNativeView(container.bg) && container.inspectorObjects.ToArray().All(HiddenNativeView);
    }

    private static bool HiddenNativeView(GameObject? root) => root == null || !root.activeInHierarchy ||
        root.GetComponentsInChildren<UIWidget>(true).All(widget => !widget.gameObject.activeInHierarchy || !widget.enabled ||
            !widget.isVisible || widget.finalAlpha < .001f);

    private static void PanToRaycastTarget(EditorSession session, Collider target, Vector3 baselinePosition)
    {
        var tree = session.Board.treeRoot; tree.position = baselinePosition; Physics.SyncTransforms();
        var camera = UICamera.FindCameraForLayer(target.gameObject.layer)?.GetComponent<Camera>()
            ?? throw new InvalidOperationException("Native raycast check requires the UI camera.");
        var ray = camera.ViewportPointToRay(new Vector3(.55f, .45f, 0));
        var plane = new Plane(tree.forward, tree.position);
        if (!plane.Raycast(ray, out var distance)) throw new InvalidOperationException("Native raycast viewport-center ray missed the canvas plane.");
        tree.position += ray.GetPoint(distance) - target.bounds.center; Physics.SyncTransforms();
        // No forced mStarted/visibility/panel fields: the next two real frames own
        // Start, panel registration, transform matrices and clipping evaluation.
    }

    /// <summary>Observes the real resolver after the caller has allowed normal Unity/UI frame synchronization.</summary>
    private static GameObject? ObserveRaycast(EditorSession session, Collider target)
    {
        var camera = UICamera.FindCameraForLayer(target.gameObject.layer)?.GetComponent<Camera>()
            ?? throw new InvalidOperationException("Native raycast check requires the UI camera.");
        var tree = session.Board.treeRoot;
        var oldCamera = UICamera.currentCamera; var oldCurrent = UICamera.current;
        var oldHover = UICamera.hoveredObject; var oldHit = UICamera.lastHit; var oldWorld = UICamera.lastWorldPosition;
        try
        {
            Physics.SyncTransforms();
            var screen = camera.WorldToScreenPoint(target.bounds.center);
            var success = UICamera.Raycast(screen);
            var hit = UICamera.lastHit.collider?.gameObject;
            Plugin.Logger.LogInfo($"Iteration 2 raycast: target={Hierarchy(target.transform)}; enabled={target.enabled}; active={target.gameObject.activeInHierarchy}; " +
                $"world={target.bounds.center}; screen={screen}; nativeResult={success}; lastHit={Hierarchy(hit?.transform)}; hover={Hierarchy(UICamera.hoveredObject?.transform)}; " +
                $"uiCamera={UICamera.current?.name}/{UICamera.current?.eventType}; camera={camera.name}; mask={camera.cullingMask}; " +
                $"treePosition={tree.position}; treeScale={tree.lossyScale.x:R}/{tree.lossyScale.y:R}/{tree.lossyScale.z:R}");
            foreach (var widget in target.GetComponents<UIWidget>())
                Plugin.Logger.LogInfo($"Iteration 2 raycast target widget: {widget.name}; visible={widget.isVisible}; enabled={widget.enabled}; " +
                    $"finalAlpha={widget.finalAlpha}; depth={widget.depth}; panel={widget.panel?.name}; panelDepth={widget.panel?.depth}; " +
                    $"panelAlpha={widget.panel?.finalAlpha}; clipping={widget.panel?.clipping}; clipRegion={widget.panel?.baseClipRegion}");
            var hitRay = camera.ScreenPointToRay(screen);
            foreach (var candidate in Physics.RaycastAll(hitRay, 1000, 1 << target.gameObject.layer).ToArray().Take(20))
                Plugin.Logger.LogInfo($"Iteration 2 raw physics candidate: {Hierarchy(candidate.collider?.transform)}; " +
                    $"widgetDepth={candidate.collider?.GetComponent<UIWidget>()?.depth}; " +
                    $"visible={candidate.collider?.GetComponent<UIWidget>()?.isVisible}; alpha={candidate.collider?.GetComponent<UIWidget>()?.finalAlpha}");
            return success ? hit : null;
        }
        finally
        {
            UICamera.currentCamera = oldCamera; UICamera.current = oldCurrent; UICamera.hoveredObject = oldHover;
            UICamera.lastHit = oldHit; UICamera.lastWorldPosition = oldWorld;
        }
    }

    private static string Hierarchy(Transform? transform)
    {
        var names = new List<string>();
        while (transform != null) { names.Add(transform.name); transform = transform.parent; }
        names.Reverse(); return names.Count == 0 ? "<none>" : string.Join("/", names);
    }

}
