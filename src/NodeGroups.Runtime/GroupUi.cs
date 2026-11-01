using NodeGroups.Core;
using Studio.Scripts;
using Studio.Scripts.Nodes;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AzureArchive.NodeGroups.Runtime;

/// <summary>
/// 节点组的 NGUI 显示层：标题和缩放边接收组操作，内部区域交给原生节点处理输入。
/// 绘制深度与拖放命中共用 ZIndex 层序；只有 EditorSession.SelectGroup 能提升组层级。
/// </summary>
internal sealed class GroupUi : IDisposable
{
    // 只缓存模组自有视图的显示输入；原生节点/连线仍在每次刷新时读取，避免漏掉外部编辑。
    private readonly record struct ViewState(Rect2 Bounds, int Depth, string Name, int Count,
        bool Collapsed, bool Selected, bool DropTarget, bool Editing);
    private sealed class View
    {
        internal GameObject Root = null!, Incoming = null!, Outgoing = null!;
        internal UISprite Header = null!, Toggle = null!, Dissolve = null!, Frame = null!;
        internal UILabel Title = null!, ToggleLabel = null!, DissolveLabel = null!;
        internal UISprite IncomingSprite = null!, OutgoingSprite = null!;
        internal UILabel? EditTitle;
        internal readonly Dictionary<int, UIWidget> Handles = new();
        internal UIInput? Input;
        internal bool Editing;
        internal ViewState? LastState;
    }
    private readonly EditorSession session;
    private readonly NativeVisualStyle style;
    private readonly GameObject toolbar;
    private readonly UISprite createButton;
    private readonly UILabel createLabel;
    private readonly Dictionary<string, View> views = new();
    private readonly List<NativeProjectedLine> edges = new();
    private IReadOnlyList<GroupState>? rankedGroups;
    private Dictionary<string, int> ranks = new(StringComparer.Ordinal);
    private string edgeSignature = "";
    internal GroupCursor Cursor { get; } = new();
    /// <summary>入口按压或拖动尚未结束时，延后恢复提示，避免中途抢走本次指针手势。</summary>
    internal bool HasActiveCreateGesture => toolbarPressed || toolbarDragging;
    private Point2 toolbarOffset = new(220, -26), toolbarPointer, toolbarStartOffset;
    private Vector2 toolbarPressScreen;
    private bool toolbarPressed, toolbarDragging, suppressCreateClick;
    private const int CreateWidth = 360, CreateHeight = 110;
    private const float ToolbarDragThreshold = 6;
    private const int ResizeBand = 20, CornerSize = 28;
    private static readonly Color SelectedBorder = new(1, .84f, 0, 1);

    internal GroupUi(EditorSession session)
    {
        this.session = session; style = new NativeVisualStyle(session);
        toolbar = Root("NodeGroups toolbar", session.Board.treeRoot.parent);
        createButton = Sprite(toolbar.transform, "创建组", 0, 0, CreateWidth, CreateHeight, style.Body, 5000, true);
        createLabel = Label(createButton.transform, "Label", 24, -18, CreateWidth - 60, CreateHeight - 36,
            "创建组", style.NodeFont, style.NodeFont.fontSize, 5001);
        createLabel.alignment = NGUIText.Alignment.Center;
        var listener = UIEventListener.Get(createButton.gameObject);
        listener.onPress = (Action<GameObject, bool>)((_, pressed) =>
        {
            if (ToolbarInputBlocked) { toolbarPressed = false; toolbarDragging = false; return; }
            if (pressed) BeginToolbarPress();
            else toolbarPressed = false;
        });
        listener.onDragStart = (Action<GameObject>)(_ =>
        {
            if (ToolbarInputBlocked) return;
            if (!toolbarPressed) BeginToolbarPress();
            toolbarDragging = true;
        });
        listener.onDrag = (Action<GameObject, Vector2>)((_, _) =>
        {
            if (!toolbarDragging || ToolbarInputBlocked) return;
            var delta = ToolbarScreenPointer() - toolbarPressScreen;
            if (!suppressCreateClick && delta.sqrMagnitude < ToolbarDragThreshold * ToolbarDragThreshold) return;
            suppressCreateClick = true;
            if (UICamera.currentTouch != null) UICamera.currentTouch.clickNotification = UICamera.ClickNotification.None;
            toolbarOffset = toolbarStartOffset + ToolbarPointer() - toolbarPointer;
            PositionToolbar();
        });
        listener.onDragEnd = (Action<GameObject>)(_ => toolbarDragging = false);
        listener.onClick = (Action<GameObject>)(_ =>
        {
            if (!ToolbarInputBlocked && !suppressCreateClick) Guard(() => session.CreateGroup(false));
        });
    }

