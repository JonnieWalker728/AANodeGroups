namespace NodeGroups.Core;

/// <summary>编辑器 treeRoot 局部画布坐标中的不可变点。</summary>
/// <param name="X">水平坐标。</param>
/// <param name="Y">垂直坐标。</param>
public readonly record struct Point2(double X, double Y)
{
    /// <summary>两个坐标是否均为可安全显示的有限值。</summary>
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);
    /// <summary>对位置施加统一平移。</summary>
    public static Point2 operator +(Point2 left, Point2 right) => new(left.X + right.X, left.Y + right.Y);
    /// <summary>计算两点之间的位移。</summary>
    public static Point2 operator -(Point2 left, Point2 right) => new(left.X - right.X, left.Y - right.Y);
}

/// <summary>treeRoot 局部坐标中的轴对齐命中矩形；Y 轴方向须与适配层约定一致。</summary>
/// <param name="X">最小水平坐标。</param>
/// <param name="Y">最小垂直坐标。</param>
/// <param name="Width">非负宽度。</param>
/// <param name="Height">非负高度。</param>
public readonly record struct Rect2(double X, double Y, double Width, double Height)
{
    /// <summary>矩形坐标及远端边界是否有限，且宽高非负。</summary>
    public bool IsValid => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height) &&
        Width >= 0 && Height >= 0 && double.IsFinite(X + Width) && double.IsFinite(Y + Height);
    /// <summary>检查点是否在闭合矩形内，边界也算命中。</summary>
    public bool Contains(Point2 point) => IsValid && point.IsFinite && point.X >= X && point.X <= X + Width && point.Y >= Y && point.Y <= Y + Height;
    /// <summary>平移矩形，保持尺寸不变。</summary>
    public Rect2 Translate(Point2 delta) => new(X + delta.X, Y + delta.Y, Width, Height);
    /// <summary>创建以画布位置为中心的矩形。</summary>
    public static Rect2 FromCenter(Point2 center, Point2 size) => new(center.X - size.X / 2, center.Y - size.Y / 2, size.X, size.Y);
}
