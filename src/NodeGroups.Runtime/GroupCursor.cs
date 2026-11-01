using UnityEngine;
using Object = UnityEngine.Object;

namespace AzureArchive.NodeGroups.Runtime;

/// <summary>
/// 管理单个编辑器会话的光标纹理。悬停仅影响最后进入的组表面，
/// 拖拽期间锁定相应光标，直到 EndDrag 或 Reset。
/// 光标可见性和锁定模式仍由 AzureArchive 管理；包括 Dispose 在内均须在 Unity 主线程调用。
/// </summary>
internal sealed class GroupCursor : IDisposable
{
    internal enum Kind
    {
        Default, Horizontal, Vertical,
        /// <summary>左下/右上方向的缩放光标。</summary>
        DiagonalLeft,
        /// <summary>左上/右下方向的缩放光标。</summary>
        DiagonalRight
    }

    private readonly Dictionary<Kind, Texture2D> textures = new();
    private GameObject? hoverOwner, dragOwner;
    private Kind hoverKind;
    private bool hadFocus = Application.isFocused, disposed;
    private const int Size = 32;

    /// <summary>当前已提交给 Unity 光标 API 的样式。</summary>
    internal Kind Current { get; private set; }
    internal bool IsDragging => dragOwner != null;
    /// <summary>当前模组拥有的可读光标纹理；默认系统光标对应 null。</summary>
    internal Texture2D? ActiveTexture => Current == Kind.Default ? null : textures[Current];
    /// <summary>Unity 光标热点坐标从纹理左上角起算。</summary>
    internal Vector2 Hotspot => new(16, 16);

    internal GroupCursor()
    {
        // 32px RGBA32、无 mipmap 且保留 CPU 像素，满足 Unity 硬件光标约定，
        // 同时兼容软件绘制模式。
        foreach (var kind in Enum.GetValues<Kind>().Where(kind => kind != Kind.Default))
            textures.Add(kind, CreateTexture(kind));
    }

    /// <summary>
    /// 处理表面进入/离开。旧表面延迟到达的离开事件不能清除新表面的光标，
    /// 悬停事件也不能覆盖正在拖拽时锁定的光标。
    /// </summary>
    internal void Hover(GameObject owner, Kind kind, bool hovered)
    {
        if (disposed) return;
        if (hovered)
        {
            if (!Alive(owner)) return;
            hoverOwner = owner; hoverKind = kind;
        }
        else
        {
            if (!SameOwner(hoverOwner, owner)) return;
            hoverOwner = null; hoverKind = Kind.Default;
        }
        if (!IsDragging) Apply(hoverOwner == null ? Kind.Default : hoverKind);
    }

    /// <summary>锁定本次手势的光标，即使指针已经离开最初表面也保持到手势结束。</summary>
    internal void BeginDrag(GameObject owner, Kind kind)
    {
        if (disposed || !Alive(owner)) return;
        dragOwner = owner; Apply(kind);
    }

    /// <summary>
    /// 结束手势；只有 NGUI 仍判定悬停在同一表面时才恢复悬停光标。
    /// 拖拽可能推迟悬停事件，不能仅凭保存的 owner 在离开组后恢复缩放光标。
    /// </summary>
    internal void EndDrag()
    {
        if (disposed) return;
        dragOwner = null;
        if (!ActuallyHovered(hoverOwner)) { hoverOwner = null; hoverKind = Kind.Default; }
        Apply(hoverOwner == null ? Kind.Default : hoverKind);
    }

    /// <summary>
    /// 清除所有组持有的光标状态；只有本会话此前使用过自定义光标时才恢复默认光标。
    /// </summary>
    internal void Reset()
    {
        if (disposed) return;
        hoverOwner = null; dragOwner = null; hoverKind = Kind.Default;
        Apply(Kind.Default);
    }

    /// <summary>逐帧清理失效表面，并在应用实际失去焦点时重置光标。</summary>
    internal void Poll() => Poll(Application.isFocused);