    private bool ToolbarInputBlocked => session.IsDisposed || session.ModalInputBlocked;

    /// <summary>每次新按下才清除点击抑制，释放/拖动结束后的迟到点击仍属于旧手势。</summary>
    private void BeginToolbarPress()
    {
        toolbarPressed = true;
        toolbarDragging = false;
        suppressCreateClick = false;
        toolbarPressScreen = ToolbarScreenPointer();
        toolbarPointer = ToolbarPointer();
        toolbarStartOffset = toolbarOffset;
    }

    private static Vector2 ToolbarScreenPointer() => UICamera.currentTouch?.pos ??
        new Vector2(Input.mousePosition.x, Input.mousePosition.y);

    private GameObject Root(string name, Transform parent)
    {
        var go = new GameObject(name) { layer = session.Board.treeRoot.gameObject.layer };
        go.transform.SetParent(parent, false); return go;
    }
    private UISprite Sprite(Transform parent, string name, float x, float y, int width, int height,
        UISprite source, int depth, bool collider = false)
    {
        var sprite = NativeVisualStyle.Sprite(Root(name, parent), source, width, height, depth);
        sprite.transform.localPosition = new Vector3(x, y, 0);
        if (collider) NGUITools.AddWidgetCollider(sprite.gameObject);
        return sprite;
    }
    private UILabel Label(Transform parent, string name, float x, float y, int width, int height,
        string text, UILabel source, int size, int depth)
    {
        var label = NativeVisualStyle.Label(Root(name, parent), source, width, height, depth, text);
        label.fontSize = size; label.transform.localPosition = new Vector3(x, y, 0); return label;
    }
    private UISprite Button(Transform parent, string text, float x, float y, int width, int height, Action click, int depth)
    {
        var box = Sprite(parent, text, x, y, width, height, style.Button, depth, true);
        var label = Label(box.transform, "Label", 8, -8, width - 16, height - 16, text, style.UiFont, 32, depth + 1);
        label.alignment = NGUIText.Alignment.Center;
        UIEventListener.Get(box.gameObject).onClick = (Action<GameObject>)(_ => Guard(click));
        return box;
    }
    private static void Guard(Action action)
    {
        try { action(); }
        catch (Exception error) { Plugin.Logger.LogError(error); }
    }

