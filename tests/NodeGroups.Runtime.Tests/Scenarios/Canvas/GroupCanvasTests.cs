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
/// Coordinates canvas regressions for selection layers, overlapping drops, region resizing,
/// native selection cancellation and floating-panel movement. Partial files isolate
/// native styling, cross-frame raycasts and editor-scene lifecycles.
/// </summary>
internal static partial class GroupCanvasTests
{
    /// <summary>Normalizes only property ordering and the native node registry's array order; story/script/choice ordering remains authoritative.</summary>
    private static string CanonicalStory(EditorSession session) => CanonicalStory(session.Editor.CaptureProject());

    private static string CanonicalStory(ProjectData project)
    {
        var serialized = ProjectFileStore.Serialize(project);
        var start = serialized.IndexOf('{');
        if (start < 0) throw new InvalidOperationException("Native project serialization has no JSON payload.");
        using var document = JsonDocument.Parse(serialized[start..]);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, document.RootElement);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element, string? propertyName = null)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value, property.Name); }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            var values = element.EnumerateArray().ToArray();
            if (propertyName == "nodes") values = values.OrderBy(node => node.GetProperty("Guid").GetString(), StringComparer.Ordinal).ToArray();
            foreach (var value in values) WriteCanonical(writer, value);
            writer.WriteEndArray();
        }
        else element.WriteTo(writer);
    }

    /// <summary>
    /// Exercises the second iteration on the already initialized independent fixture.
    /// Native event listeners receive deterministic NGUI pointer data; no operating-system
    /// mouse automation is involved. A scoped click fixture temporarily supplies Unity's
    /// button/key states and removes its own prefixes before returning. Temporary metadata and movements are
    /// undone through the original history before the caller continues its verification.
    /// </summary>
    /// <param name="session">The isolated integration process's live editor session.</param>
    /// <param name="check">Assertion reporter that throws immediately on failure.</param>
    internal static void Run(EditorSession session, Action<bool, string> check)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NODEGROUPS_TEST_REPORT")))
            throw new InvalidOperationException("Iteration 2 checks require the explicitly isolated test process.");
        var historyBoundary = OperationManager.history.Count;
        var previousSelection = Blackboard.selected.ToArray();
        var previousGroup = session.SelectedGroup;
        var previousSelectedObject = UICamera.selectedObject;
        var originalNodes = session.ReadNodes();
        var compileDelta = OperationManager.compileDelta;
        var success = false;
        try
        {
            CheckInitialUi(session, check);
            CheckCreateButtonGestures(session, check);
            var script = session.Board.nodes.ToArray().Select(node => node.TryCast<ScriptNode>())
                .FirstOrDefault(node => node != null) ?? throw new InvalidOperationException("Iteration 2 fixture requires a native ScriptNode.");
            Blackboard.selected.Clear(); Blackboard.selected.Add(script);
            session.CreateGroup(false);
            var lower = session.SelectedGroup ?? throw new InvalidOperationException("Created group did not receive selection.");
            var region = InitialRegion(session, script);
            session.Commit(session.Workspace.SetExpandedBounds(lower, region));
            Blackboard.selected.Clear(); session.CreateGroup(false);
            var upper = session.SelectedGroup ?? throw new InvalidOperationException("Empty creation did not select its group.");
            session.Commit(session.Workspace.SetExpandedBounds(upper, region));
            session.Tick();

            CheckVisualResources(session, lower, script, check);
            CheckDormantFontSource(session, script, check);
            CheckSelectionLayer(session, lower, upper, check);
            CheckResizeCallbacks(session, lower, upper, check);
            CheckNativeSelectionCancellation(session, lower, script, check);
            CheckOverlapDrop(session, lower, upper, script, check);
            CheckManyGroupDepths(session, check);

            var guard = OperationManager.history.Count - historyBoundary + 8;
            while (OperationManager.history.Count > historyBoundary && guard-- > 0) OperationManager.Undo();
            session.Tick();
            check(OperationManager.history.Count == historyBoundary && session.Board.nodes.Count == originalNodes.Length &&
                originalNodes.All(node => session.Find(node.Id) is { } current && Near(session.Position(current), node.Position)),
                "iteration 2 temporary gestures undo to the original native-node and history boundary");
            check(OperationManager.compileDelta == compileDelta,
                "iteration 2 group appearance, region, selection and drag gestures preserve native compilation delta");
            success = true;
        }
        finally
        {
            if (!success) session.CancelDrag();
            Blackboard.selected.Clear();
            foreach (var node in previousSelection)
                if (node != null && session.Find(EditorSession.Id(node)) != null) Blackboard.selected.Add(node);
            session.SelectedGroup = previousGroup;
            UICamera.selectedObject = previousSelectedObject;
        }
    }

    private static Rect2 InitialRegion(EditorSession session, Node node)
    {
        var points = node.GetComponentsInChildren<UIWidget>(true).SelectMany(widget => widget.worldCorners.ToArray())
            .Select(point => session.Board.treeRoot.InverseTransformPoint(point)).ToArray();
        if (points.Length == 0) throw new InvalidOperationException("Native frame test requires real node widget corners.");
        var minX = points.Min(point => point.x); var minY = points.Min(point => point.y);
        return new Rect2(minX - 500, minY - 500,
            Math.Max(1400, points.Max(point => point.x) - minX + 1000),
            Math.Max(1100, points.Max(point => point.y) - minY + 1000));
    }

    private static Transform GroupRoot(EditorSession session, string id) => session.Board.treeRoot.Find("NodeGroup " + id)
        ?? throw new InvalidOperationException("Group view is missing: " + id);

    private static void CheckSelectionLayer(EditorSession session, string lower, string upper, Action<bool, string> check)
    {
        var history = OperationManager.history.Count;
        var otherOrder = LayerOrder(session.Workspace.Snapshot).Where(id => id != lower).ToArray();
        var title = GroupRoot(session, lower).Find("Header/Name (double click)")!;
        UIEventListener.Get(title.gameObject).OnClick();
        var selected = session.Workspace.Snapshot;
        check(session.SelectedGroup == lower && LayerOrder(selected).Last() == lower &&
            otherOrder.SequenceEqual(LayerOrder(selected).Where(id => id != lower)) && OperationManager.history.Count == history,
            "iteration 2 native group selection promotes only that group without adding an Undo step");
        check(GroupRoot(session, lower).GetComponentsInChildren<UIWidget>(true).Max(widget => widget.depth) >
            GroupRoot(session, upper).GetComponentsInChildren<UIWidget>(true).Max(widget => widget.depth),
            "iteration 2 promoted group widgets render above the overlapping lower group");
        session.SelectGroup(lower);
        check(OperationManager.history.Count == history && LayerOrder(session.Workspace.Snapshot).SequenceEqual(LayerOrder(selected)),
            "iteration 2 selecting the already top group does not change history or layer order");
    }

    private static string[] LayerOrder(WorkspaceSnapshot snapshot) => snapshot.Groups.OrderBy(group => group.ZIndex)
        .Select(group => group.Id).ToArray();

    private static void CheckResizeCallbacks(EditorSession session, string id, string otherGroup, Action<bool, string> check)
    {
        var original = session.Workspace.Snapshot.GetGroup(id).ExpandedBounds
            ?? throw new InvalidOperationException("Resize fixture has no persisted region.");
        var nodes = session.ReadNodes();
        var memberships = Memberships(session.Workspace.Snapshot);
        var nativeSnapshot = ProjectFileStore.Serialize(session.Editor.CaptureProject());
        var history = OperationManager.history.Count;
        var layerOrder = LayerOrder(session.Workspace.Snapshot);
        var handle = GroupRoot(session, id).Find("Resize 2")
            ?? throw new InvalidOperationException("Right resize handle is missing.");
        var listener = UIEventListener.Get(handle.gameObject);
        using (var touch = new PointerContext(session, handle.gameObject, new Point2(original.X + original.Width, original.Y + original.Height / 2)))
        {
            listener.OnDragStart();
            var delta = touch.MoveCanvasBy(new Point2(160, 0)); listener.OnDrag(delta);
            check(session.Workspace.Snapshot.GetGroup(id).ExpandedBounds is { } preview && preview.Width > original.Width + 100,
                "iteration 2 native right-border callback previews a resized fixed region");
            listener.OnDragEnd();
        }
        var resized = session.Workspace.Snapshot.GetGroup(id).ExpandedBounds!.Value;
        check(Near(new Point2(resized.X, resized.Y), new Point2(original.X, original.Y)) &&
            Math.Abs(resized.Width - original.Width - 160) < .1 && Math.Abs(resized.Height - original.Height) < .1 &&
            OperationManager.history.Count == history + 1,
            "iteration 2 completed border resize changes only its requested edge in one native-history command");
        check(NodesUnchanged(session, nodes) && Memberships(session.Workspace.Snapshot) == memberships &&
            ProjectFileStore.Serialize(session.Editor.CaptureProject()) == nativeSnapshot && layerOrder.SequenceEqual(LayerOrder(session.Workspace.Snapshot)),
            "iteration 2 resize preserves every native node, membership, story snapshot and group layer order");
        session.Tick();
        check(session.Workspace.Snapshot.GetGroup(id).ExpandedBounds == resized && session.Bounds(session.Workspace.Snapshot.GetGroup(id)) == resized,
            "iteration 2 periodic refresh retains the user's resized expanded region");
        session.SelectGroup(otherGroup);
        var latestLayers = LayerOrder(session.Workspace.Snapshot);
        OperationManager.Undo();
        check(session.Workspace.Snapshot.GetGroup(id).ExpandedBounds == original && NodesUnchanged(session, nodes),
            "iteration 2 one native Undo restores the resized region without moving nodes");
        check(latestLayers.SequenceEqual(LayerOrder(session.Workspace.Snapshot)),
            "iteration 2 resize Undo retains the latest selection-driven group layer order");
        OperationManager.Redo();
        check(session.Workspace.Snapshot.GetGroup(id).ExpandedBounds == resized && NodesUnchanged(session, nodes),
            "iteration 2 one native Redo restores the resized region without moving nodes");
        check(latestLayers.SequenceEqual(LayerOrder(session.Workspace.Snapshot)),
            "iteration 2 resize Redo retains the latest selection-driven group layer order");
        session.SelectGroup(id);

        history = OperationManager.history.Count;
        using (var touch = new PointerContext(session, handle.gameObject, new Point2(resized.X + resized.Width, resized.Y + resized.Height / 2)))
        {
            listener.OnDragStart(); listener.OnDrag(touch.MoveCanvasBy(new Point2(90, 30)));
            check(session.Workspace.Snapshot.GetGroup(id).ExpandedBounds != resized,
                "iteration 2 cancellation fixture has a real border-resize preview");
            session.CancelDrag(); listener.OnDragEnd(); session.Tick();
        }
        check(session.Workspace.Snapshot.GetGroup(id).ExpandedBounds == resized && OperationManager.history.Count == history &&
            NodesUnchanged(session, nodes) && Memberships(session.Workspace.Snapshot) == memberships,
            "iteration 2 cancelled border resize restores the region and writes no history or membership changes");

        using (var touch = new PointerContext(session, handle.gameObject, new Point2(resized.X + resized.Width, resized.Y + resized.Height / 2)))
        {
            listener.OnDragStart(); listener.OnDrag(touch.MoveCanvasBy(new Point2(90, 0)));
            check(session.Workspace.Snapshot.GetGroup(id).ExpandedBounds is { } preview && preview.Width > resized.Width + 80,
                "iteration 2 no-op resize fixture first moves the right edge away from its origin");
            listener.OnDrag(touch.MoveCanvasBy(new Point2(-90, 0))); listener.OnDragEnd(); session.Tick();
        }
        check(session.Workspace.Snapshot.GetGroup(id).ExpandedBounds is { } returned && NearRect(returned, resized, .001) &&
            OperationManager.history.Count == history && NodesUnchanged(session, nodes),
            "iteration 2 border gesture returned to its origin writes no Undo command despite pointer roundoff");
    }

    private static bool NearRect(Rect2 first, Rect2 second, double tolerance) => Math.Abs(first.X - second.X) <= tolerance &&
        Math.Abs(first.Y - second.Y) <= tolerance && Math.Abs(first.Width - second.Width) <= tolerance && Math.Abs(first.Height - second.Height) <= tolerance;

    private static Transform? ActiveContextMenu(EditorSession session) => session.Board.treeRoot.GetComponentsInChildren<Transform>(true)
        .FirstOrDefault(transform => transform.name == "NodeGroup context menu" && transform.gameObject.activeInHierarchy);

    private static void PerformGroupRightClick(EditorSession session, string id)
    {
        var title = GroupRoot(session, id).Find("Header/Name (double click)")!;
        using var touch = new PointerContext(session, title.gameObject, title.position, dragging: false);
        UICamera.currentTouchID = -2;
        UIEventListener.Get(title.gameObject).OnClick();
        session.Tick();
    }

    private static void CheckNativeSelectionCancellation(EditorSession session, string id, Node node, Action<bool, string> check)
    {
        session.Board.DeselectAllChildren(false);
        node.Deselect(false);
        node.Select(false);
        check(node.selected && Blackboard.selected.ToArray().Any(selected => selected.Pointer == node.Pointer),
            "iteration 2 selection-cancellation fixture selects its original native node");
        var baselineHistory = OperationManager.history.Count;
        var originalStory = CanonicalStory(session);
        var originalCompiled = AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray();
        var originalNodes = session.ReadNodes();
        var baselineBefore = Blackboard.selected.ToArray().Select(EditorSession.Id).ToArray();
        bool baselineSelected;
        string[] baselineIds;
        int baselineDelta;
        string[] baselineTypes;
        try
        {
            PerformNativeClick(session, node, "without selected group", baselineHistory, baselineBefore);
            baselineSelected = node.selected;
            baselineIds = Blackboard.selected.ToArray().Select(EditorSession.Id).ToArray();
            baselineDelta = OperationManager.history.Count - baselineHistory;
            baselineTypes = AddedOperationTypes(baselineHistory);
            check(baselineDelta >= 0 && session.Find(EditorSession.Id(node)) != null &&
                session.Board.nodes.Count == originalNodes.Length && NodesUnchanged(session, originalNodes) &&
                CanonicalStory(session) == originalStory &&
                AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray().SequenceEqual(originalCompiled),
                "iteration 2 original native click baseline preserves every story node, position, connection and compile byte");
        }
        finally { UndoClickOperations(session, baselineHistory); }
        check(OperationManager.history.Count == baselineHistory && CanonicalStory(session) == originalStory &&
            AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray().SequenceEqual(originalCompiled),
            "iteration 2 original native click commands undo to the unchanged story and history boundary");
        session.Board.DeselectAllChildren(false); node.Deselect(false); node.Select(false);
        session.SelectGroup(id);
        PerformGroupRightClick(session, id);
        check(ActiveContextMenu(session) == null,
            "retained native group right-click leaves the removed context menu absent");
        var nativeSelected = Blackboard.selected.ToArray().Select(EditorSession.Id).ToArray();
        var history = OperationManager.history.Count;
        try
        {
            PerformNativeClick(session, node, "with selected group", history, nativeSelected);
            check(session.SelectedGroup == null && node.selected == baselineSelected &&
                baselineIds.SequenceEqual(Blackboard.selected.ToArray().Select(EditorSession.Id)) &&
                OperationManager.history.Count - history == baselineDelta && baselineTypes.SequenceEqual(AddedOperationTypes(history)),
                "iteration 2 clicking an already selected native node clears group highlight and preserves the original native selection and command baseline");
            check(session.Find(EditorSession.Id(node)) != null && session.Board.nodes.Count == originalNodes.Length &&
                NodesUnchanged(session, originalNodes) && CanonicalStory(session) == originalStory &&
                AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray().SequenceEqual(originalCompiled),
                "iteration 2 group-selection cancellation preserves the original native story and compile bytes");
            check(ActiveContextMenu(session) == null,
                "native-node selection does not recreate the removed group context menu");
        }
        finally { UndoClickOperations(session, history); }
        check(OperationManager.history.Count == history && CanonicalStory(session) == originalStory,
            "iteration 2 native click comparison leaves no temporary native command or story change");

        session.Board.DeselectAllChildren(false); node.Deselect(false); node.Select(false);
        var blankBaselineHistory = OperationManager.history.Count;
        var blankBefore = Blackboard.selected.ToArray().Select(EditorSession.Id).ToArray();
        bool blankSelected;
        string[] blankIds;
        int blankDelta;
        string[] blankTypes;
        try
        {
            PerformNativeCanvasDeselect(session, node, "without selected group", blankBaselineHistory, blankBefore);
            blankSelected = node.selected; blankIds = Blackboard.selected.ToArray().Select(EditorSession.Id).ToArray();
            blankDelta = OperationManager.history.Count - blankBaselineHistory; blankTypes = AddedOperationTypes(blankBaselineHistory);
            check(!blankSelected && blankIds.Length == 0 && blankDelta >= 0 &&
                LogNativeStoryState(session, "native empty-canvas baseline", originalNodes, originalStory, originalCompiled),
                "iteration 2 original empty-canvas baseline clears native selection and preserves every story and compile byte");
        }
        finally { UndoClickOperations(session, blankBaselineHistory); }
        check(OperationManager.history.Count == blankBaselineHistory &&
            LogNativeStoryState(session, "native empty-canvas baseline Undo", originalNodes, originalStory, originalCompiled),
            "iteration 2 original empty-canvas commands undo to the native story and history boundary");

        session.Board.DeselectAllChildren(false); node.Deselect(false); node.Select(false);
        session.SelectGroup(id); PerformGroupRightClick(session, id);
        var blankHistory = OperationManager.history.Count;
        var blankGroupedBefore = Blackboard.selected.ToArray().Select(EditorSession.Id).ToArray();
        try
        {
            PerformNativeCanvasDeselect(session, node, "with selected group", blankHistory, blankGroupedBefore);
            check(session.SelectedGroup == null && node.selected == blankSelected &&
                blankIds.SequenceEqual(Blackboard.selected.ToArray().Select(EditorSession.Id)) &&
                OperationManager.history.Count - blankHistory == blankDelta && blankTypes.SequenceEqual(AddedOperationTypes(blankHistory)),
                "iteration 2 original empty-canvas deselection clears group highlight and preserves the native selection and command baseline");
            check(LogNativeStoryState(session, "grouped empty-canvas cancellation", originalNodes, originalStory, originalCompiled),
                "iteration 2 empty-canvas group cancellation preserves the original story and compile bytes");
            check(ActiveContextMenu(session) == null,
                "empty-canvas deselection leaves the removed group context menu absent");
        }
        finally { UndoClickOperations(session, blankHistory); }
        check(OperationManager.history.Count == blankHistory &&
            LogNativeStoryState(session, "grouped empty-canvas Undo", originalNodes, originalStory, originalCompiled),
            "iteration 2 empty-canvas comparison leaves no temporary native command or story change");
        session.SelectGroup(id);
    }

    /// <summary>Dispatches the original blank-canvas deselection with identical neutral pointer/input state for both baselines.</summary>
    private static void PerformNativeCanvasDeselect(EditorSession session, Node node, string label, int history, string[] selected)
    {
        LogSelectionState(session, node, label + ": before empty-canvas deselection", history, selected);
        using (var input = new ClickInputContext())
        using (var touch = new PointerContext(session, session.Board.gameObject, session.Board.transform.position, dragging: false))
        {
            input.Release();
            session.Board.DeselectAllChildren(false);
            LogSelectionState(session, node, label + ": after empty-canvas deselection", history, selected);
        }
        session.Tick(); LogSelectionState(session, node, label + ": after empty-canvas tick", history, selected);
    }

    private static bool LogNativeStoryState(EditorSession session, string label, NodeState[] originalNodes, string originalStory, byte[] originalCompiled)
    {
        var sameNodes = session.Board.nodes.Count == originalNodes.Length && NodesUnchanged(session, originalNodes);
        var sameStory = CanonicalStory(session) == originalStory;
        var sameCompiled = AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray().SequenceEqual(originalCompiled);
        Plugin.Logger.LogInfo($"Iteration 2 native story ({label}): nodesAndPositionsSame={sameNodes}; storyAndConnectionsSame={sameStory}; compileBytesSame={sameCompiled}");
        return sameNodes && sameStory && sameCompiled;
    }

    private static void PerformNativeClick(EditorSession session, Node node, string label, int history, string[] selected)
    {
        LogSelectionState(session, node, label + ": before native click", history, selected);
        var active = Node.activeNode;
        var tracker = session.Editor.mouseTracker;
        var trackerPosition = tracker != null ? tracker.position : Vector3.zero;
        using var input = new ClickInputContext();
        using (var touch = new PointerContext(session, node.gameObject, node.root.position, dragging: false))
        {
            try
            {
                if (tracker != null) tracker.position = node.root.position;
                input.Press();
                LogSelectionState(session, node, label + ": touch installed", history, selected);
                node.OnPress(true); LogSelectionState(session, node, label + ": after native press", history, selected);
                input.Release();
                node.OnPress(false); LogSelectionState(session, node, label + ": after native release", history, selected);
                node.OnClick(); LogSelectionState(session, node, label + ": after native click", history, selected);
            }
            finally
            {
                Node.activeNode = active;
                if (tracker != null) tracker.position = trackerPosition;
            }
        }
        session.Tick(); LogSelectionState(session, node, label + ": after session tick", history, selected);
    }

    private static string[] AddedOperationTypes(int historyBoundary) => Enumerable.Range(Math.Min(historyBoundary, OperationManager.history.Count),
        Math.Max(0, OperationManager.history.Count - historyBoundary))
        .Select(index => OperationManager.history._array[index].GetIl2CppType().FullName).ToArray();

    /// <summary>Reverts legitimate native click commands as well as a failing diagnostic click before the next fixture step.</summary>
    private static void UndoClickOperations(EditorSession session, int historyBoundary)
    {
        var guard = OperationManager.history.Count - historyBoundary + 8;
        while (OperationManager.history.Count > historyBoundary && guard-- > 0) OperationManager.Undo();
        session.Tick();
    }

    private static void LogSelectionState(EditorSession session, Node node, string stage, int historyBefore, string[] selectedBefore)
    {
        var touch = UICamera.currentTouch;
        Plugin.Logger.LogInfo($"Iteration 2 native selection ({stage}): group={session.SelectedGroup ?? "<none>"}; " +
            $"node={EditorSession.Id(node)}; nodeSelected={node.selected}; activeNode={Node.activeNode?.guid}; " +
            $"nativeBefore=[{string.Join(",", selectedBefore)}]; nativeNow=[{string.Join(",", Blackboard.selected.ToArray().Select(EditorSession.Id))}]; " +
            $"history={historyBefore}->{OperationManager.history.Count}; addedTypes=[{string.Join(",", AddedOperationTypes(historyBefore))}]; " +
            $"nodeStillRegistered={session.Find(EditorSession.Id(node)) != null}; nodeCount={session.Board.nodes.Count}; " +
            $"lastOperation={(OperationManager.history.Count == 0 ? "<none>" : OperationManager.history.Peek().Name)}; " +
            $"mouseButtons={Input.GetMouseButton(0)}/{Input.GetMouseButton(1)}/{Input.GetMouseButton(2)}; " +
            $"control={Input.GetKey(KeyCode.LeftControl)}/{Input.GetKey(KeyCode.RightControl)}; " +
            $"alt={Input.GetKey(KeyCode.LeftAlt)}/{Input.GetKey(KeyCode.RightAlt)}; touchID={UICamera.currentTouchID}; uiKey={UICamera.currentKey}; " +
            $"touchKey={touch?.key}; touchCurrent={Hierarchy(touch?.current?.transform)}; touchPressed={Hierarchy(touch?.pressed?.transform)}; " +
            $"touchDrag={touch?.dragStarted}; touchPressStarted={touch?.pressStarted}; touchNotification={touch?.clickNotification}; " +
            $"uiSelected={Hierarchy(UICamera.selectedObject?.transform)}; longPress={node.isLongPressed}");
    }

    /// <summary>
    /// Supplies press/release state to the original Unity Input getters for one opt-in native click.
    /// It patches no node handler and removes only its own prefixes on disposal, including on failure.
    /// </summary>
    private sealed class ClickInputContext : IDisposable
    {
        private static bool active;
        private static bool held;
        private static bool down;
        private static bool up;
        private readonly Harmony harmony = new(Plugin.Id + ".iteration2-click-fixture");
        private readonly List<System.Reflection.MethodInfo> patched = new();

        internal ClickInputContext()
        {
            if (active) throw new InvalidOperationException("Native click input scopes cannot overlap.");
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NODEGROUPS_TEST_REPORT")))
                throw new InvalidOperationException("Native click input is available only in the isolated test process.");
            active = true; held = down = up = false;
            try
            {
                Patch(nameof(Input.GetMouseButton), typeof(int), nameof(MouseHeld));
                Patch(nameof(Input.GetMouseButtonDown), typeof(int), nameof(MouseDown));
                Patch(nameof(Input.GetMouseButtonUp), typeof(int), nameof(MouseUp));
                Patch(nameof(Input.GetKey), typeof(KeyCode), nameof(KeyHeld));
                Patch(nameof(Input.GetKeyDown), typeof(KeyCode), nameof(KeyDown));
                Patch(nameof(Input.GetKeyUp), typeof(KeyCode), nameof(KeyUp));
            }
            catch { Dispose(); throw; }
        }

        private void Patch(string methodName, Type parameter, string prefixName)
        {
            var method = AccessTools.Method(typeof(Input), methodName, new[] { parameter });
            harmony.Patch(method, prefix: new HarmonyMethod(AccessTools.Method(typeof(ClickInputContext), prefixName)));
            patched.Add(method);
        }

        internal void Press() { held = down = true; up = false; }
        internal void Release() { held = down = false; up = true; }

        private static bool MouseHeld(int __0, ref bool __result) => Supply(__0 == 0 && held, ref __result);
        private static bool MouseDown(int __0, ref bool __result) => Supply(__0 == 0 && down, ref __result);
        private static bool MouseUp(int __0, ref bool __result) => Supply(__0 == 0 && up, ref __result);
        private static bool KeyHeld(KeyCode __0, ref bool __result) => Supply(__0 == KeyCode.Mouse0 && held, ref __result);
        private static bool KeyDown(KeyCode __0, ref bool __result) => Supply(__0 == KeyCode.Mouse0 && down, ref __result);
        private static bool KeyUp(KeyCode __0, ref bool __result) => Supply(__0 == KeyCode.Mouse0 && up, ref __result);
        private static bool Supply(bool value, ref bool result)
        {
            if (!active) return true;
            result = value; return false;
        }

        public void Dispose()
        {
            active = held = down = up = false;
            try { foreach (var method in patched) harmony.Unpatch(method, HarmonyPatchType.Prefix, harmony.Id); }
            finally { patched.Clear(); }
        }
    }

    /// <summary>Validates controls above a high-rank canvas without introducing native operations or story nodes.</summary>
    private static void CheckManyGroupDepths(EditorSession session, Action<bool, string> check)
    {
        var baseline = session.Workspace.Snapshot;
        var selected = session.SelectedGroup;
        var history = OperationManager.history.Count;
        var story = ProjectFileStore.Serialize(session.Editor.CaptureProject());
        var highest = baseline.Groups.Count == 0 ? 0 : baseline.Groups.Max(group => group.ZIndex);
        var extras = Enumerable.Range(1, 255).Select(index => new GroupState(Guid.NewGuid().ToString("D"), "离屏层级测试 " + index,
            Array.Empty<string>(), false, new Point2(500000, 500000), zIndex: highest + index,
            expandedBounds: new Rect2(500000, 500000, 720, 340))).ToArray();
        try
        {
            session.Workspace.Restore(new WorkspaceSnapshot(baseline.Nodes, baseline.Groups.Concat(extras)));
            session.Tick();
            var maxGroupDepth = session.Board.treeRoot.GetComponentsInChildren<UIWidget>(true)
                .Where(widget => widget.transform.GetComponentsInParent<Transform>(true).Any(parent => parent.name.StartsWith("NodeGroup ", StringComparison.Ordinal)))
                .Max(widget => widget.depth);
            var toolbar = session.Board.treeRoot.parent.Find("NodeGroups toolbar")!;
            check(toolbar.GetComponentsInChildren<UIWidget>(true).Min(widget => widget.depth) > maxGroupDepth,
                "single-create entry stays above every group with 255 additional canvas layers");
            PerformGroupRightClick(session, extras[^1].Id);
            var dissolve = GroupRoot(session, extras[^1].Id).Find("Header/解散");
            check(ActiveContextMenu(session) == null && dissolve != null &&
                dissolve.GetComponent<Collider>() != null && dissolve.GetComponentInChildren<UILabel>().text == "解散",
                "high-layer groups retain a directly accessible dissolve button without a context menu");
            check(OperationManager.history.Count == history && ProjectFileStore.Serialize(session.Editor.CaptureProject()) == story,
                "iteration 2 high-layer UI stress preserves native story data and operation history");
        }
        finally
        {
            session.Workspace.Restore(baseline); session.SelectedGroup = selected; session.Tick();
        }
        check(ActiveContextMenu(session) == null,
            "removing temporary high-layer groups leaves no obsolete group context menu");
    }

    private static bool NodesUnchanged(EditorSession session, IEnumerable<NodeState> nodes) => nodes.All(node =>
        session.Find(node.Id) is { } native && Near(session.Position(native), node.Position));

    private static string Memberships(WorkspaceSnapshot snapshot) => string.Join("|", snapshot.Groups.OrderBy(group => group.Id)
        .Select(group => group.Id + ":" + string.Join(",", group.MemberNodeIds)));

    private static void CheckOverlapDrop(EditorSession session, string lower, string upper, Node node, Action<bool, string> check)
    {
        var layers = LayerOrder(session.Workspace.Snapshot);
        var nativeBefore = session.Position(node);
        var ownerBefore = session.Workspace.Snapshot.FindGroupForNode(EditorSession.Id(node))?.Id;
        var bounds = session.DropBounds(session.Workspace.Snapshot.GetGroup(upper));
        var target = new Point2(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        var history = OperationManager.history.Count;
        Blackboard.selected.Clear(); Blackboard.selected.Add(node);
        session.BeginNodeDrag(node);
        var delta = target - nativeBefore;
        var worldDelta = session.Board.treeRoot.TransformVector(new Vector3((float)delta.X, (float)delta.Y, 0));
        var nativeDelta = node.root.parent.InverseTransformVector(worldDelta);
        node.expectedDragPos = node.root.localPosition; node.Move(nativeDelta, false, true);
        session.PreviewNodeDrag();
        check(session.DropTarget == lower && layers.SequenceEqual(LayerOrder(session.Workspace.Snapshot)),
            "iteration 2 overlapping-region drag preview chooses the selected top group without raising hover targets");
        session.EndNodeDrag();
        check(session.Workspace.Snapshot.FindGroupForNode(EditorSession.Id(node))?.Id == lower &&
            OperationManager.history.Count == history + 1 && layers.SequenceEqual(LayerOrder(session.Workspace.Snapshot)),
            "iteration 2 overlapping drop commits the top group with one movement command and preserves stacking");
        OperationManager.Undo();
        check(Near(session.Position(node), nativeBefore) && session.Workspace.Snapshot.FindGroupForNode(EditorSession.Id(node))?.Id == ownerBefore,
            "iteration 2 overlapping-drop Undo restores native position and original ownership");
    }

    private static bool Near(Point2 first, Point2 second) => Math.Abs(first.X - second.X) < .1 && Math.Abs(first.Y - second.Y) < .1;

    /// <summary>Scoped NGUI pointer fixture for group and panel event handlers; restores every replaced static field.</summary>
    private sealed class PointerContext : IDisposable
    {
        private readonly EditorSession session;
        private readonly Camera camera;
        private readonly UICamera.MouseOrTouch? previousTouch;
        private readonly UICamera? previousCamera;
        private readonly Camera? previousCurrentCamera;
        private readonly KeyCode previousKey;
        private readonly int previousId;
        private readonly Vector3 previousWorldPosition;
        private readonly UICamera.MouseOrTouch touch;

        internal PointerContext(EditorSession session, GameObject target, Point2 canvasPosition, bool dragging = true)
            : this(session, target, session.Board.treeRoot.TransformPoint(new Vector3((float)canvasPosition.X, (float)canvasPosition.Y, 0)), dragging) { }

        internal PointerContext(EditorSession session, GameObject target, Vector3 worldPosition, bool dragging = true)
        {
            this.session = session;
            previousTouch = UICamera.currentTouch; previousCamera = UICamera.current;
            previousCurrentCamera = UICamera.currentCamera; previousKey = UICamera.currentKey;
            previousId = UICamera.currentTouchID; previousWorldPosition = UICamera.lastWorldPosition;
            camera = UICamera.FindCameraForLayer(target.layer)?.GetComponent<Camera>() ?? UICamera.mainCamera
                ?? throw new InvalidOperationException("NGUI pointer fixture requires the editor UI camera.");
            var screen = camera.WorldToScreenPoint(worldPosition);
            touch = new UICamera.MouseOrTouch
            {
                key = KeyCode.Mouse0, pos = new Vector2(screen.x, screen.y), lastPos = new Vector2(screen.x, screen.y),
                delta = Vector2.zero, totalDelta = Vector2.zero, pressedCam = camera, current = target,
                pressed = target, dragged = dragging ? target : null, pressStarted = true, dragStarted = dragging
            };
            UICamera.current = UICamera.FindCameraForLayer(target.layer); UICamera.currentCamera = camera;
            UICamera.currentTouch = touch; UICamera.currentTouchID = -1; UICamera.currentKey = KeyCode.Mouse0;
            UICamera.lastWorldPosition = worldPosition;
        }

        internal Vector2 MoveScreenBy(Vector2 delta) => SetPosition(touch.pos + delta);

        internal Vector2 MoveCanvasBy(Point2 delta)
        {
            var current = session.CanvasPointer();
            var world = session.Board.treeRoot.TransformPoint(new Vector3((float)(current.X + delta.X), (float)(current.Y + delta.Y), 0));
            var screen = camera.WorldToScreenPoint(world);
            return SetPosition(new Vector2(screen.x, screen.y));
        }

        private Vector2 SetPosition(Vector2 screen)
        {
            var delta = screen - touch.pos;
            touch.lastPos = touch.pos; touch.pos = screen; touch.delta = delta; touch.totalDelta += delta;
            var ray = camera.ScreenPointToRay(new Vector3(screen.x, screen.y, 0));
            var plane = new Plane(session.Board.treeRoot.forward, session.Board.treeRoot.position);
            if (!plane.Raycast(ray, out var distance)) throw new InvalidOperationException("NGUI fixture pointer missed the canvas plane.");
            UICamera.lastWorldPosition = ray.GetPoint(distance);
            return delta;
        }

        public void Dispose()
        {
            UICamera.currentTouch = previousTouch; UICamera.current = previousCamera;
            UICamera.currentCamera = previousCurrentCamera; UICamera.currentTouchID = previousId;
            UICamera.currentKey = previousKey; UICamera.lastWorldPosition = previousWorldPosition;
        }
    }
}
