namespace NodeGroups.Core;

/// <summary>手势开始时冻结的组内容区域或折叠卡片。</summary>
/// <param name="GroupId">已存在的组标识。</param>
/// <param name="Bounds">画布坐标中的命中区域。</param>
/// <param name="ZIndex">重叠命中时优先选择层号最大的区域。</param>
public sealed record GroupHitRegion(string GroupId, Rect2 Bounds, int ZIndex);

/// <summary>用于目标高亮及原生坐标预览的只读结果，不修改工作区。</summary>
/// <param name="TargetGroupId">主节点标题中心命中的目标组；null 表示位于所有组之外。</param>
/// <param name="Nodes">仅包含被拖拽节点的预览几何。</param>
public sealed record DragPreview(string? TargetGroupId, IReadOnlyList<NodeState> Nodes);

/// <summary>用户拖拽事务，Commit 前不修改模型；取消时不会留下部分成员变化。</summary>
public sealed class DragSession : IDisposable
{
    private readonly GroupWorkspace workspace;
    private readonly Point2 primaryStart;
    private readonly NodeState[] startNodes;
    private readonly GroupHitRegion[] regions;
    private bool ended;

    internal DragSession(GroupWorkspace workspace, IEnumerable<string> nodeIds, string primaryNodeId, IEnumerable<GroupHitRegion> regions)
    {
        this.workspace = workspace;
        Before = workspace.Snapshot;
        NodeIds = Array.AsReadOnly(nodeIds.Distinct(StringComparer.Ordinal).ToArray());
        if (NodeIds.Count == 0 || !NodeIds.Contains(primaryNodeId, StringComparer.Ordinal))
            throw new ArgumentException("A drag needs present selected nodes and a primary node in that selection.", nameof(primaryNodeId));
        // 几何与归属都以手势开始时的快照为准；预览只做平移，避免每帧重复查找节点。
        startNodes = NodeIds.Select(Before.GetNode).ToArray();
        primaryStart = Before.GetNode(primaryNodeId).Position;
        // 捕获手势起点的完整命中几何；源组边框不得随移动中的成员扩张并持续追踪它们。
        this.regions = regions.ToArray();
        foreach (var region in this.regions)
        {
            Before.GetGroup(region.GroupId);
            if (!region.Bounds.IsValid) throw new ArgumentException("Drag hit rectangles must be finite and nonnegative.", nameof(regions));
        }
        // 一次排序后按从上到下命中。同层后出现的区域优先，不能用相减比较以免整数溢出。
        this.regions = this.regions.Select((region, index) => (region, index))
            .OrderByDescending(item => item.region.ZIndex).ThenByDescending(item => item.index)
            .Select(item => item.region).ToArray();
    }
    internal IReadOnlyList<string> NodeIds { get; }
    /// <summary>手势开始前的状态，取消时用于还原原生节点的预览坐标。</summary>
    public WorkspaceSnapshot Before { get; }
    /// <summary>生成目标组和统一平移结果，不修改当前工作区。</summary>
    /// <param name="delta">相对于手势起点的累计画布位移，必须为有限值。</param>
    /// <returns>返回选中节点预览，以及主节点起始位置加位移后命中的最高冻结区域。</returns>
    public DragPreview Preview(Point2 delta)
    {
        EnsureActive();
        Guard.Point(delta, nameof(delta));
        var hitPoint = primaryStart + delta;
        Guard.Point(hitPoint, nameof(delta));
        string? target = null;
        foreach (var region in regions)
        {
            if (!region.Bounds.Contains(hitPoint)) continue;
            target = region.GroupId;
            break;
        }
        var nodes = new NodeState[startNodes.Length];
        for (var index = 0; index < startNodes.Length; index++)
        {
            var node = startNodes[index];
            var position = node.Position + delta;
            Guard.Point(position, nameof(delta));
            nodes[index] = node with { Position = position };
        }
        return new DragPreview(target, Array.AsReadOnly(nodes));
    }
    /// <summary>原子提交几何、归属及目标组展开状态；手势期间工作区发生变化时抛出异常。</summary>
    /// <param name="delta">与最后一次 Preview 一致的累计位移。</param>
    /// <returns>一次可同时撤销坐标、成员关系和展开状态的命令。</returns>
    /// <exception cref="InvalidOperationException">会话已经结束，或中途模型修改使当前手势过期。</exception>
    public WorkspaceChange Commit(Point2 delta)
    {
        EnsureActive();
        var change = workspace.CommitDrag(this, delta);
        ended = true;
        return change;
    }
    /// <summary>结束手势且不修改模型；调用方使用 Before 还原原生预览坐标。</summary>
    public void Cancel() => ended = true;
    /// <summary>取消尚未完成的手势。</summary>
    public void Dispose() => Cancel();
    private void EnsureActive()
    {
        if (ended) throw new InvalidOperationException("Drag session has already committed or cancelled.");
    }
}