    private View Create(GroupState group)
    {
        var view = new View { Root = Root("NodeGroup " + group.Id, session.Board.treeRoot) };
        view.Frame = Sprite(view.Root.transform, "Region frame", 0, 0, EditorSession.MinimumFrameWidth,
            EditorSession.MinimumFrameHeight, style.Body, 150);
        view.Frame.fillCenter = false;
        // 复用原生九宫格轮廓作为唯一边框；选中和悬浮只改色，不额外绘制外框。
        view.Header = Sprite(view.Root.transform, "Header", 0, 0, EditorSession.CardWidth, EditorSession.HeaderHeight, style.Body, 150, true);
        view.Title = Label(view.Header.transform, "Name (double click)", 28, -28, 508, 84, group.Name,
            style.NodeFont, style.NodeFont.fontSize, 152);
        NGUITools.AddWidgetCollider(view.Title.gameObject);
        view.Toggle = Button(view.Header.transform, "折叠", 548, -30, 90, 74,
            () => { session.SelectGroup(group.Id); session.Toggle(group.Id); }, 153);
        view.Dissolve = Button(view.Header.transform, "解散", 548, -30, 90, 74,
            () => session.Dissolve(group.Id), 153);
        view.ToggleLabel = view.Toggle.GetComponentInChildren<UILabel>();
        view.DissolveLabel = view.Dissolve.GetComponentInChildren<UILabel>();
        view.Incoming = Port(view.Root.transform, "Incoming port", style.Incoming);
        view.Outgoing = Port(view.Root.transform, "Outgoing port", style.Outgoing);
        view.IncomingSprite = view.Incoming.GetComponentInChildren<UISprite>(true);
        view.OutgoingSprite = view.Outgoing.GetComponentInChildren<UISprite>(true);
        foreach (var surface in new[] { view.Header.gameObject, view.Title.gameObject })
        {
            var listener = UIEventListener.Get(surface);
            listener.onClick = (Action<GameObject>)(_ => Guard(() => session.SelectGroup(group.Id)));
            listener.onDoubleClick = (Action<GameObject>)(_ => Guard(() => BeginEdit(group.Id)));
            listener.onHover = (Action<GameObject, bool>)((target, over) => Cursor.Hover(target, GroupCursor.Kind.Default, over && !view.Editing));
            listener.onDragStart = (Action<GameObject>)(target =>
            { if (!view.Editing) Guard(() => { Cursor.BeginDrag(target, GroupCursor.Kind.Default); session.BeginGroupMove(group.Id); }); });
            listener.onDrag = (Action<GameObject, Vector2>)((_, _) => { if (!view.Editing) Guard(session.PreviewGroupMove); });
            listener.onDragEnd = (Action<GameObject>)(_ =>
            { if (!view.Editing) Guard(() => { try { session.EndGroupMove(); } finally { Cursor.EndDrag(); } }); });
        }
        // 左=1、右=2、下=4；下方两个角组合方向位。上边框和标题仅用于移动。
        foreach (var mask in new[] { 1, 2, 4, 5, 6 })
        {
            // 基础 UIWidget 的 OnFill 不绘制像素，但仍提供尺寸、命中深度和碰撞体。
            // 保持 alpha 不透明，避免 NGUI 把这个不可见输入面当作隐藏控件过滤。
            var handle = Root("Resize " + mask, view.Root.transform).AddComponent<UIWidget>();
            handle.color = Color.white;
            handle.pivot = UIWidget.Pivot.TopLeft; handle.SetDimensions(24, 24); NGUITools.AddWidgetCollider(handle.gameObject);
            view.Handles.Add(mask, handle);
            var listener = UIEventListener.Get(handle.gameObject);
            var cursorKind = mask switch
            { 1 or 2 => GroupCursor.Kind.Horizontal, 4 => GroupCursor.Kind.Vertical, 5 => GroupCursor.Kind.DiagonalLeft, _ => GroupCursor.Kind.DiagonalRight };
            listener.onHover = (Action<GameObject, bool>)((target, over) => Cursor.Hover(target, cursorKind, over));
            listener.onClick = (Action<GameObject>)(_ => Guard(() => session.SelectGroup(group.Id)));
            listener.onDragStart = (Action<GameObject>)(target => Guard(() =>
            { Cursor.BeginDrag(target, cursorKind); session.BeginResize(group.Id, mask); }));
            listener.onDrag = (Action<GameObject, Vector2>)((_, _) => Guard(session.PreviewResize));
            listener.onDragEnd = (Action<GameObject>)(_ => Guard(() =>
            { try { session.EndResize(); } finally { Cursor.EndDrag(); } }));
        }
        return view;
    }

    private GameObject Port(Transform parent, string name, UISprite source)
    {
        var root = Root(name, parent);
        var sprite = NativeVisualStyle.Sprite(Root("Sprite", root.transform), source, source.width, source.height, 154);
        sprite.pivot = source.pivot; sprite.transform.localScale = source.transform.localScale;
        sprite.transform.localPosition = source.transform.localPosition;
        return root;
    }
    /// <summary>只在用户编辑名称时激活原生输入框，一次确认重命名对应一次原生撤销操作。</summary>
    private void BeginEdit(string id)
    {
        session.SelectGroup(id);
        var view = views[id];
        if (view.Editing) return;
        Cursor.Reset();
        // 禁用的 UIInput 仍可能收到 NGUI 的 OnSelect/OnDrag 并重写标签。
        // 因此编辑器使用独立对象，平时保持不激活，避免拖动标题时名称和数量闪烁。
        if (view.Input == null)
        {
            view.EditTitle = Label(view.Header.transform, "Name editor", 28, -28, view.Title.width, 84,
                "", style.NodeFont, style.NodeFont.fontSize, view.Title.depth);
            NGUITools.AddWidgetCollider(view.EditTitle.gameObject);
            view.Input = view.EditTitle.gameObject.AddComponent<UIInput>();
            view.Input.label = view.EditTitle;
        }
        view.Input.characterLimit = 256;
        view.Input.caretColor = Color.black;
        view.Input.submitOnUnselect = false; view.Input.selectAllTextOnFocus = true; view.Input.enabled = true;
        view.Input.value = session.Workspace.Snapshot.GetGroup(id).Name;
        view.Editing = true; view.Title.gameObject.SetActive(false); view.EditTitle!.gameObject.SetActive(true);
        UICamera.selectedObject = view.EditTitle.gameObject; view.Input.isSelected = true;
    }
    /// <summary>回车或失焦确认名称，Escape 放弃编辑；退出后交还普通标题的拖动输入。</summary>
    internal void HandleEdit()
    {
        foreach (var (id, view) in views)
        {
            if (!view.Editing || view.Input == null) continue;
            var escape = Input.GetKeyDown(KeyCode.Escape);
            if (!escape && !Input.GetKeyDown(KeyCode.Return) && !Input.GetKeyDown(KeyCode.KeypadEnter) && view.Input.isSelected) continue;
            view.Editing = false; var name = view.Input.value;
            view.Input.isSelected = false; view.Input.enabled = false;
            view.Input.gameObject.SetActive(false); view.Title.gameObject.SetActive(true);
            if (!escape) Guard(() => session.Rename(id, name));
        }
    }

