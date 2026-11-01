using AzureArchive.Automation;
using Il2CppInterop.Runtime;
using NodeGroups.Core;
using Studio.Scripts;
using Studio.Scripts.Nodes;
using Studio.Scripts.OperationManagement;
using System.Text.Json;
using UnityEngine;

namespace AzureArchive.NodeGroups.Runtime.Tests;

/// <summary>
/// Runs natural-frame name editing, stable title movement, resize cursor lifecycles and
/// edge hit checks. Owns scoped pointer fixtures and restores borrowed graph and UI state.
/// </summary>
internal static partial class GroupControlsTests
{
    private enum MotionPhase { LegacyInput, EditingChinese, EditingEmpty, EditingEnd, AwaitTitle, Moving, AwaitMove, Raycasting }

    private sealed record HitTarget(string Name, Vector3 LocalPosition, Transform Expected);

    /// <summary>Owns only temporary group commands and restores borrowed UI/canvas state.</summary>
    private sealed class MotionState
    {
        internal EditorSession Owner = null!;
        internal int History, CompileDelta, Frames, Step, GestureHistory;
        internal NodeState[] Nodes = Array.Empty<NodeState>();
        internal Dictionary<string, Vector3> RootPositions = new();
        internal Node[] PreviousNodes = Array.Empty<Node>();
        internal string Story = "", Id = "", Name = "自然帧移动组";
        internal byte[] Compiled = Array.Empty<byte>();
        internal string? PreviousGroup;
        internal GameObject? PreviousObject;
        internal Vector3 TreePosition;
        internal UILabel Title = null!;
        internal UIInput Input = null!;
        internal GameObject? LegacyRoot;
        internal UILabel? LegacyLabel;
        internal UIInput? LegacyInput;
        internal UIEventListener Header = null!;
        internal Vector3[] Glyphs = Array.Empty<Vector3>();
        internal Point2 Pointer;
        internal Rect2 OriginalBounds;
        internal string[] Members = Array.Empty<string>();
        internal MotionPhase Phase;
        internal HitTarget[] Targets = Array.Empty<HitTarget>();
        internal Texture2D? RetiredTexture;
        internal bool RetirementVerified;
        internal bool BorrowedStateRestored;
    }
    private static MotionState? motion;

