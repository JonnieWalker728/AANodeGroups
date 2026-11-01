namespace NodeGroups.Core;

/// <summary>为新节点或多节点拖放计算统一画布位移，保持节点间的相对布局。</summary>
public static class NodePlacement
{
    /// <summary>将传入节点整体放在既有成员下方；空组中将主节点放到回退锚点。</summary>
    /// <param name="existing">目标组现有成员几何；忽略传入节点自身的标识，避免自身碰撞。</param>
    /// <param name="incoming">需要整体放置的已有节点；坐标和尺寸必须采用同一画布坐标约定。</param>
    /// <param name="fallbackAnchor">目标组没有其他成员时，期望的主节点中心位置。</param>
    /// <param name="primaryId">传入节点中作为空组锚点基准的节点标识。</param>
    /// <param name="gap">两个整体占用区域之间的间距，单位为画布坐标，必须有限且为正。</param>
    /// <returns>对全部传入节点统一施加的有限位移。</returns>
    /// <remarks>
    /// 下方表示更小的画布 Y 值，与 AzureArchive 向上为正的 treeRoot 坐标一致。
    /// 算法保留传入选择集内部的既有重叠，不修改节点、选择归属、调整组尺寸，
    /// 也不分别吸附成员或写入历史。
    /// </remarks>
    public static Point2 FindTranslationToFreeSpace(IEnumerable<NodeState> existing, IEnumerable<NodeState> incoming,
        Point2 fallbackAnchor, string primaryId, double gap = 24)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(incoming);
        Guard.Point(fallbackAnchor, nameof(fallbackAnchor));
        Guard.Id(primaryId, nameof(primaryId));
        if (!double.IsFinite(gap) || gap <= 0) throw new ArgumentOutOfRangeException(nameof(gap), "Separation must be finite and positive.");
        var additions = incoming.ToArray();
        var members = existing.ToArray();
        if (additions.Length == 0) throw new ArgumentException("At least one incoming node is required.", nameof(incoming));
        ValidateNodes(additions, nameof(incoming));
        ValidateNodes(members, nameof(existing));
        var primary = additions.FirstOrDefault(node => node.Id == primaryId) ??
            throw new ArgumentException("Primary identity must belong to the incoming selection.", nameof(primaryId));
        var incomingIds = additions.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        members = members.Where(node => !incomingIds.Contains(node.Id)).ToArray();

        var memberBottom = members.Length == 0 ? double.PositiveInfinity : members.Min(node => node.Bounds.Y);
        Point2 delta;
        if (members.Length == 0)
            delta = fallbackAnchor - primary.Position;
        else
        {
            // 移动完整占用区域，不能只移动主节点或分别调整各节点。
            // 整组选中节点放在既有成员最低边界下方，并保留统一间距。
            var memberLeft = members.Min(node => node.Bounds.X);
            var incomingLeft = additions.Min(node => node.Bounds.X);
            var incomingTop = additions.Max(node => node.Bounds.Y + node.Bounds.Height);
            delta = new Point2(memberLeft - incomingLeft, memberBottom - gap - incomingTop);
        }
        Guard.Point(delta, nameof(incoming));
        foreach (var node in additions)
        {
            var placed = node with { Position = node.Position + delta };
            ValidateGeometry(placed, nameof(incoming));
            // 极大的有限浮点数仍可能因舍入丢失间距；应拒绝无法表示的无重叠结果，
            // 不能错误地将这种布局视为空闲区域。
            // 正常坐标下，整个传入区域严格位于所有成员最低边界以下，已足以证明没有交集。
            // 间距被浮点舍入吞掉时才回退逐对检查，保留极端坐标与零尺寸节点的原有判断。
            var bounds = placed.Bounds;
            if (bounds.Y + bounds.Height >= memberBottom && members.Any(member => Intersects(bounds, member.Bounds)))
                throw new ArgumentException("The coordinates cannot represent a non-overlapping placement.", nameof(incoming));
        }
        return delta;
    }

    private static void ValidateNodes(IEnumerable<NodeState> nodes, string parameter)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            ArgumentNullException.ThrowIfNull(node);
            Guard.Id(node.Id, parameter);
            if (!identities.Add(node.Id)) throw new ArgumentException("Node identities must be unique within each input set.", parameter);
            ValidateGeometry(node, parameter);
        }
    }

    private static void ValidateGeometry(NodeState node, string parameter)
    {
        Guard.Point(node.Position, parameter);
        Guard.Size(node.Size, parameter);
        if (!node.Bounds.IsValid) throw new ArgumentException("Node bounds must be finite.", parameter);
    }

    private static bool Intersects(Rect2 first, Rect2 second) => first.X <= second.X + second.Width &&
        first.X + first.Width >= second.X && first.Y <= second.Y + second.Height && first.Y + first.Height >= second.Y;
}