    /// <summary>用当前快照更新本会话的视图；悬浮反馈只改变外观，不修改分组或历史。</summary>
    internal void Refresh()
    {
        Cursor.Poll();
        PositionToolbar();
        var groups = session.Workspace.Snapshot.Groups;
        // 快照的组集合不可变；几何、成员或层序变化会生成新集合，因此可安全复用层序索引。
        var groupsChanged = !ReferenceEquals(rankedGroups, groups);
        if (groupsChanged)
        {
            ranks = groups.OrderBy(group => group.ZIndex).Select((group, index) => (group.Id, index))
                .ToDictionary(item => item.Id, item => item.index, StringComparer.Ordinal);
            rankedGroups = groups;
        }
        // 侧车允许数百个组，创建入口必须高于全部组，避免被高层组遮挡。
        var groupTopDepth = groups.Count == 0 ? 150 : 150 + (groups.Count - 1) * 20 + 8;
        var toolbarBase = Math.Max(5000, groupTopDepth + 40);
        createButton.depth = toolbarBase;
        createLabel.depth = toolbarBase + 1;
        // 复用本次层序索引判定删除，避免对每个视图重新扫描全部组。
        if (groupsChanged)
            foreach (var id in views.Keys.Where(id => !ranks.ContainsKey(id)).ToArray())
            { views[id].Root.SetActive(false); Object.Destroy(views[id].Root); views.Remove(id); }
        foreach (var group in groups)
        {
            if (!views.TryGetValue(group.Id, out var view)) views.Add(group.Id, view = Create(group));
            var depth = 150 + ranks[group.Id] * 20;
            var bounds = session.Bounds(group);
            var selected = session.SelectedGroup == group.Id;
            var dropTarget = session.DropTarget == group.Id;
            var presentCount = session.Workspace.Snapshot.CountPresentMembers(group.Id);
            var state = new ViewState(bounds, depth, group.Name, presentCount,
                group.Collapsed, selected, dropTarget, view.Editing);
            // 空闲刷新不再反复跨 IL2CPP 边界写尺寸、深度和七处碰撞体；编辑、悬浮、选择均参与失效判断。
            if (view.LastState == state) continue;
            var width = (int)Math.Ceiling(bounds.Width); var height = (int)Math.Ceiling(bounds.Height);
            view.Root.transform.localPosition = new Vector3((float)bounds.X, (float)(bounds.Y + bounds.Height), 0);
            view.Header.depth = depth; view.Header.SetDimensions(width, EditorSession.HeaderHeight); view.Header.ResizeCollider();
            view.Title.depth = depth + 2;
            if (!view.Editing)
            {
                var caption = group.Name + $" ({presentCount})";
                if (view.Title.text != caption) view.Title.text = caption;
            }
            view.Title.width = width - 260; view.Title.ResizeCollider();
            if (view.EditTitle != null)
            { view.EditTitle.depth = depth + 2; view.EditTitle.width = view.Title.width; view.EditTitle.ResizeCollider(); }
            view.Toggle.depth = depth + 3; view.Toggle.transform.localPosition = new Vector3(width - 212, -30, 0);
            view.ToggleLabel.depth = depth + 4; view.ToggleLabel.text = group.Collapsed ? "展开" : "折叠";
            view.Dissolve.depth = depth + 3; view.Dissolve.transform.localPosition = new Vector3(width - 110, -30, 0);
            view.DissolveLabel.depth = depth + 4;
            // 同一轮廓上，待加入的绿色优先于选中黄色。
            // 未选中的折叠卡片也临时显示轮廓；这不会改变选中状态或组层级。
            view.Frame.gameObject.SetActive(!group.Collapsed || selected || dropTarget);
            view.Frame.depth = selected || dropTarget ? depth + 5 : depth;
            view.Frame.SetDimensions(width, height);
            var borderColor = dropTarget ? new Color(.553f, 1, .545f, 1) : selected ? SelectedBorder : Color.white;
            view.Frame.color = borderColor;
            // Common_Bg_Raius5px_Shadow 包含透明阴影，实线轮廓在原图左右约内缩 6.5px、
            // 底部约内缩 15.5px。将轮廓映射到九宫格边带，向外取整以覆盖可见线条。
            // 缩放命中面位于边框内、标题下方，只接收输入，不再绘制第二圈边框。
            var drawing = view.Frame.drawingDimensions;
            var left = drawing.x + (int)(view.Frame.border.x * .3f);
            var right = drawing.z - (int)(view.Frame.border.z * .3f);
            var bottom = drawing.y + (int)(view.Frame.border.y * .5f);
            var innerWidth = (int)Math.Floor(right - left);
            var sideHeight = (int)Math.Floor(-EditorSession.HeaderHeight - bottom - CornerSize);
            foreach (var (mask, handle) in view.Handles)
            {
                handle.gameObject.SetActive(!group.Collapsed);
                var corner = mask is 5 or 6; handle.depth = depth + (corner ? 8 : 7);
                handle.color = Color.white;
                handle.SetDimensions(corner ? CornerSize : mask == 4 ? Math.Max(ResizeBand, innerWidth - 2 * CornerSize) : ResizeBand,
                    corner ? CornerSize : mask == 4 ? ResizeBand : Math.Max(ResizeBand, sideHeight));
                var x = mask == 4 ? left + CornerSize : (mask & 2) != 0 ? right - (corner ? CornerSize : ResizeBand) : left;
                var y = (mask & 4) != 0 ? bottom + (corner ? CornerSize : ResizeBand) : -EditorSession.HeaderHeight;
                handle.transform.localPosition = new Vector3(x, y, 0); handle.ResizeCollider();
            }
            view.Incoming.SetActive(group.Collapsed); view.Outgoing.SetActive(group.Collapsed);
            view.Incoming.transform.localPosition = new Vector3(width / 2f, 0, 0);
            view.Outgoing.transform.localPosition = new Vector3(width / 2f, -EditorSession.HeaderHeight, 0);
            view.IncomingSprite.depth = depth + 4;
            view.OutgoingSprite.depth = depth + 4;
            view.LastState = state;
        }
        // 删除或撤销可能在本次刷新中禁用光标所属对象，应立即恢复光标。
        Cursor.Poll();
        ProjectEdges();
    }