    /// <summary>
    /// Begins natural-frame editing, movement and edge-hit acceptance checks.
    /// TickMotion must run in subsequent real Unity Update frames; this method
    /// never drives UILabel.Start or panel lifecycle fields by hand.
    /// </summary>
    internal static void BeginMotion(EditorSession session, Action<bool, string> check)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NODEGROUPS_TEST_REPORT")) || motion != null)
            throw new InvalidOperationException("Cross-frame iteration 3 checks require one isolated test process.");
        var state = new MotionState
        {
            Owner = session, History = OperationManager.history.Count, CompileDelta = OperationManager.compileDelta,
            Nodes = session.ReadNodes(), Story = ProjectFileStore.Serialize(session.Editor.CaptureProject()),
            RootPositions = session.Board.nodes.ToArray().ToDictionary(EditorSession.Id, node => node.root.localPosition),
            Compiled = AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray(),
            PreviousNodes = Blackboard.selected.ToArray(), PreviousGroup = session.SelectedGroup,
            PreviousObject = UICamera.selectedObject, TreePosition = session.Board.treeRoot.position,
            Phase = MotionPhase.LegacyInput
        };
        motion = state;
        var source = session.Board.nodes.ToArray().First(node => node.TryCast<ScriptNode>() != null);
        Blackboard.selected.Clear(); Blackboard.selected.Add(source); session.CreateGroup(false);
        state.Id = session.SelectedGroup ?? throw new InvalidOperationException("Motion fixture group has no selection.");
        var initial = session.Bounds(session.Workspace.Snapshot.GetGroup(state.Id));
        session.Commit(session.Workspace.SetExpandedBounds(state.Id,
            new Rect2(initial.X - 80, initial.Y - 80, initial.Width + 160, initial.Height + 160)));
        session.Tick();
        var root = GroupRoot(session, state.Id);
        state.Title = root.Find("Header/Name (double click)")!.GetComponent<UILabel>();
        state.Header = UIEventListener.Get(root.Find("Header")!.gameObject);
        PanToLocal(state, new Vector3(160, -65, 0));
        // Reproduce the disabled-input ownership bug in an independent disposable
        // label. Shipping group titles never receive these legacy callbacks.
        state.LegacyRoot = new GameObject("NodeGroups legacy input regression") { layer = root.gameObject.layer };
        state.LegacyRoot.transform.SetParent(session.Board.treeRoot, false);
        state.LegacyRoot.transform.localPosition = new Vector3(500000, 500000, 0);
        state.LegacyLabel = NativeVisualStyle.Label(state.LegacyRoot, state.Title, 600, 100, 1, "回归示例");
        state.LegacyInput = state.LegacyRoot.AddComponent<UIInput>();
        state.LegacyInput.label = state.LegacyLabel; state.LegacyInput.value = "回归示例";
        state.LegacyInput.submitOnUnselect = false; state.LegacyInput.enabled = false;
        using (var touch = new PointerContext(session, state.LegacyRoot, state.LegacyRoot.transform.position, false))
            state.LegacyInput.OnSelect(true);
        check(!state.LegacyInput.enabled,
            "iteration 3 isolated historical-input regression invokes native selection with its Behaviour disabled");
    }

    private static void BeginActualEdit(EditorSession session, MotionState state, Action<bool, string> check)
    {
        var root = GroupRoot(session, state.Id);
        using (var touch = new PointerContext(session, state.Title.gameObject, state.Title.transform.position, false))
            UIEventListener.Get(state.Title.gameObject).OnDoubleClick();
        state.Input = root.GetComponentsInChildren<UIInput>(true).Single();
        state.Input.value = state.Name;
        state.Input.cursorPosition = 1;
        check(state.Input.isSelected && state.Input.value == state.Name,
            "iteration 3 natural-frame fixture enters the original name editor with a Chinese insertion position");
        state.Phase = MotionPhase.EditingChinese; state.Frames = 0;
    }

    /// <summary>
    /// Observes real caret widgets and title geometry across frames, invokes the
    /// actual group event handlers, then raycasts the visible edges and their
    /// inner hit bands through the original NGUI resolver.
    /// </summary>
    /// <returns>True only after temporary history, canvas and graph values are restored.</returns>
    internal static bool TickMotion(EditorSession session, Action<bool, string> check, Action<string>? capturePreview = null)
    {
        var state = motion ?? throw new InvalidOperationException("Iteration 3 motion fixture was not started.");
        if (!ReferenceEquals(state.Owner, session) || session.IsDisposed)
            throw new InvalidOperationException("Iteration 3 cross-frame fixture changed its editor owner.");
        state.Frames++;
        if (state.Phase == MotionPhase.LegacyInput)
        {
            if (state.Frames < 3) return false;
            var legacy = state.LegacyInput ?? throw new InvalidOperationException("Independent legacy input was lost.");
            var label = state.LegacyLabel ?? throw new InvalidOperationException("Independent legacy label was lost.");
            var legacyRoot = state.LegacyRoot ?? throw new InvalidOperationException("Independent legacy owner was lost.");
            label.text = "回归示例 (7)";
            using (var touch = new PointerContext(session, legacyRoot, legacyRoot.transform.position))
                legacy.OnDrag(new Vector2(1, 0));
            check(!legacy.enabled && label.text == "回归示例",
                "iteration 3 original disabled UIInput drag reproduces the missing count on an independent historical label");
            DisposeLegacy(state);
            BeginActualEdit(session, state, check);
            return false;
        }
        if (state.Phase is MotionPhase.EditingChinese or MotionPhase.EditingEmpty or MotionPhase.EditingEnd)
        {
            if (state.Frames < 3) return false;
            var caret = state.Input.caret;
            var label = state.Input.label;
            var tint = caret == null ? Color.white : caret.color;
            var colors = caret == null ? Array.Empty<Color>() : Enumerable.Range(0, caret.geometry.cols.Count)
                .Select(index => caret.geometry.cols[index]).ToArray();
            var doInit = typeof(UIInput).GetProperty("mDoInit")?.GetValue(state.Input);
            var selectTime = typeof(UIInput).GetProperty("mSelectTime")?.GetValue(state.Input);
            Plugin.Logger.LogInfo($"Iteration 3 caret diagnostic: phase={state.Phase}; frames={state.Frames}; unityFrame={Time.frameCount}; " +
                $"input={state.Input.name}; enabled={state.Input.enabled}; selected={state.Input.isSelected}; active={state.Input.gameObject.activeInHierarchy}; " +
                $"value={state.Input.value}; caretColor={state.Input.caretColor}; doInit={doInit}; selectTime={selectTime}; " +
                $"label={label?.name}; labelActive={label?.gameObject.activeInHierarchy}; labelEnabled={label?.enabled}; labelText={label?.text}; " +
                $"font={label?.trueTypeFont?.name}; glyphs={label?.geometry.verts.Count}; " +
                $"selectedObject={NativeName(UICamera.selectedObject)}; scheme={UICamera.currentScheme}; inputSelection={NativeName(UIInput.selection)}; " +
                $"caret={NativeName(caret)}; caretEnabled={caret?.enabled}; caretColorActual={caret?.color}; caretActive={caret?.gameObject.activeInHierarchy}; " +
                $"caretVisible={caret?.isVisible}; caretGeometry={caret?.geometry.verts.Count}; " +
                $"caretVertexColors=[{string.Join(";", colors.Select(color => color.ToString()))}]");
            // Native UIInput blinks the widget and writes caretColor into its
            // vertex buffer, leaving the UITexture tint white. Observe a visible
            // phase within one bounded blink interval and inspect the submitted
            // tint × vertex RGB rather than imposing a different renderer contract.
            var visibleCaret = caret != null && caret.enabled && caret.gameObject.activeInHierarchy &&
                caret.isVisible && caret.finalAlpha > .001f && caret.geometry.hasVertices &&
                colors.Length == caret.geometry.verts.Count && colors.Length > 0;
            if (!visibleCaret && state.Input.gameObject.activeInHierarchy && state.Input.isSelected && state.Frames < 45) return false;
            check(state.Input.gameObject.activeInHierarchy && state.Input.isSelected && visibleCaret && colors.All(color =>
                color.a > .001f && color.r * tint.r < .05f && color.g * tint.g < .05f && color.b * tint.b < .05f),
                "iteration 3 actual native caret renders black in " + state.Phase);
            if (state.Phase == MotionPhase.EditingChinese) capturePreview?.Invoke("caret");
            if (state.Phase == MotionPhase.EditingChinese)
            {
                state.Input.value = ""; state.Input.cursorPosition = 0;
                state.Phase = MotionPhase.EditingEmpty;
            }
            else if (state.Phase == MotionPhase.EditingEmpty)
            {
                state.Input.value = state.Name; state.Input.cursorPosition = state.Name.Length;
                state.Phase = MotionPhase.EditingEnd;
            }
            else
            {
                state.Input.isSelected = false; session.Tick();
                state.Phase = MotionPhase.AwaitTitle;
            }
            state.Frames = 0;
            return false;
        }
        if (state.Phase == MotionPhase.AwaitTitle)
        {
            if (state.Frames < 3) return false;
            check(state.Title.gameObject.activeInHierarchy && state.Title.enabled &&
                state.Title.text == state.Name + " (1)" && state.Title.geometry.hasVertices && state.Title.geometry.verts.Count > 0,
                "iteration 3 an edited Chinese group name and membership count produce complete real title geometry");
            state.Glyphs = GlyphVertices(state.Title);
            state.OriginalBounds = session.Bounds(session.Workspace.Snapshot.GetGroup(state.Id));
            state.Members = session.Workspace.Snapshot.GetGroup(state.Id).MemberNodeIds.ToArray();
            state.Pointer = new Point2(state.OriginalBounds.X + 160, state.OriginalBounds.Y + state.OriginalBounds.Height - 65);
            state.GestureHistory = OperationManager.history.Count;
            using (var touch = new PointerContext(session, state.Header.gameObject, state.Pointer))
            {
                // Native OnSelect/OnDrag ignore Behaviour.enabled. If a legacy
                // input still owns the display label, exercise the real methods
                // which previously replaced Name(count) with its cached Name.
                var legacyInput = state.Title.GetComponent<UIInput>();
                if (legacyInput != null) { legacyInput.OnSelect(true); legacyInput.OnDrag(Vector2.zero); }
                state.Header.OnHover(true); state.Header.OnDragStart(); state.Header.OnHover(false);
            }
            check(session.Cursor.Current == GroupCursor.Kind.Default && session.Cursor.IsDragging,
                "retained header movement keeps the ordinary pointer and remains latched after leaving its drag surface");
            state.Phase = MotionPhase.Moving; state.Frames = 0;
            return false;
        }
        if (state.Phase == MotionPhase.Moving)
        {
            CheckMovingTitle(session, state, check);
            using (var touch = new PointerContext(session, state.Header.gameObject, state.Pointer))
            {
                var delta = new Point2(12, state.Step % 2 == 0 ? -6 : 6);
                state.Header.OnDrag(touch.MoveCanvasBy(delta)); state.Pointer += delta;
            }
            session.Tick();
            if (++state.Step < 10) return false;
            using (var touch = new PointerContext(session, state.Header.gameObject, state.Pointer))
                state.Header.OnDragEnd();
            check(OperationManager.history.Count == state.GestureHistory + 1 && !session.Cursor.IsDragging &&
                session.Cursor.Current == GroupCursor.Kind.Default,
                "iteration 3 multi-frame group move writes one native-history command and restores the default cursor");
            state.Phase = MotionPhase.AwaitMove; state.Frames = 0;
            return false;
        }
        if (state.Phase == MotionPhase.AwaitMove)
        {
            CheckMovingTitle(session, state, check);
            if (state.Frames < 3) return false;
            OperationManager.Undo(); session.Tick();
            check(NodesUnchanged(session, state.Nodes) && OperationManager.history.Count == state.GestureHistory,
                "iteration 3 one native Undo restores all node positions after the multi-frame header move");
            CheckCursorHandlers(session, state, check);
            CheckCursorLifecycle(session, state, check);
            state.Targets = HitTargets(session, state).ToArray();
            state.Step = 0; state.Frames = 0; state.Phase = MotionPhase.Raycasting;
            PanToLocal(state, state.Targets[0].LocalPosition);
            return false;
        }
        if (state.Phase == MotionPhase.Raycasting)
        {
            if (state.Frames < 2) return false;
            if (!state.RetirementVerified)
            {
                check(state.RetiredTexture == null,
                    "iteration 3 a disposed cursor's owned native texture is destroyed after two real Unity frames");
                state.RetirementVerified = true;
            }
            var target = state.Targets[state.Step];
            var hit = ObserveLocalHit(state, target.LocalPosition);
            check(hit != null && (hit.Pointer == target.Expected.gameObject.Pointer || hit.transform.IsChildOf(target.Expected)),
                "iteration 3 actual NGUI edge raycast resolves " + target.Name + " to " + target.Expected.name);
            state.Step++; state.Frames = 0;
            if (state.Step < state.Targets.Length) { PanToLocal(state, state.Targets[state.Step].LocalPosition); return false; }
            CompleteMotion(session, state, check);
            motion = null;
            return true;
        }
        throw new InvalidOperationException("Unknown iteration 3 motion phase.");
    }

    private static void CheckMovingTitle(EditorSession session, MotionState state, Action<bool, string> check)
    {
        var current = GlyphVertices(state.Title);
        check(state.Title.text == state.Name + " (1)" && state.Title.gameObject.activeInHierarchy &&
            state.Title.enabled && state.Title.geometry.hasVertices && current.Length == state.Glyphs.Length &&
            current.Zip(state.Glyphs, (first, second) => (first - second).sqrMagnitude < .001f).All(equal => equal) &&
            session.Workspace.Snapshot.GetGroup(state.Id).MemberNodeIds.SequenceEqual(state.Members),
            "iteration 3 natural motion frame " + state.Phase + "/" + state.Step + " retains the full Chinese title, parentheses, count and glyph geometry");
        check(IsYellow(GroupRoot(session, state.Id).Find("Region frame")!.GetComponent<UISprite>().color),
            "iteration 3 natural motion frame retains the persistent selected yellow border");
    }

    private static void CheckCursorHandlers(EditorSession session, MotionState state, Action<bool, string> check)
    {
        var root = GroupRoot(session, state.Id);
        var original = session.Bounds(session.Workspace.Snapshot.GetGroup(state.Id));
        var modes = new Dictionary<int, GroupCursor.Kind>
        {
            [1] = GroupCursor.Kind.Horizontal, [2] = GroupCursor.Kind.Horizontal,
            [4] = GroupCursor.Kind.Vertical, [5] = GroupCursor.Kind.DiagonalLeft, [6] = GroupCursor.Kind.DiagonalRight
        };
        var textures = new Dictionary<GroupCursor.Kind, long>();
        foreach (var (mask, mode) in modes)
        {
            var handle = root.Find("Resize " + mask)!;
            var listener = UIEventListener.Get(handle.gameObject);
            using var touch = new PointerContext(session, handle.gameObject, handle.position);
            listener.OnHover(true);
            var texture = session.Cursor.ActiveTexture;
            var hotspot = session.Cursor.Hotspot;
            check(session.Cursor.Current == mode && !session.Cursor.IsDragging && texture != null &&
                texture.width > 0 && texture.height > 0 && hotspot.x >= 0 && hotspot.y >= 0 &&
                hotspot.x < texture.width && hotspot.y < texture.height,
                "iteration 3 " + handle.name + " hover selects its directional cursor with a real native texture and valid hotspot");
            var activeTexture = texture ?? throw new InvalidOperationException("Directional cursor texture is missing.");
            var pixels = activeTexture.GetPixels32().ToArray();
            check(pixels.Any(pixel => pixel.a == 0) && pixels.Any(pixel => pixel.a > 200 && pixel.r < 20 && pixel.g < 20 && pixel.b < 20) &&
                pixels.Any(pixel => pixel.a > 200 && pixel.r > 235 && pixel.g > 235 && pixel.b > 235),
                "iteration 3 " + handle.name + " native cursor texture has a transparent background, dark outline and visible light core");
            if (!textures.ContainsKey(mode)) textures.Add(mode, activeTexture.Pointer.ToInt64());
            listener.OnDragStart(); listener.OnHover(false);
            var history = OperationManager.history.Count;
            listener.OnDrag(touch.MoveCanvasBy(new Point2((mask & 1) != 0 ? -20 : 20, (mask & 4) != 0 ? -20 : 0)));
            check(session.Cursor.Current == mode && session.Cursor.IsDragging &&
                session.Bounds(session.Workspace.Snapshot.GetGroup(state.Id)) != original,
                "iteration 3 " + handle.name + " resize keeps the directional cursor after dragging beyond its handle");
            session.CancelDrag(); listener.OnDragEnd(); session.Tick();
            check(session.Cursor.Current == GroupCursor.Kind.Default && !session.Cursor.IsDragging &&
                session.Bounds(session.Workspace.Snapshot.GetGroup(state.Id)) == original &&
                OperationManager.history.Count == history && NodesUnchanged(session, state.Nodes),
                "iteration 3 cancelling " + handle.name + " restores the original region, nodes, history and default cursor");
        }
        check(textures.Values.Distinct().Count() == 4 && session.Cursor.ActiveTexture == null,
            "iteration 3 the four resize directions retain separate native cursor resources and release the active cursor on cancellation");
    }

    /// <summary>Tests the cursor controller's genuine hover/focus/disposal state without physical mouse injection.</summary>
    private static void CheckCursorLifecycle(EditorSession session, MotionState state, Action<bool, string> check)
    {
        var root = GroupRoot(session, state.Id);
        var header = root.Find("Header")!.gameObject;
        var title = state.Title.gameObject;
        var button = root.Find("Header")!.GetComponentsInChildren<UILabel>(true)
            .First(label => label.text == "解散").transform.parent.gameObject;
        var previousHover = UICamera.hoveredObject;
        var owner = new GameObject("NodeGroups temporary cursor owner") { layer = root.gameObject.layer };
        owner.transform.SetParent(session.Board.treeRoot, false);
        try
        {
            session.Cursor.Hover(header, GroupCursor.Kind.Horizontal, true);
            session.Cursor.Hover(title, GroupCursor.Kind.Horizontal, true);
            session.Cursor.Hover(header, GroupCursor.Kind.Horizontal, false);
            check(session.Cursor.Current == GroupCursor.Kind.Horizontal && !session.Cursor.IsDragging,
                "iteration 3 a delayed leave from the old header cannot clear the newly hovered title cursor");
            UICamera.hoveredObject = button;
            session.Cursor.Hover(header, GroupCursor.Kind.Horizontal, true);
            session.Cursor.BeginDrag(header, GroupCursor.Kind.Horizontal); session.Cursor.EndDrag();
            check(session.Cursor.Current == GroupCursor.Kind.Default && !session.Cursor.IsDragging,
                "iteration 3 drag release over a header button does not restore the old parent header's stale directional cursor");
            UICamera.hoveredObject = title;
            session.Cursor.Hover(title, GroupCursor.Kind.Horizontal, true);
            session.Cursor.BeginDrag(title, GroupCursor.Kind.Horizontal); session.Cursor.EndDrag();
            check(session.Cursor.Current == GroupCursor.Kind.Horizontal && !session.Cursor.IsDragging,
                "iteration 3 drag release over the exact native-hovered title restores its valid directional cursor");
            session.Cursor.Poll(true); session.Cursor.BeginDrag(title, GroupCursor.Kind.Horizontal); session.Cursor.Poll(false);
            check(session.Cursor.Current == GroupCursor.Kind.Default && !session.Cursor.IsDragging && session.Cursor.ActiveTexture == null,
                "iteration 3 a genuine focused-to-unfocused observation resets a latched cursor and active texture");
            session.Cursor.Poll(Application.isFocused);
            session.Cursor.Hover(owner, GroupCursor.Kind.Horizontal, true); owner.SetActive(false);
            session.Cursor.Poll(Application.isFocused);
            check(session.Cursor.Current == GroupCursor.Kind.Default && !session.Cursor.IsDragging,
                "iteration 3 polling releases the cursor when its owned hover surface becomes inactive");
            using (var disposable = new GroupCursor())
            {
                owner.SetActive(true); disposable.Hover(owner, GroupCursor.Kind.Vertical, true);
                state.RetiredTexture = disposable.ActiveTexture;
                check(state.RetiredTexture != null,
                    "iteration 3 isolated cursor-disposal fixture owns a real submitted native texture");
                disposable.Dispose();
                check(disposable.Current == GroupCursor.Kind.Default && !disposable.IsDragging && disposable.ActiveTexture == null,
                    "iteration 3 disposing a cursor immediately restores the platform default and clears its active resource");
            }
        }
        finally
        {
            session.Cursor.Reset(); UICamera.hoveredObject = previousHover; session.Cursor.Reset();
            owner.SetActive(false); UnityEngine.Object.Destroy(owner);
        }
    }

    private static IEnumerable<HitTarget> HitTargets(EditorSession session, MotionState state)
    {
        // A completed native Undo has just moved the tree's original members and
        // group view. Synchronize PhysX before sampling button centers; cached
        // world bounds otherwise still include the previous gesture translation.
        Physics.SyncTransforms();
        var root = GroupRoot(session, state.Id);
        foreach (var mask in new[] { 1, 2, 4, 5, 6 })
        {
            var expected = root.Find("Resize " + mask)!;
            var box = expected.GetComponent<Collider>().bounds;
            var minimum = root.InverseTransformPoint(box.min);
            var maximum = root.InverseTransformPoint(box.max);
            var center = (minimum + maximum) / 2;
            var edge = mask switch
            {
                1 => new Vector3(minimum.x + .5f, center.y, 0),
                2 => new Vector3(maximum.x - .5f, center.y, 0),
                4 => new Vector3(center.x, minimum.y + .5f, 0),
                5 => new Vector3(minimum.x + .5f, minimum.y + .5f, 0),
                _ => new Vector3(maximum.x - .5f, minimum.y + .5f, 0)
            };
            var inner = edge + new Vector3(mask == 4 ? 0 : (mask & 1) != 0 ? 8 : -8, (mask & 4) != 0 ? 8 : 0, 0);
            yield return new HitTarget("retained " + expected.name + " input-band boundary", edge, expected);
            yield return new HitTarget("inner " + expected.name + " band", inner, expected);
        }
        yield return new HitTarget("top border movement surface", new Vector3(160, -1, 0), root.Find("Header")!);
        foreach (var text in new[] { "折叠", "解散" })
        {
            var label = root.Find("Header")!.GetComponentsInChildren<UILabel>(true).First(item => item.text == text);
            var button = label.transform.parent;
            var center = root.InverseTransformPoint(button.GetComponent<Collider>().bounds.center);
            yield return new HitTarget("independent " + text + " button", center, button);
        }
    }

    private static void PanToLocal(MotionState state, Vector3 localPosition)
    {
        var tree = state.Owner.Board.treeRoot; tree.position = state.TreePosition; Physics.SyncTransforms();
        var camera = UICamera.FindCameraForLayer(tree.gameObject.layer)?.GetComponent<Camera>()
            ?? throw new InvalidOperationException("Iteration 3 hit fixture has no native UI camera.");
        var ray = camera.ViewportPointToRay(new Vector3(.55f, .45f, 0));
        var plane = new Plane(tree.forward, tree.position);
        if (!plane.Raycast(ray, out var distance)) throw new InvalidOperationException("Iteration 3 pan ray missed the canvas.");
        var root = GroupRoot(state.Owner, state.Id);
        tree.position += ray.GetPoint(distance) - root.TransformPoint(localPosition); Physics.SyncTransforms();
    }

    private static GameObject? ObserveLocalHit(MotionState state, Vector3 localPosition)
    {
        var root = GroupRoot(state.Owner, state.Id);
        var camera = UICamera.FindCameraForLayer(root.gameObject.layer)?.GetComponent<Camera>()
            ?? throw new InvalidOperationException("Iteration 3 hit fixture has no native UI camera.");
        var previousCamera = UICamera.currentCamera; var previousCurrent = UICamera.current;
        var previousHover = UICamera.hoveredObject; var previousHit = UICamera.lastHit; var previousWorld = UICamera.lastWorldPosition;
        try
        {
            Physics.SyncTransforms();
            var screen = camera.WorldToScreenPoint(root.TransformPoint(localPosition));
            var success = UICamera.Raycast(screen);
            var hit = UICamera.lastHit.collider?.gameObject;
            Plugin.Logger.LogInfo($"Retained iteration 3 input-band raycast: local={localPosition}; screen={screen}; success={success}; hit={hit?.name}");
            return success ? hit : null;
        }
        finally
        {
            UICamera.currentCamera = previousCamera; UICamera.current = previousCurrent;
            UICamera.hoveredObject = previousHover; UICamera.lastHit = previousHit; UICamera.lastWorldPosition = previousWorld;
        }
    }

    private static void CompleteMotion(EditorSession session, MotionState state, Action<bool, string> check)
    {
        RestoreMotion(session, state);
        var actualStory = ProjectFileStore.Serialize(session.Editor.CaptureProject());
        var actualCompiled = AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray();
        var sameNodes = NodesUnchanged(session, state.Nodes); var sameStory = actualStory == state.Story;
        var sameCompiled = actualCompiled.SequenceEqual(state.Compiled);
        Plugin.Logger.LogInfo($"Iteration 3 restoration diagnostic: history={OperationManager.history.Count}/{state.History}; " +
            $"topOperation={(OperationManager.history.Count > 0 ? OperationManager.history.Peek().GetType().Name : "<empty>")}; " +
            $"nodes={sameNodes}; story={sameStory}; compiled={sameCompiled}; compileDelta={OperationManager.compileDelta}/{state.CompileDelta}; " +
            $"treePosition={session.Board.treeRoot.position}; expectedTree={state.TreePosition}");
        foreach (var expected in state.Nodes)
        {
            var current = session.Find(expected.Id);
            if (current == null) { Plugin.Logger.LogInfo("Iteration 3 missing restored node: " + expected.Id); continue; }
            var point = session.Position(current); var local = current.root.localPosition;
            var oldLocal = state.RootPositions[expected.Id];
            Plugin.Logger.LogInfo($"Iteration 3 restored node {expected.Id}: " +
                $"canvas=({point.X:R},{point.Y:R}) expected=({expected.Position.X:R},{expected.Position.Y:R}) " +
                $"delta=({point.X - expected.Position.X:R},{point.Y - expected.Position.Y:R}); " +
                $"local=({local.x:R},{local.y:R},{local.z:R}) originalLocal=({oldLocal.x:R},{oldLocal.y:R},{oldLocal.z:R}); " +
                $"rootParent={current.root.parent?.name}; directCanvasParent={current.root.parent?.Pointer == session.Board.treeRoot.Pointer}");
        }
        if (!sameStory)
        {
            var offset = 0; while (offset < Math.Min(actualStory.Length, state.Story.Length) && actualStory[offset] == state.Story[offset]) offset++;
            Plugin.Logger.LogInfo($"Iteration 3 restored story first textual diff offset={offset}; lengths={state.Story.Length}/{actualStory.Length}; " +
                $"before={Snippet(state.Story, offset)}; after={Snippet(actualStory, offset)}");
            LogJsonDifferences(state.Story, actualStory);
            var report = Environment.GetEnvironmentVariable("NODEGROUPS_TEST_REPORT");
            if (!string.IsNullOrWhiteSpace(report))
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(report))!;
                File.WriteAllText(Path.Combine(directory, "iteration3-restoration-before.json"), state.Story);
                File.WriteAllText(Path.Combine(directory, "iteration3-restoration-after.json"), actualStory);
            }
        }
        check(OperationManager.history.Count == state.History && sameNodes && sameStory && sameCompiled &&
            OperationManager.compileDelta == state.CompileDelta,
            "retained natural-frame editing, motion, resize cursors and input-band hits restore the exact graph, compile bytes and history boundary");
    }

    private static string Snippet(string text, int offset)
    {
        var start = Math.Max(0, offset - 80); var length = Math.Min(240, text.Length - start);
        return text.Substring(start, length).Replace("\r", " ").Replace("\n", " ");
    }

    private static void LogJsonDifferences(string expected, string actual)
    {
        try
        {
            using var before = JsonDocument.Parse(expected[expected.IndexOf('{')..]);
            using var after = JsonDocument.Parse(actual[actual.IndexOf('{')..]);
            var differences = new List<string>(); FindJsonDifferences(before.RootElement, after.RootElement, "$", differences);
            foreach (var difference in differences.Take(40)) Plugin.Logger.LogInfo("Iteration 3 restored story property difference: " + difference);
        }
        catch (Exception error) { Plugin.Logger.LogInfo("Iteration 3 JSON-difference diagnostic could not parse payload: " + error.Message); }
    }

    /// <summary>Diagnostic only: compares every story value without normalizing the strict acceptance assertion.</summary>
    private static void FindJsonDifferences(JsonElement expected, JsonElement actual, string path, List<string> differences)
    {
        if (differences.Count >= 40) return;
        if (expected.ValueKind != actual.ValueKind) { differences.Add(path + " kind=" + expected.ValueKind + "/" + actual.ValueKind); return; }
        if (expected.ValueKind == JsonValueKind.Object)
        {
            var before = expected.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
            var after = actual.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
            foreach (var name in before.Keys.Union(after.Keys).OrderBy(name => name, StringComparer.Ordinal))
            {
                if (!before.TryGetValue(name, out var first) || !after.TryGetValue(name, out var second)) differences.Add(path + "." + name + " missing property");
                else FindJsonDifferences(first, second, path + "." + name, differences);
            }
        }
        else if (expected.ValueKind == JsonValueKind.Array)
        {
            var before = expected.EnumerateArray().ToArray(); var after = actual.EnumerateArray().ToArray();
            if (before.Length != after.Length) differences.Add(path + " length=" + before.Length + "/" + after.Length);
            for (var index = 0; index < Math.Min(before.Length, after.Length); index++) FindJsonDifferences(before[index], after[index], path + "[" + index + "]", differences);
        }
        else if (expected.GetRawText() != actual.GetRawText()) differences.Add(path + " " + expected.GetRawText() + " -> " + actual.GetRawText());
    }

    /// <summary>
    /// Releases a failing cross-frame fixture before its isolated test process
    /// exits. Borrowed history is restored only while the original owner is still
    /// live; a replacement scene's native history is never touched.
    /// </summary>
    internal static void AbortMotion(EditorSession? session)
    {
        var state = motion; motion = null;
        if (state == null) return;
        try
        {
            DisposeLegacy(state);
            if (session != null && !session.IsDisposed && ReferenceEquals(session, state.Owner)) RestoreMotion(session, state);
        }
        catch (Exception error) { Plugin.Logger.LogError("Iteration 3 fixture cleanup failed: " + error); }
    }

    private static void DisposeLegacy(MotionState state)
    {
        if (state.LegacyInput != null) state.LegacyInput.isSelected = false;
        if (state.LegacyRoot != null) { state.LegacyRoot.SetActive(false); UnityEngine.Object.Destroy(state.LegacyRoot); }
        state.LegacyRoot = null; state.LegacyLabel = null; state.LegacyInput = null;
    }

    private static void RestoreMotion(EditorSession session, MotionState state)
    {
        if (state.BorrowedStateRestored) return;
        DisposeLegacy(state);
        if (state.Input != null)
        {
            state.Input.isSelected = false; state.Input.enabled = false; state.Input.gameObject.SetActive(false);
        }
        session.CancelDrag(); session.Cursor.Reset(); session.Board.treeRoot.position = state.TreePosition; Physics.SyncTransforms();
        var guard = Math.Max(0, OperationManager.history.Count - state.History) + 8;
        while (OperationManager.history.Count > state.History && guard-- > 0) OperationManager.Undo();
        Blackboard.selected.Clear();
        foreach (var node in state.PreviousNodes)
            if (node != null && session.Find(EditorSession.Id(node)) != null) Blackboard.selected.Add(node);
        session.SelectedGroup = state.PreviousGroup; UICamera.selectedObject = state.PreviousObject; session.Tick();
        state.BorrowedStateRestored = true;
    }

    private static Vector3[] GlyphVertices(UILabel label) => Enumerable.Range(0, label.geometry.verts.Count)
        .Select(index => label.geometry.verts[index]).ToArray();

    private static string NativeName(UnityEngine.Object? target) => target == null ? "<null>" : target.name;

    /// <summary>Scoped native pointer state; never synthesizes physical system mouse input.</summary>
    private sealed class PointerContext : IDisposable
    {
        private readonly EditorSession session;
        private readonly Camera camera;
        private readonly UICamera.MouseOrTouch? previousTouch;
        private readonly UICamera? previousCamera;
        private readonly Camera? previousCurrentCamera;
        private readonly KeyCode previousKey;
        private readonly int previousId;
        private readonly Vector3 previousWorld;
        private readonly UICamera.MouseOrTouch touch;

        internal PointerContext(EditorSession session, GameObject target, Point2 canvasPosition, bool dragging = true)
            : this(session, target, session.Board.treeRoot.TransformPoint(new Vector3((float)canvasPosition.X, (float)canvasPosition.Y, 0)), dragging) { }

        internal PointerContext(EditorSession session, GameObject target, Vector3 worldPosition, bool dragging = true)
        {
            this.session = session;
            previousTouch = UICamera.currentTouch; previousCamera = UICamera.current;
            previousCurrentCamera = UICamera.currentCamera; previousKey = UICamera.currentKey;
            previousId = UICamera.currentTouchID; previousWorld = UICamera.lastWorldPosition;
            camera = UICamera.FindCameraForLayer(target.layer)?.GetComponent<Camera>() ?? UICamera.mainCamera
                ?? throw new InvalidOperationException("Iteration 3 pointer fixture has no native UI camera.");
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

        internal Vector2 MoveCanvasBy(Point2 delta)
        {
            var current = session.CanvasPointer();
            var world = session.Board.treeRoot.TransformPoint(new Vector3((float)(current.X + delta.X), (float)(current.Y + delta.Y), 0));
            var screen = camera.WorldToScreenPoint(world); var next = new Vector2(screen.x, screen.y);
            var difference = next - touch.pos;
            touch.lastPos = touch.pos; touch.pos = next; touch.delta = difference; touch.totalDelta += difference;
            var ray = camera.ScreenPointToRay(new Vector3(screen.x, screen.y, 0));
            var plane = new Plane(session.Board.treeRoot.forward, session.Board.treeRoot.position);
            if (!plane.Raycast(ray, out var distance)) throw new InvalidOperationException("Iteration 3 pointer missed the canvas plane.");
            UICamera.lastWorldPosition = ray.GetPoint(distance);
            return difference;
        }

        public void Dispose()
        {
            UICamera.currentTouch = previousTouch; UICamera.current = previousCamera;
            UICamera.currentCamera = previousCurrentCamera; UICamera.currentTouchID = previousId;
            UICamera.currentKey = previousKey; UICamera.lastWorldPosition = previousWorld;
        }
    }

}
