using AzureArchive.Automation;
using Il2CppInterop.Runtime;
using NodeGroups.Core;
using Studio.Scripts;
using Studio.Scripts.Nodes;
using Studio.Scripts.OperationManagement;
using UnityEngine;

namespace AzureArchive.NodeGroups.Runtime.Tests;

/// <summary>
/// Single native-border geometry, five resize targets and default movement-pointer checks.
/// These checks borrow native selection and history, then restore both before
/// returning. They never create a node, alter connections or simulate OS input.
/// </summary>
internal static class BorderHitTargetTests
{
    private sealed record HitTarget(string Name, Vector3 Position, Transform? Expected);

    /// <summary>Borrowed canvas, selection and graph state for natural-frame raycasts.</summary>
    private sealed class VisualState
    {
        internal EditorSession Owner = null!;
        internal int History, CompileDelta, Frames, Step;
        internal string Id = "", Story = "";
        internal byte[] Compiled = Array.Empty<byte>();
        internal Node[] PreviousNodes = Array.Empty<Node>();
        internal string? PreviousGroup;
        internal GameObject? PreviousObject;
        internal Vector3 TreePosition;
        internal HitTarget[]? Targets;
        internal bool Restored;
    }
    private static VisualState? visual;

    /// <summary>
    /// Verifies the single native contour and ordinary movement pointer while
    /// retaining all five resize surfaces and their four directional cursors.
    /// </summary>
    /// <param name="session">Owner of the temporary runtime fixture.</param>
    /// <param name="check">Assertion sink which stops at the first failed contract.</param>
    internal static void Run(EditorSession session, Action<bool, string> check)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NODEGROUPS_TEST_REPORT")))
            throw new InvalidOperationException("Iteration 4 checks require the explicitly isolated runtime fixture.");
        var boundary = OperationManager.history.Count;
        var previousNodes = Blackboard.selected.ToArray();
        var previousGroup = session.SelectedGroup;
        var previousObject = UICamera.selectedObject;
        var story = ProjectFileStore.Serialize(session.Editor.CaptureProject());
        var compiled = AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray();
        var compileDelta = OperationManager.compileDelta;
        try
        {
            var source = session.Board.nodes.ToArray().Select(node => node.TryCast<ScriptNode>()).First(node => node != null)!;
            Blackboard.selected.Clear(); Blackboard.selected.Add(source);
            session.CreateGroup(false); session.Tick();
            var id = session.SelectedGroup ?? throw new InvalidOperationException("Created group has no selection.");
            var root = GroupRoot(session, id);
            var frame = root.Find("Region frame")!.GetComponent<UISprite>();
            LogFrameGeometry(frame);
            check(!root.GetComponentsInChildren<Transform>(true).Any(transform =>
                transform.name.StartsWith("Border ", StringComparison.Ordinal)),
                "iteration 4 group has no additional outer border-line overlays");
            check(root.Find("Native selected effect") == null,
                "iteration 4 group selection creates no second expanded selection-effect contour");
            check(frame.enabled && frame.gameObject.activeInHierarchy && !frame.fillCenter && IsYellow(frame.color) &&
                frame.atlas.Pointer == source.sprite.atlas.Pointer && frame.spriteName == source.sprite.spriteName &&
                frame.type == source.sprite.type && root.GetComponentsInChildren<UISprite>(true).Count(sprite =>
                    sprite.enabled && sprite.gameObject.activeInHierarchy && !sprite.fillCenter) == 1,
                "iteration 4 the sole selected region contour reuses the original native node sprite and remains yellow");
            check(frame.GetComponent<Collider>() == null,
                "iteration 4 the single region frame leaves its interior free of a whole-area input collider");
            CheckResizeSurfaces(root, session, check);
            CheckDefaultMovementPointer(root, session, check);
            session.Toggle(id); session.Tick();
            check(frame.gameObject.activeInHierarchy && IsYellow(frame.color) && frame.height == EditorSession.HeaderHeight,
                "iteration 4 collapsed selection retains only the single native yellow header contour");
            session.DeselectGroup(); session.Tick();
            check(!root.GetComponentsInChildren<UISprite>(true).Any(sprite =>
                sprite.enabled && sprite.gameObject.activeInHierarchy && !sprite.fillCenter && IsYellow(sprite.color)),
                "iteration 4 deselection removes the yellow contour without retaining a second outline");
        }
        finally
        {
            session.CancelDrag(); session.Cursor.Reset();
            var guard = Math.Max(0, OperationManager.history.Count - boundary) + 8;
            while (OperationManager.history.Count > boundary && guard-- > 0) OperationManager.Undo();
            Blackboard.selected.Clear();
            foreach (var node in previousNodes)
                if (node != null && session.Find(EditorSession.Id(node)) != null) Blackboard.selected.Add(node);
            session.SelectedGroup = previousGroup; UICamera.selectedObject = previousObject; session.Tick();
        }
        check(OperationManager.history.Count == boundary && OperationManager.compileDelta == compileDelta &&
            ProjectFileStore.Serialize(session.Editor.CaptureProject()) == story &&
            AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray().SequenceEqual(compiled),
            "iteration 4 synchronous appearance and cursor checks restore the exact native graph, compile bytes and history boundary");
    }

    private static void CheckResizeSurfaces(Transform root, EditorSession session, Action<bool, string> check)
    {
        var handles = root.GetComponentsInChildren<Transform>(true)
            .Where(transform => transform.name.StartsWith("Resize ", StringComparison.Ordinal) && transform.GetComponent<Collider>() != null)
            .OrderBy(transform => transform.name, StringComparer.Ordinal).ToArray();
        check(handles.Select(transform => transform.name).SequenceEqual(new[] { "Resize 1", "Resize 2", "Resize 4", "Resize 5", "Resize 6" }),
            "iteration 4 removing triangle artwork preserves exactly the five approved resize input surfaces");
        foreach (var handle in handles)
        {
            var widget = handle.GetComponent<UIWidget>();
            check(widget != null && widget.enabled && widget.gameObject.activeInHierarchy && widget.color.a > .01f &&
                handle.GetComponent<UITexture>() == null && handle.GetComponent<UISprite>() == null,
                "iteration 4 " + handle.name + " retains a native hit-test widget without drawing triangle or rectangle artwork");
            var kind = handle.name switch
            {
                "Resize 1" or "Resize 2" => GroupCursor.Kind.Horizontal,
                "Resize 4" => GroupCursor.Kind.Vertical,
                "Resize 5" => GroupCursor.Kind.DiagonalLeft,
                _ => GroupCursor.Kind.DiagonalRight
            };
            var listener = UIEventListener.Get(handle.gameObject);
            listener.OnHover(true);
            check(session.Cursor.Current == kind && session.Cursor.ActiveTexture != null,
                "iteration 4 hovering " + handle.name + " still supplies its directional resize cursor");
            listener.OnHover(false);
        }
        check(session.Cursor.Current == GroupCursor.Kind.Default && session.Cursor.ActiveTexture == null,
            "iteration 4 leaving a resize surface restores the ordinary pointer");
    }

    private static void CheckDefaultMovementPointer(Transform root, EditorSession session, Action<bool, string> check)
    {
        foreach (var name in new[] { "Header", "Header/Name (double click)" })
        {
            var surface = root.Find(name)!;
            var resize = UIEventListener.Get(root.Find("Resize 1")!.gameObject);
            resize.OnHover(true);
            var listener = UIEventListener.Get(surface.gameObject); listener.OnHover(true);
            check(session.Cursor.Current == GroupCursor.Kind.Default && session.Cursor.ActiveTexture == null,
                "iteration 4 " + name + " hover clears a previous resize cursor and uses the ordinary pointer");
            listener.OnDragStart();
            check(session.Cursor.IsDragging && session.Cursor.Current == GroupCursor.Kind.Default && session.Cursor.ActiveTexture == null,
                "iteration 4 " + name + " movement retains the ordinary pointer throughout its owned gesture");
            listener.OnHover(false); resize.OnHover(false); session.CancelDrag();
            check(!session.Cursor.IsDragging && session.Cursor.Current == GroupCursor.Kind.Default,
                "iteration 4 cancelling " + name + " movement leaves the ordinary pointer active");
        }
    }

    /// <summary>
    /// Creates one empty metadata-only group away from fixture nodes. The caller
    /// must subsequently call TickVisual on real Unity frames and AbortVisual
    /// when its surrounding fixture fails or changes editor owner.
    /// </summary>
    internal static void BeginVisual(EditorSession session)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NODEGROUPS_TEST_REPORT")) || visual != null)
            throw new InvalidOperationException("Iteration 4 natural-frame checks require one isolated test process.");
        var state = new VisualState
        {
            Owner = session, History = OperationManager.history.Count, CompileDelta = OperationManager.compileDelta,
            Story = ProjectFileStore.Serialize(session.Editor.CaptureProject()),
            Compiled = AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray(),
            PreviousNodes = Blackboard.selected.ToArray(), PreviousGroup = session.SelectedGroup,
            PreviousObject = UICamera.selectedObject, TreePosition = session.Board.treeRoot.position,
            Id = Guid.NewGuid().ToString("D")
        };
        visual = state;
        var region = new Rect2(-6000, -6600, 1080, 760);
        session.Commit(session.Workspace.CreateGroup(state.Id, "单层边框验收", Array.Empty<string>(),
            new Point2(region.X, region.Y + region.Height), expandedBounds: region));
        session.SelectGroup(state.Id); session.Tick();
        // NGUI legitimately omits clipped widgets' geometry. Bring this remote
        // empty fixture into view before observing its natural render lifecycle.
        PanToLocal(state, new Vector3((float)region.Width / 2, -(float)region.Height / 2, 0));
    }

    /// <summary>
    /// Inspects a private GPU copy of the native sprite's pixels to determine
    /// its actual opaque white border, then raycasts that visible boundary and
    /// its inner input band through the real NGUI camera. Atlas padding and
    /// sliced border lengths alone do not prove where the line is visible.
    /// </summary>
    /// <returns>True only after all temporary commands and borrowed state are restored.</returns>
    internal static bool TickVisual(EditorSession session, Action<bool, string> check, Action<string>? capturePreview = null)
    {
        var state = visual ?? throw new InvalidOperationException("Iteration 4 visual fixture was not started.");
        if (!ReferenceEquals(state.Owner, session) || session.IsDisposed)
            throw new InvalidOperationException("Iteration 4 natural-frame fixture changed editor owner.");
        if (++state.Frames < 3) return false;
        if (state.Targets == null)
        {
            var root = GroupRoot(session, state.Id);
            var frame = root.Find("Region frame")!.GetComponent<UISprite>();
            LogFrameGeometry(frame);
            check(frame.geometry.hasVertices && frame.geometry.verts.Count > 0 && IsYellow(frame.color),
                "iteration 4 the sole native frame submits yellow sliced-border geometry over natural Unity frames");
            check(root.GetComponentsInChildren<Transform>(true).Where(transform =>
                transform.name.StartsWith("Resize ", StringComparison.Ordinal)).All(transform =>
                transform.GetComponent<UIWidget>() is { } widget && !widget.geometry.hasVertices),
                "iteration 4 all five resize surfaces submit no triangle or rectangle drawing geometry");
            state.Targets = NativeVisibleTargets(root, frame, check).ToArray();
            state.Step = 0; state.Frames = 0; PanToLocal(state, state.Targets[0].Position);
            return false;
        }
        var target = state.Targets[state.Step];
        var hit = ObserveLocalHit(state, target.Position);
        if (target.Expected != null)
            check(hit != null && (hit.Pointer == target.Expected.gameObject.Pointer || hit.transform.IsChildOf(target.Expected)),
                "iteration 4 actual NGUI raycast hits " + target.Name + " on " + target.Expected.name);
        else
            check(hit == null || !hit.transform.IsChildOf(GroupRoot(session, state.Id)),
                "iteration 4 actual NGUI raycast leaves the open frame interior available to original canvas and node input");
        if (state.Step == 0) capturePreview?.Invoke("single-border");
        state.Step++; state.Frames = 0;
        if (state.Step < state.Targets.Length)
        {
            PanToLocal(state, state.Targets[state.Step].Position); return false;
        }
        RestoreVisual(session, state);
        check(OperationManager.history.Count == state.History && OperationManager.compileDelta == state.CompileDelta &&
            ProjectFileStore.Serialize(session.Editor.CaptureProject()) == state.Story &&
            AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray().SequenceEqual(state.Compiled),
            "iteration 4 natural-frame native border raycasts restore the exact original graph, compile bytes and history boundary");
        visual = null; return true;
    }

    private static IEnumerable<HitTarget> NativeVisibleTargets(Transform root, UISprite frame, Action<bool, string> check)
    {
        var report = Path.GetFullPath(Environment.GetEnvironmentVariable("NODEGROUPS_TEST_REPORT")!);
        var data = frame.GetType().GetMethod("GetAtlasSprite", Type.EmptyTypes)?.Invoke(frame, null)
            ?? throw new InvalidOperationException("The native sliced frame has no atlas record.");
        int Field(string name) => Convert.ToInt32(data.GetType().GetProperty(name)?.GetValue(data)
            ?? throw new InvalidOperationException("Native atlas record has no " + name + "."));
        var atlasX = Field("x"); var atlasY = Field("y"); var width = Field("width"); var height = Field("height");
        var borderLeft = Field("borderLeft"); var borderRight = Field("borderRight");
        var borderBottom = Field("borderBottom"); var borderTop = Field("borderTop");
        check(new[] { "paddingLeft", "paddingRight", "paddingBottom", "paddingTop" }.All(name => Field(name) == 0),
            "iteration 4 installed native frame has measured zero atlas padding before visible-line mapping");
        var atlas = frame.mainTexture;
        var target = new RenderTexture(atlas.width, atlas.height, 0);
        var copy = new Texture2D(atlas.width, atlas.height, TextureFormat.RGBA32, false);
        var crop = new Texture2D(width, height, TextureFormat.RGBA32, false);
        var previous = RenderTexture.active;
        Color[] pixels;
        try
        {
            target.Create(); Graphics.Blit(atlas, target); RenderTexture.active = target;
            copy.ReadPixels(new Rect(0, 0, atlas.width, atlas.height), 0, 0); copy.Apply();
            pixels = new Color[width * height];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var color = copy.GetPixel(atlasX + x, atlas.height - atlasY - height + y);
                pixels[y * width + x] = color; crop.SetPixel(x, y, color);
            }
            crop.Apply(); File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(report)!, "iteration4-native-frame-sprite.png"),
                ImageConversion.EncodeToPNG(crop).ToArray());
        }
        finally
        {
            RenderTexture.active = previous; target.Release();
            UnityEngine.Object.Destroy(target); UnityEngine.Object.Destroy(copy); UnityEngine.Object.Destroy(crop);
        }
        bool Solid(int x, int y)
        {
            var color = pixels[y * width + x];
            return color.a > .95f && color.r > .9f && color.g > .9f && color.b > .9f;
        }
        var centerX = borderLeft + (width - borderLeft - borderRight) / 2;
        var centerY = borderBottom + (height - borderBottom - borderTop) / 2;
        var row = Enumerable.Range(0, width).Where(x => Solid(x, centerY)).ToArray();
        var column = Enumerable.Range(0, height).Where(y => Solid(centerX, y)).ToArray();
        check(row.Length > 0 && column.Length > 0,
            "iteration 4 GPU-read native sprite identifies opaque white border pixels independently of its shadow");
        var leftPixel = row.First() + .5f; var rightPixel = row.Last() + .5f;
        var bottomPixel = column.First() + .5f; var topPixel = column.Last() + .5f;
        // Rounded corners can begin farther inward than straight edges. Sample
        // their own actual opaque bottom row rather than intersecting two lines
        // at a potentially transparent corner pixel.
        var bottomRow = Enumerable.Range(0, width).Where(x => Solid(x, column.First())).ToArray();
        var bottomLeftPixel = bottomRow.First() + .5f; var bottomRightPixel = bottomRow.Last() + .5f;
        var drawing = frame.drawingDimensions;
        var scaleX = frame.border.x / borderLeft; var scaleY = frame.border.y / borderBottom;
        float Map(float value, int sourceSize, int nearBorder, int farBorder, float minimum, float maximum, float scale)
        {
            if (value <= nearBorder) return minimum + value * scale;
            if (value >= sourceSize - farBorder) return maximum - (sourceSize - value) * scale;
            return minimum + nearBorder * scale + (value - nearBorder) / (sourceSize - nearBorder - farBorder) *
                (maximum - minimum - (nearBorder + farBorder) * scale);
        }
        float X(float pixel) => Map(pixel, width, borderLeft, borderRight, drawing.x, drawing.z, scaleX);
        float Y(float pixel) => Map(pixel, height, borderBottom, borderTop, drawing.y, drawing.w, scaleY);
        var left = X(leftPixel); var right = X(rightPixel); var bottom = Y(bottomPixel); var top = Y(topPixel);
        var midX = (drawing.x + drawing.z) / 2; var midY = (drawing.y + drawing.w) / 2;
        var metrics = $"atlas={atlasX},{atlasY},{width},{height}; source white L/R/B/T=" +
            $"{leftPixel},{rightPixel},{bottomPixel},{topPixel}; corner x={bottomLeftPixel},{bottomRightPixel}; " +
            $"drawing={drawing}; border={frame.border}; local opaque L/R/B/T={left},{right},{bottom},{top}";
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(report)!, "iteration4-native-frame-metrics.txt"), metrics);
        Plugin.Logger.LogInfo("Iteration 4 native visible-line metrics: " + metrics);
        foreach (var (mask, edge, inner) in new[]
        {
            (1, new Vector3(left, midY, 0), new Vector3(left + 8, midY, 0)),
            (2, new Vector3(right, midY, 0), new Vector3(right - 8, midY, 0)),
            (4, new Vector3(midX, bottom, 0), new Vector3(midX, bottom + 8, 0)),
            (5, new Vector3(X(bottomLeftPixel), bottom, 0), new Vector3(X(bottomLeftPixel) + 8, bottom + 8, 0)),
            (6, new Vector3(X(bottomRightPixel), bottom, 0), new Vector3(X(bottomRightPixel) - 8, bottom + 8, 0))
        })
        {
            var expected = root.Find("Resize " + mask)!;
            yield return new HitTarget("native visible " + expected.name + " line", edge, expected);
            yield return new HitTarget("native inner " + expected.name + " band", inner, expected);
        }
        yield return new HitTarget("native top line movement surface", new Vector3(160, top, 0), root.Find("Header")!);
        Physics.SyncTransforms();
        foreach (var name in new[] { "折叠", "解散" })
        {
            var button = root.Find("Header/" + name)!;
            yield return new HitTarget("independent " + name + " button", root.InverseTransformPoint(button.GetComponent<Collider>().bounds.center), button);
        }
        // A frame's open middle must never be swallowed by an invisible resize
        // widget; the fixture has no nodes here, so a frame descendant is wrong.
        yield return new HitTarget("open frame interior", new Vector3(midX, midY, 0), null);
    }

    private static void PanToLocal(VisualState state, Vector3 localPosition)
    {
        var tree = state.Owner.Board.treeRoot; tree.position = state.TreePosition; Physics.SyncTransforms();
        var camera = UICamera.FindCameraForLayer(tree.gameObject.layer)?.GetComponent<Camera>()
            ?? throw new InvalidOperationException("Iteration 4 hit fixture has no native UI camera.");
        var ray = camera.ViewportPointToRay(new Vector3(.55f, .45f, 0));
        var plane = new Plane(tree.forward, tree.position);
        if (!plane.Raycast(ray, out var distance)) throw new InvalidOperationException("Iteration 4 pan ray missed the canvas.");
        tree.position += ray.GetPoint(distance) - GroupRoot(state.Owner, state.Id).TransformPoint(localPosition);
        Physics.SyncTransforms();
    }

    private static GameObject? ObserveLocalHit(VisualState state, Vector3 localPosition)
    {
        var root = GroupRoot(state.Owner, state.Id);
        var camera = UICamera.FindCameraForLayer(root.gameObject.layer)?.GetComponent<Camera>()
            ?? throw new InvalidOperationException("Iteration 4 hit fixture has no native UI camera.");
        var previousCamera = UICamera.currentCamera; var previousCurrent = UICamera.current;
        var previousHover = UICamera.hoveredObject; var previousHit = UICamera.lastHit; var previousWorld = UICamera.lastWorldPosition;
        try
        {
            Physics.SyncTransforms();
            var screen = camera.WorldToScreenPoint(root.TransformPoint(localPosition));
            var success = UICamera.Raycast(screen); var hit = UICamera.lastHit.collider?.gameObject;
            Plugin.Logger.LogInfo($"Iteration 4 native visible-line raycast: local={localPosition}; screen={screen}; success={success}; hit={hit?.name}");
            return success ? hit : null;
        }
        finally
        {
            UICamera.currentCamera = previousCamera; UICamera.current = previousCurrent;
            UICamera.hoveredObject = previousHover; UICamera.lastHit = previousHit; UICamera.lastWorldPosition = previousWorld;
        }
    }

    /// <summary>Restore only this fixture's still-alive owner after an interrupted run.</summary>
    internal static void AbortVisual(EditorSession? session)
    {
        var state = visual; if (state == null) return;
        try
        {
            if (session != null && !session.IsDisposed && ReferenceEquals(session, state.Owner)) RestoreVisual(session, state);
        }
        finally { visual = null; }
    }

    private static void RestoreVisual(EditorSession session, VisualState state)
    {
        if (state.Restored) return;
        session.CancelDrag(); session.Cursor.Reset(); session.Board.treeRoot.position = state.TreePosition; Physics.SyncTransforms();
        var guard = Math.Max(0, OperationManager.history.Count - state.History) + 8;
        while (OperationManager.history.Count > state.History && guard-- > 0) OperationManager.Undo();
        Blackboard.selected.Clear();
        foreach (var node in state.PreviousNodes)
            if (node != null && session.Find(EditorSession.Id(node)) != null) Blackboard.selected.Add(node);
        session.SelectedGroup = state.PreviousGroup; UICamera.selectedObject = state.PreviousObject; session.Tick();
        state.Restored = true;
    }

    /// <summary>
    /// Read-only geometry evidence. Atlas border values are not assumed to be
    /// transparent padding; the atlas record and submitted vertices are logged
    /// separately before a failing RED assertion can interrupt the fixture.
    /// </summary>
    private static void LogFrameGeometry(UISprite frame)
    {
        var data = frame.GetType().GetMethod("GetAtlasSprite", Type.EmptyTypes)?.Invoke(frame, null);
        var fields = new[] { "name", "x", "y", "width", "height", "borderLeft", "borderRight", "borderTop", "borderBottom",
            "paddingLeft", "paddingRight", "paddingTop", "paddingBottom" };
        var record = data == null ? "<unavailable>" : string.Join(";", fields.Select(name =>
            name + "=" + data.GetType().GetProperty(name)?.GetValue(data)));
        var geometry = Enumerable.Range(0, frame.geometry.verts.Count).Select(index => frame.geometry.verts[index].ToString());
        Plugin.Logger.LogInfo($"Iteration 4 native frame geometry: drawing={frame.drawingDimensions}; border={frame.border}; " +
            $"size={frame.width}x{frame.height}; atlas=[{record}]; vertices=[{string.Join(";", geometry)}]");
    }

    private static Transform GroupRoot(EditorSession session, string id) => session.Board.treeRoot.Find("NodeGroup " + id)
        ?? throw new InvalidOperationException("Group view is missing: " + id);
    private static bool IsYellow(Color color) => color.r > .9f && color.g > .8f && color.b < .2f && color.a > .95f;
}