    /// <summary>以视口角点加用户拖动偏移定位入口，画布平移和缩放不会带动按钮。</summary>
    private void PositionToolbar()
    {
        var camera = UICamera.FindCameraForLayer(toolbar.layer)?.GetComponent<Camera>();
        if (camera == null) return;
        var parent = toolbar.transform.parent;
        var ray = camera.ViewportPointToRay(new Vector3(0, 1, 0));
        var plane = new Plane(parent.forward, parent.position);
        if (!plane.Raycast(ray, out var distance)) return;
        var corner = parent.InverseTransformPoint(ray.GetPoint(distance));
        toolbar.transform.localPosition = corner + new Vector3((float)toolbarOffset.X, (float)toolbarOffset.Y, 0);
    }
    private Point2 ToolbarPointer()
    {
        var camera = UICamera.currentCamera ?? UICamera.FindCameraForLayer(toolbar.layer)?.GetComponent<Camera>();
        if (camera == null) return toolbarPointer;
        var screen = UICamera.currentTouch?.pos ?? new Vector2(Input.mousePosition.x, Input.mousePosition.y);
        var ray = camera.ScreenPointToRay(new Vector3(screen.x, screen.y, 0));
        var parent = toolbar.transform.parent; var plane = new Plane(parent.forward, parent.position);
        if (!plane.Raycast(ray, out var distance)) return toolbarPointer;
        var point = parent.InverseTransformPoint(ray.GetPoint(distance)); return new(point.x, point.y);
    }

