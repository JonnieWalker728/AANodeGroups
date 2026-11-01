using HarmonyLib;
using NodeGroups.Core;
using Studio.Scripts;
using Studio.Scripts.Nodes;
using Studio.Scripts.OperationManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AzureArchive.NodeGroups.Runtime.Tests;

/// <summary>
/// Opt-in native-handler regression checks for the independent integration-test process.
/// These checks exercise actual Node, NGUI and NodeParcel handlers rather than replacing
/// their results with managed-model calls. No assertion is reported until its native
/// effects have been observed. Normal plugin startup never calls this class.
/// </summary>
internal static class NativeCommandIntegrationTests
{
    private static KeyCode? forcedKey;
    private static bool mouseFixtureActive;
    private static bool mouseFixturePressed;

    /// <summary>
    /// Runs synchronous native-handler checks on the Unity main thread in the existing
    /// temporary fixture. On success, temporary groups/nodes are undone to the initial
    /// history boundary; camera, touch, input selection and native node selection are restored.
    /// </summary>
    /// <param name="session">The isolated fixture's attached editor session.</param>
    /// <param name="check">The caller's assertion reporter; it must throw when condition is false.</param>
    /// <remarks>
    /// MouseOrTouch and a scoped Input.GetMouseButton prefix supply a deterministic NGUI
    /// gesture rather than operating-system mouse input. Native OnDrag checks the real
    /// Unity button state in addition to its touch data. Escape is injected for one real
    /// Input.GetKeyDown/HandleKeys call. Both temporary prefixes are always removed.
    /// Tests prove native handler and
    /// callback execution; full screen raycast/physical keyboard behavior remains separate.
    /// </remarks>
    internal static void Run(EditorSession session, Action<bool, string> check)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NODEGROUPS_TEST_REPORT")))
            throw new InvalidOperationException("Advanced native checks require the explicitly isolated test process.");
        var beforeHistory = OperationManager.history.Count;
        var beforeNodes = session.Board.nodes.Count;
        var oldSelection = Blackboard.selected.ToArray();
        var oldGroup = session.SelectedGroup;
        var oldSelectedObject = UICamera.selectedObject;
        var success = false;
        try
        {
            var sourceId = CreateTemporaryGroup(session, new Point2(-2200, 1800));
            var first = session.CreateMember(sourceId, 0);
            var sourceAnchor = session.Workspace.Snapshot.GetGroup(sourceId).Anchor;
            var createdBounds = WidgetBounds(session, first);
            var createdCenter = new Point2(createdBounds.X + createdBounds.Width / 2, createdBounds.Y + createdBounds.Height / 2);
            var fallbackCenter = new Point2(sourceAnchor.X + 120, sourceAnchor.Y - 100);
            Plugin.Logger.LogInfo($"Empty-group native creation geometry: anchor={sourceAnchor}, root={session.Position(first)}, bounds={createdBounds}, center={createdCenter}, expectedCenter={fallbackCenter}");
            check(Near(createdCenter, fallbackCenter), "empty-group native creation centers its full widget bounds at the group fallback position");
            var second = session.CreateMember(sourceId, 0);
            var targetId = CreateTemporaryGroup(session, new Point2(2200, 1600));
            var resident = session.CreateMember(targetId, 0);
            session.Tick();

            CheckNameAndToggleCallbacks(session, sourceId, check);
            CheckNativeMoveAndEscape(session, first, check);
            CheckCancelledDragPreservesUnselectedMove(session, first, second, check);
            CheckPaletteUnpack(session, targetId, check);
            CheckCollapsedMultiDrop(session, sourceId, targetId, first, second, resident, check);

            // All temporary actions use native history. Undo the fixture additions and
            // observe the original node count instead of manually destroying story nodes.
            var limit = OperationManager.history.Count - beforeHistory + 8;
            while (OperationManager.history.Count > beforeHistory && limit-- > 0) OperationManager.Undo();
            session.Tick();
            check(OperationManager.history.Count == beforeHistory && session.Board.nodes.Count == beforeNodes,
                "advanced native checks undo every temporary group and node to the original history boundary");
            success = true;
        }
        finally
        {
            forcedKey = null;
            if (!success) session.CancelDrag();
            Blackboard.selected.Clear();
            foreach (var selected in oldSelection)
                if (selected != null && session.Find(EditorSession.Id(selected)) != null) Blackboard.selected.Add(selected);
            session.SelectedGroup = oldGroup;
            UICamera.selectedObject = oldSelectedObject;
        }
    }

    private static string CreateTemporaryGroup(EditorSession session, Point2 anchor)
    {
        Blackboard.selected.Clear();
        session.CreateGroup(true);
        var id = session.SelectedGroup ?? throw new InvalidOperationException("Temporary empty group was not selected.");
        session.Commit(session.Workspace.MoveGroup(id, anchor - session.Workspace.Snapshot.GetGroup(id).Anchor));
        return id;
    }

    private static void CheckNameAndToggleCallbacks(EditorSession session, string id, Action<bool, string> check)
    {
        var root = session.Board.treeRoot.Find("NodeGroup " + id) ?? throw new InvalidOperationException("Temporary group view is missing.");
        var title = root.Find("Header/Name (double click)") ?? throw new InvalidOperationException("Group title view is missing.");
        check(title.GetComponent<Collider>() != null || title.GetComponent<Collider2D>() != null,
            "NGUI group title has a live collider for input focus");
        // Native OnDoubleClick dispatches the UIEventListener delegate installed by GroupUi.
        UIEventListener.Get(title.gameObject).OnDoubleClick();
        var input = root.Find("Header/Name editor")?.GetComponent<UIInput>()
            ?? throw new InvalidOperationException("Title callback did not activate its separate native name editor.");
        check(input.enabled && input.isSelected, "native title double-click callback focuses the editable group name");
        var oldName = session.Workspace.Snapshot.GetGroup(id).Name;
        var history = OperationManager.history.Count;
        input.value = "NGUI 回调重命名测试";
        input.isSelected = false;
        session.HandleKeys(); // Real input-unselect path, not a direct Rename call.
        check(session.Workspace.Snapshot.GetGroup(id).Name == "NGUI 回调重命名测试" && !input.enabled,
            "native UIInput focus-loss path commits the edited name and disables the input");
        check(OperationManager.history.Count == history + 1, "NGUI name edit writes one original-history command");
        OperationManager.Undo();
        check(session.Workspace.Snapshot.GetGroup(id).Name == oldName, "one native Undo restores the name edited through NGUI");
        OperationManager.Redo();
        check(session.Workspace.Snapshot.GetGroup(id).Name == "NGUI 回调重命名测试", "one native Redo restores the NGUI name edit");

        var toggle = root.Find("Header/折叠") ?? throw new InvalidOperationException("Group fold button is missing.");
        UIEventListener.Get(toggle.gameObject).OnClick();
        check(session.Workspace.Snapshot.GetGroup(id).Collapsed, "native fold-button callback collapses its original group view");
        UIEventListener.Get(toggle.gameObject).OnClick();
        check(!session.Workspace.Snapshot.GetGroup(id).Collapsed, "native fold-button callback expands its original group view");
    }

    private static void CheckNativeMoveAndEscape(EditorSession session, Node node, Action<bool, string> check)
    {
        Select(node);
        var id = EditorSession.Id(node);
        var before = session.Position(node);
        var group = session.Workspace.Snapshot.FindGroupForNode(id)?.Id;
        var history = OperationManager.history.Count;
        using (var touch = new TouchContext(session, node))
        {
            node.OnDragStart();
            check(NativeHistory.SuppressNativeMovement, "native Node.OnDragStart activates movement-history suppression through its patch");
            var touchBefore = Node.GetTouchPos();
            var screenDelta = touch.MoveBy(new Vector2(120, -40));
            Plugin.Logger.LogInfo($"Advanced native touch: before={touchBefore}, after={Node.GetTouchPos()}, delta={screenDelta}, expected={node.expectedDragPos}, root={session.Position(node)}");
            node.OnDrag(screenDelta);
            Plugin.Logger.LogInfo($"Advanced native drag result: expected={node.expectedDragPos}, root={session.Position(node)}, selected={Blackboard.selected.Count}, active={Node.activeNode?.guid}");
            var movedWithSuppression = !Near(session.Position(node), before);
            if (!movedWithSuppression)
            {
                LogNativeDragAddress();
                // Diagnostic only. A movement under this different suppression state
                // cannot satisfy the original assertion or be reported as a passing test.
                var savedSuppression = NativeHistory.SuppressNativeMovement;
                var diagnosticHistory = OperationManager.history.Count;
                NativeHistory.SuppressNativeMovement = false;
                try
                {
                    node.OnDrag(screenDelta);
                    Plugin.Logger.LogInfo($"Advanced suppression-off diagnostic: expected={node.expectedDragPos}, root={session.Position(node)}, historyBefore={diagnosticHistory}, historyAfter={OperationManager.history.Count}, touch={Node.GetTouchPos()}");
                }
                finally { NativeHistory.SuppressNativeMovement = savedSuppression; }
            }
            check(movedWithSuppression, "native Node.OnDrag changes the actual native node root position");
            node.OnDragEnd();
        }
        check(!NativeHistory.SuppressNativeMovement && OperationManager.history.Count == history + 1,
            "native Node.OnDragEnd suppresses duplicate original movement history and commits one gesture");
        OperationManager.Undo();
        check(Near(session.Position(node), before) && session.Workspace.Snapshot.FindGroupForNode(id)?.Id == group,
            "one native Undo restores a gesture routed through the original node handlers");

        // A temporary prefix supplies just this Escape key-down to the real HandleKeys
        // path. It has no Harmony attributes and is never installed by Plugin.PatchAll.
        var inputMethod = AccessTools.Method(typeof(Input), nameof(Input.GetKeyDown), new[] { typeof(KeyCode) });
        var prefix = AccessTools.Method(typeof(NativeCommandIntegrationTests), nameof(KeyDownPrefix));
        var harmony = new Harmony(Plugin.Id + ".advanced-escape-fixture");
        history = OperationManager.history.Count;
        using var cancelledTouch = new TouchContext(session, node);
        try
        {
            node.OnDragStart();
            node.OnDrag(cancelledTouch.MoveBy(new Vector2(90, 20)));
            check(!Near(session.Position(node), before), "native node handler produces a preview before Escape cancellation");
            harmony.Patch(inputMethod, prefix: new HarmonyMethod(prefix));
            forcedKey = KeyCode.Escape;
            session.HandleKeys();
            forcedKey = null;
            check(session.CancelledNodeGesture && Near(session.Position(node), before),
                "real HandleKeys Escape path cancels and restores a native drag preview");
            node.OnDrag(cancelledTouch.MoveBy(new Vector2(40, 0)));
            check(Near(session.Position(node), before), "cancelled native gesture ignores late Node.OnDrag callbacks");
            node.OnDragEnd();
            check(!NativeHistory.SuppressNativeMovement && OperationManager.history.Count == history &&
                session.Workspace.Snapshot.FindGroupForNode(id)?.Id == group,
                "cancelled native Node.OnDragEnd writes no history and retains original ownership");
        }
        finally
        {
            forcedKey = null;
            harmony.Unpatch(inputMethod, HarmonyPatchType.Prefix, harmony.Id);
        }
    }

    private static bool KeyDownPrefix(KeyCode key, ref bool __result)
    {
        if (forcedKey != key) return true;
        __result = true;
        return false;
    }

    /// <summary>
    /// Regression for R7: a native gesture owns only its selected node geometry and
    /// callback history scope. Cancelling it must preserve another node's independent
    /// programmatic movement and its original native Undo/Redo command.
    /// </summary>
    private static void CheckCancelledDragPreservesUnselectedMove(EditorSession session, Node dragged,
        Node unrelated, Action<bool, string> check)
    {
        Select(dragged);
        var draggedBefore = session.Position(dragged);
        var unrelatedBefore = session.Position(unrelated);
        var ownerBefore = session.Workspace.Snapshot.FindGroupForNode(EditorSession.Id(dragged))?.Id;
        var historyBefore = OperationManager.history.Count;
        var unrelatedDelta = new Point2(48, 16);
        var unrelatedAfter = unrelatedBefore + unrelatedDelta;
        using (var touch = new TouchContext(session, dragged))
        {
            dragged.OnDragStart();
            dragged.OnDrag(touch.MoveBy(new Vector2(60, 20)));
            check(!Near(session.Position(dragged), draggedBefore) && Near(session.Position(unrelated), unrelatedBefore),
                "R7 native gesture previews only selected A before an unrelated B movement");

            // This occurs outside native OnDrag callbacks while the overall gesture is
            // active. The native Move call deliberately records its own original history.
            unrelated.Move(new Vector3((float)unrelatedDelta.X, (float)unrelatedDelta.Y, 0), false, false);
            check(NativeHistory.NativeMovementCallbackDepth == 0 && Near(session.Position(unrelated), unrelatedAfter) &&
                OperationManager.history.Count == historyBefore + 1,
                "R7 unselected B programmatic Move retains its native history during A's active gesture");

            session.CancelDrag();
            check(Near(session.Position(dragged), draggedBefore) && Near(session.Position(unrelated), unrelatedAfter) &&
                session.Workspace.Snapshot.FindGroupForNode(EditorSession.Id(dragged))?.Id == ownerBefore &&
                OperationManager.history.Count == historyBefore + 1,
                "R7 cancel restores A only and preserves B geometry, ownership and original history");
            dragged.OnDrag(touch.MoveBy(new Vector2(30, 0)));
            dragged.OnDragEnd();
            check(!NativeHistory.SuppressNativeMovement && NativeHistory.NativeMovementCallbackDepth == 0 &&
                Near(session.Position(dragged), draggedBefore) && Near(session.Position(unrelated), unrelatedAfter) &&
                OperationManager.history.Count == historyBefore + 1,
                "R7 cancelled A's late native drag/end callbacks add no history or overwrite B movement");
        }
        OperationManager.Undo();
        check(Near(session.Position(unrelated), unrelatedBefore) && Near(session.Position(dragged), draggedBefore) &&
            OperationManager.history.Count == historyBefore,
            "R7 one native Undo still restores B's independent movement after A cancellation");
        OperationManager.Redo();
        check(Near(session.Position(unrelated), unrelatedAfter) && Near(session.Position(dragged), draggedBefore),
            "R7 one native Redo reapplies B's independent movement without restoring A's cancelled preview");
        OperationManager.Undo();
        session.Tick(); // Reconcile native B coordinates before subsequent fixture gestures.
    }

    private static bool MouseButtonPrefix(int __0, ref bool __result)
    {
        if (!mouseFixtureActive) return true;
        __result = __0 == 0 && mouseFixturePressed;
        return false;
    }

    /// <summary>Logs original native method RVAs for offline read-only disassembly when a handler precondition is unclear.</summary>
    private static void LogNativeDragAddress()
    {
        try
        {
            var fields = typeof(Node).GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var module = process.Modules.Cast<System.Diagnostics.ProcessModule>().FirstOrDefault(candidate =>
                string.Equals(candidate.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase));
            if (module == null) return;
            foreach (var field in fields.Where(candidate => candidate.Name.StartsWith("NativeMethodInfoPtr_OnDrag_", StringComparison.Ordinal) ||
                candidate.Name.StartsWith("NativeMethodInfoPtr_OnDragStart_", StringComparison.Ordinal) ||
                candidate.Name.StartsWith("NativeMethodInfoPtr_GetTouchPos_", StringComparison.Ordinal)))
            {
                if (field.GetValue(null) is not IntPtr methodInfo || methodInfo == IntPtr.Zero) continue;
                // IL2CPP MethodInfo's first member is methodPointer. The pointer comes from
                // this live generated wrapper, never an untrusted address or another process.
                var native = System.Runtime.InteropServices.Marshal.ReadIntPtr(methodInfo);
                var rva = native.ToInt64() - module.BaseAddress.ToInt64();
                Plugin.Logger.LogInfo($"Advanced native method address: {field.Name}; rva=0x{rva:X}; moduleSize=0x{module.ModuleMemorySize:X}");
            }
        }
        catch (Exception error) { Plugin.Logger.LogWarning("Native handler address diagnostic unavailable: " + error.Message); }
    }

    private static void CheckPaletteUnpack(EditorSession session, string targetId, Action<bool, string> check)
    {
        var factory = ParcelFactory.instance ?? throw new InvalidOperationException("The native editor's palette factory is missing.");
        var scriptIndex = Enumerable.Range(0, factory.nodePrefabs.Count).FirstOrDefault(index =>
            factory.nodePrefabs[index].GetComponentInChildren<ScriptNode>(true) != null, -1);
        if (scriptIndex < 0) throw new InvalidOperationException("The native palette has no ScriptNode prefab.");
        var beforeParcels = Object.FindObjectsOfType<NodeParcel>(true).Select(parcel => parcel.Pointer).ToHashSet();
        var count = session.Board.nodes.Count;
        var history = OperationManager.history.Count;
        factory.CreateParcel(scriptIndex);
        var parcel = Object.FindObjectsOfType<NodeParcel>(true).FirstOrDefault(candidate => !beforeParcels.Contains(candidate.Pointer)) ??
            throw new InvalidOperationException("ParcelFactory.CreateParcel did not expose its new palette parcel.");
        var preview = parcel.content?.GetComponentInChildren<Node>(true) ?? throw new InvalidOperationException("The native parcel has no node content.");
        var bounds = session.DropBounds(session.Workspace.Snapshot.GetGroup(targetId));
        var position = new Vector3((float)(bounds.X + bounds.Width / 2), (float)(bounds.Y + bounds.Height / 2), 0);
        parcel.Unpack(position); // Native registration, creation history and the actual palette patch.
        var createdId = EditorSession.Id(preview);
        session.Tick();
        check(session.Find(createdId) != null && session.Board.nodes.Count == count + 1,
            "native ParcelFactory and NodeParcel.Unpack create exactly one registered node");
        check(session.Workspace.Snapshot.FindGroupForNode(createdId)?.Id == targetId,
            "native palette Unpack patch assigns its created node to the hit group");
        check(OperationManager.history.Count >= history + 2 && OperationManager.history.Peek().linkedPrev != null,
            "palette membership history is linked to the original native creation command");
        OperationManager.Undo();
        session.Tick();
        check(session.Find(createdId) == null && session.Workspace.Snapshot.FindGroupForNode(createdId) == null &&
            OperationManager.history.Count == history, "one native Undo removes palette creation and membership together");
        OperationManager.Redo();
        session.Tick();
        check(session.Find(createdId) != null && session.Workspace.Snapshot.FindGroupForNode(createdId)?.Id == targetId,
            "one native Redo restores palette node GUID and membership together");
        OperationManager.Undo();
        session.Tick();
    }

    private static void CheckCollapsedMultiDrop(EditorSession session, string sourceId, string targetId,
        Node first, Node second, Node resident, Action<bool, string> check)
    {
        var targetRoot = session.Board.treeRoot.Find("NodeGroup " + targetId) ?? throw new InvalidOperationException("Target group view is missing.");
        var toggle = targetRoot.Find("Header/折叠") ?? throw new InvalidOperationException("Target fold button is missing.");
        UIEventListener.Get(toggle.gameObject).OnClick();
        check(session.Workspace.Snapshot.GetGroup(targetId).Collapsed, "multi-drop fixture folds its populated target through the NGUI button");
        Select(first, second);
        var firstBefore = session.Position(first); var secondBefore = session.Position(second);
        var relative = secondBefore - firstBefore;
        var history = OperationManager.history.Count;
        var bounds = session.DropBounds(session.Workspace.Snapshot.GetGroup(targetId));
        var target = new Point2(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        using (var touch = new TouchContext(session, first))
        {
            first.OnDragStart();
            first.OnDrag(touch.MoveTo(target));
            check(session.DropTarget == targetId, "native multi-node drag preview hits the frozen collapsed target card");
            first.OnDragEnd();
        }
        check(!session.Workspace.Snapshot.GetGroup(targetId).Collapsed &&
            session.Workspace.Snapshot.FindGroupForNode(EditorSession.Id(first))?.Id == targetId &&
            session.Workspace.Snapshot.FindGroupForNode(EditorSession.Id(second))?.Id == targetId,
            "native collapsed-card drop expands the target and transfers every selected node");
        check(Near(session.Position(second) - session.Position(first), relative),
            "native collapsed-target placement translates the complete selection without changing relative layout");
        var residentBounds = WidgetBounds(session, resident);
        check(!Intersects(WidgetBounds(session, first), residentBounds) && !Intersects(WidgetBounds(session, second), residentBounds),
            "collapsed-target placement keeps the full incoming native widget bounds clear of existing members");
        check(OperationManager.history.Count == history + 1, "native multi-node movement, group transfer and automatic placement form one history command");
        OperationManager.Undo();
        check(session.Workspace.Snapshot.GetGroup(targetId).Collapsed &&
            session.Workspace.Snapshot.FindGroupForNode(EditorSession.Id(first))?.Id == sourceId &&
            session.Workspace.Snapshot.FindGroupForNode(EditorSession.Id(second))?.Id == sourceId &&
            Near(session.Position(first), firstBefore) && Near(session.Position(second), secondBefore),
            "one native Undo restores both drag positions, source ownership and target collapse state");
    }

    private static void Select(params Node[] nodes)
    {
        Blackboard.selected.Clear();
        foreach (var node in nodes) Blackboard.selected.Add(node);
    }
    private static bool Near(Point2 first, Point2 second) => Math.Abs(first.X - second.X) < .03 && Math.Abs(first.Y - second.Y) < .03;
    private static bool Intersects(Rect2 first, Rect2 second) => first.X <= second.X + second.Width && first.X + first.Width >= second.X &&
        first.Y <= second.Y + second.Height && first.Y + first.Height >= second.Y;
    private static Rect2 WidgetBounds(EditorSession session, Node node)
    {
        var corners = node.GetComponentsInChildren<UIWidget>(true).SelectMany(widget => widget.worldCorners.ToArray())
            .Select(corner => session.Board.treeRoot.InverseTransformPoint(corner)).ToArray();
        if (corners.Length == 0) throw new InvalidOperationException("Placement check requires real native widget bounds.");
        var minX = corners.Min(point => point.x); var maxX = corners.Max(point => point.x);
        var minY = corners.Min(point => point.y); var maxY = corners.Max(point => point.y);
        return new(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>Supplies deterministic native NGUI pointer state and restores all replaced global fields on disposal.</summary>
    // 回收站回归复用同一真实原生触点夹具，避免复制输入补丁或伪造节点移动结果。
    internal sealed class TouchContext : IDisposable
    {
        private readonly EditorSession session;
        private readonly Camera camera;
        private readonly UICamera.MouseOrTouch? previousTouch;
        private readonly UICamera? previousCamera;
        private readonly Camera? previousCurrentCamera;
        private readonly KeyCode previousKey;
        private readonly int previousId;
        private readonly Vector3 previousWorldPosition;
        private readonly Transform? mouseTracker;
        private readonly Vector3 previousTrackerPosition;
        private readonly Node node;
        private readonly Node? previousActiveNode;
        private readonly UICamera.MouseOrTouch touch;
        private readonly Harmony mouseHarmony;
        private readonly System.Reflection.MethodInfo mouseButtonMethod;

        internal TouchContext(EditorSession session, Node node)
        {
            this.session = session;
            this.node = node;
            previousActiveNode = Node.activeNode;
            previousTouch = UICamera.currentTouch;
            previousCamera = UICamera.current;
            previousCurrentCamera = UICamera.currentCamera;
            previousKey = UICamera.currentKey;
            previousId = UICamera.currentTouchID;
            previousWorldPosition = UICamera.lastWorldPosition;
            mouseTracker = session.Editor.mouseTracker;
            previousTrackerPosition = mouseTracker != null ? mouseTracker.position : Vector3.zero;
            camera = UICamera.currentCamera ?? UICamera.mainCamera ?? throw new InvalidOperationException("Native drag tests require the editor's UI camera.");
            mouseButtonMethod = AccessTools.Method(typeof(Input), nameof(Input.GetMouseButton), new[] { typeof(int) });
            mouseHarmony = new Harmony(Plugin.Id + ".advanced-mouse-fixture");
            mouseHarmony.Patch(mouseButtonMethod, prefix: new HarmonyMethod(AccessTools.Method(typeof(NativeCommandIntegrationTests), nameof(MouseButtonPrefix))));
            mouseFixtureActive = true;
            mouseFixturePressed = true;
            var screen = camera.WorldToScreenPoint(node.root.position);
            var screenPosition = new Vector2(screen.x, screen.y);
            touch = new UICamera.MouseOrTouch
            {
                key = KeyCode.Mouse0, pos = screenPosition, lastPos = screenPosition, delta = Vector2.zero,
                totalDelta = Vector2.zero, pressedCam = camera, current = node.gameObject,
                pressed = node.gameObject, dragged = node.gameObject, pressStarted = true, dragStarted = true
            };
            UICamera.current = UICamera.FindCameraForLayer(node.gameObject.layer);
            UICamera.currentCamera = camera;
            UICamera.currentTouch = touch;
            UICamera.currentTouchID = -1;
            UICamera.currentKey = KeyCode.Mouse0;
            UICamera.lastWorldPosition = node.root.position;
            if (mouseTracker != null) mouseTracker.position = node.root.position;
            // NGUI dispatches OnPress before OnDragStart. Native drag handlers can rely
            // on press-time and active-node state; skipping that event is not a real gesture.
            try { node.OnPress(true); }
            catch { Dispose(); throw; }
            Plugin.Logger.LogInfo($"Advanced native press: active={Node.activeNode?.guid}, inBatch={node.inBatch}, longPress={node.isLongPressed}, rootScale={session.Board.treeRoot.lossyScale.x:R}/{session.Board.treeRoot.lossyScale.y:R}/{session.Board.treeRoot.lossyScale.z:R}");
        }
        internal Vector2 MoveBy(Vector2 delta) => SetPosition(touch.pos + delta);
        internal Vector2 MoveTo(Point2 canvasPosition)
        {
            var world = session.Board.treeRoot.TransformPoint(new Vector3((float)canvasPosition.X, (float)canvasPosition.Y, 0));
            UICamera.lastWorldPosition = world;
            var screen = camera.WorldToScreenPoint(world);
            return SetPosition(new Vector2(screen.x, screen.y));
        }
        private Vector2 SetPosition(Vector2 screen)
        {
            var delta = screen - touch.pos;
            touch.lastPos = touch.pos; touch.pos = screen; touch.delta = delta; touch.totalDelta += delta;
            // NGUI's event dispatch also publishes its raycast hit. Updating only touch.pos
            // leaves GetTouchPos at the original hit and makes a native drag look stationary.
            var ray = camera.ScreenPointToRay(new Vector3(screen.x, screen.y, 0));
            var plane = new Plane(session.Board.treeRoot.forward, session.Board.treeRoot.position);
            if (!plane.Raycast(ray, out var distance)) throw new InvalidOperationException("Synthetic native pointer ray did not hit the canvas plane.");
            UICamera.lastWorldPosition = ray.GetPoint(distance);
            // StudioCommon normally updates this pointer transform during its Update.
            // A synchronous handler test must publish the same state before OnDrag.
            if (mouseTracker != null) mouseTracker.position = UICamera.lastWorldPosition;
            return delta;
        }
        public void Dispose()
        {
            mouseFixturePressed = false;
            try { node.OnPress(false); }
            finally
            {
                mouseFixtureActive = false;
                try { mouseHarmony.Unpatch(mouseButtonMethod, HarmonyPatchType.Prefix, mouseHarmony.Id); }
                finally
                {
                    Node.activeNode = previousActiveNode;
                    UICamera.currentTouch = previousTouch;
                    UICamera.current = previousCamera;
                    UICamera.currentCamera = previousCurrentCamera;
                    UICamera.currentTouchID = previousId;
                    UICamera.currentKey = previousKey;
                    UICamera.lastWorldPosition = previousWorldPosition;
                    if (mouseTracker != null) mouseTracker.position = previousTrackerPosition;
                }
            }
        }
    }
}