    /// <summary>
    /// 使用与 Poll 相同的焦点观测值。显式参数便于测试焦点切换，无需操作应用窗口。
    /// 初始即未聚焦的隐藏测试窗口不会被误判为刚刚失焦。
    /// </summary>
    internal void Poll(bool focused)
    {
        if (disposed) return;
        var lostFocus = hadFocus && !focused; hadFocus = focused;
        if (lostFocus || (dragOwner is not null && !Alive(dragOwner)))
        { Reset(); return; }
        if (hoverOwner is not null && !Alive(hoverOwner))
        {
            hoverOwner = null; hoverKind = Kind.Default;
            if (!IsDragging) Apply(Kind.Default);
        }
    }

    private static bool Alive(GameObject? owner) => owner != null && owner.activeInHierarchy;
    private static bool SameOwner(GameObject? left, GameObject? right) =>
        left is not null && right is not null && left.Pointer == right.Pointer;
    private static bool ActuallyHovered(GameObject? owner)
    {
        if (!Alive(owner)) return false;
        var target = UICamera.hoveredObject;
        if (!Alive(target)) return false;
        // 标题内的按钮和名称输入框独立处理交互；父子关系不代表光标所有权。
        // 只有实际注册悬停的同一表面可以恢复本次手势的提示。
        return SameOwner(owner, target);
    }

    private void Apply(Kind kind)
    {
        if (Current == kind) return;
        // SetCursor(null) 恢复系统默认光标。当前已经默认时不重复提交，
        // 避免覆盖应用中其他界面刚刚设置的光标。
        Cursor.SetCursor(kind == Kind.Default ? null : textures[kind], Hotspot, CursorMode.Auto);
        Current = kind;
    }

    private static Texture2D CreateTexture(Kind kind)
    {
        var segments = Segments(kind).ToArray();
        var pixels = new Color[Size * Size];
        // 黑色 3px 外描边加白色中心，保证在白色组框、灰色画布和蓝色面板上均可辨认。
        foreach (var segment in segments) DrawLine(pixels, segment, 1, Color.black);
        foreach (var segment in segments) DrawLine(pixels, segment, 0, Color.white);
        var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
        {
            name = "NodeGroups cursor " + kind,
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };
        texture.SetPixels(pixels); texture.Apply(false, false);
        return texture;
    }

    private static IEnumerable<(int X1, int Y1, int X2, int Y2)> Segments(Kind kind)
    {
        if (kind == Kind.Horizontal)
        {
            yield return (5, 16, 27, 16);
            yield return (5, 16, 11, 10); yield return (5, 16, 11, 22);
            yield return (27, 16, 21, 10); yield return (27, 16, 21, 22);
        }
        if (kind == Kind.Vertical)
        {
            yield return (16, 5, 16, 27);
            yield return (16, 5, 10, 11); yield return (16, 5, 22, 11);
            yield return (16, 27, 10, 21); yield return (16, 27, 22, 21);
        }
        if (kind == Kind.DiagonalLeft)
        {
            yield return (6, 6, 26, 26);
            yield return (6, 6, 6, 14); yield return (6, 6, 14, 6);
            yield return (26, 26, 18, 26); yield return (26, 26, 26, 18);
        }
        if (kind == Kind.DiagonalRight)
        {
            yield return (6, 26, 26, 6);
            yield return (6, 26, 6, 18); yield return (6, 26, 14, 26);
            yield return (26, 6, 18, 6); yield return (26, 6, 26, 14);
        }
    }

    /// <summary>使用整数 Bresenham 算法绘制线段，避免 32px 光标经抗锯齿后模糊。</summary>
    private static void DrawLine(Color[] pixels, (int X1, int Y1, int X2, int Y2) segment, int radius, Color color)
    {
        var (x, y, endX, endY) = segment;
        var dx = Math.Abs(endX - x); var sx = x < endX ? 1 : -1;
        var dy = -Math.Abs(endY - y); var sy = y < endY ? 1 : -1;
        var error = dx + dy;
        while (true)
        {
            for (var yy = y - radius; yy <= y + radius; yy++)
                for (var xx = x - radius; xx <= x + radius; xx++)
                    if (xx >= 0 && xx < Size && yy >= 0 && yy < Size) pixels[yy * Size + xx] = color;
            if (x == endX && y == endY) break;
            var twice = error * 2;
            if (twice >= dy) { error += dy; x += sx; }
            if (twice <= dx) { error += dx; y += sy; }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        Reset(); disposed = true;
        foreach (var texture in textures.Values) Object.Destroy(texture);
        textures.Clear();
    }
}