    /// <summary>仅在端点或投影状态变化时重建只读原生连线，保留原生线宽、曲线和材质。</summary>
    private void ProjectEdges()
    {
        var snapshot = session.Workspace.Snapshot;
        // 全展开时由原生连线负责绘制，无须捕获所有选项或生成投影指纹。
        // 离开折叠状态仍立即销毁旧代理；再次折叠会重新读取原生图而非复用过时数据。
        if (!snapshot.Groups.Any(group => group.Collapsed))
        {
            foreach (var edge in edges) edge.Dispose();
            edges.Clear(); edgeSignature = "";
            return;
        }
        var groupBounds = snapshot.Groups.ToDictionary(group => group.Id, session.Bounds);
        var presentation = CaptureEdgePresentation(snapshot, groupBounds).ToArray();
        var signature = ProjectionCacheKey.Create(snapshot, groupBounds, presentation);
        if (edgeSignature == signature) return;
        foreach (var edge in edges) edge.Dispose(); edges.Clear();
        var projected = EdgeProjector.Project(snapshot, presentation.Select(edge =>
            new EdgeState(edge.Id, edge.FromNodeId, edge.ToNodeId, edge.OptionId, edge.Text)));
        for (var index = 0; index < presentation.Length; index++)
        {
            if (projected[index].Visibility != EdgeVisibility.Projected) continue;
            var edge = presentation[index];
            // 连线绘制在节点卡片下方，使用原生几何与样式。
            edges.Add(new NativeProjectedLine(session, edge.Id, edge.Start, edge.End, 21));
        }
        edgeSignature = signature; // 重建成功后才更新签名；失败可在下次刷新重试。
    }
    internal bool IsProjected(Line line) => edges.Any(edge => edge.Line.Pointer == line.Pointer);

    /// <summary>读取插槽和选项身份以投影连线；不修改连接，也不显示预制体的占位文字。</summary>
    private IEnumerable<ProjectionEdgePresentation> CaptureEdgePresentation(WorkspaceSnapshot snapshot, IReadOnlyDictionary<string, Rect2> bounds)
    {
        Point2 End(Node node, string id, bool output)
        {
            var group = snapshot.FindGroupForNode(id);
            if (group?.Collapsed == true)
            {
                var rect = bounds[group.Id]; return new(rect.X + rect.Width / 2, output ? rect.Y : rect.Y + rect.Height);
            }
            var slot = node.GetSlot(!output);
            if (slot == null) return session.Position(node);
            var local = session.Board.treeRoot.InverseTransformPoint(slot.position); return new(local.x, local.y);
        }
        foreach (var from in session.Board.nodes.ToArray())
        {
            var fromId = EditorSession.Id(from); var selection = from.TryCast<SelectionNode>();
            foreach (var connection in from.connectionsTo.ToArray())
            {
                var to = connection.target; var line = connection.line;
                if (to == null || line == null) continue;
                var toId = EditorSession.Id(to);
                var identity = line.Pointer.ToInt64().ToString("X", System.Globalization.CultureInfo.InvariantCulture);
                var start = End(from, fromId, true); var end = End(to, toId, false);
                var options = selection?.Options?.ToArray().Where(option =>
                    string.Equals(ChoiceInterop.TargetId(option), toId, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (options == null || options.Length == 0)
                    yield return new(identity, fromId, toId, start, end, null, null, false, connection.count, null);
                else foreach (var option in options)
                    yield return new(identity + "/" + option.OptionId, fromId, toId, start, end, option.Text, option.OptionId,
                        string.Equals(selection!.DefaultOptionId, option.OptionId, StringComparison.Ordinal), connection.count, null);
            }
        }
    }
    public void Dispose()
    {
        Cursor.Dispose();
        toolbar.SetActive(false); Object.Destroy(toolbar);
        foreach (var view in views.Values) { view.Root.SetActive(false); Object.Destroy(view.Root); }
        foreach (var edge in edges) edge.Dispose();
        style.Dispose();
    }
}
