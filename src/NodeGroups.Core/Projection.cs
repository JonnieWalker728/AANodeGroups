namespace NodeGroups.Core;

/// <summary>只读的原生剧情边；即使多个分支选项共用端点，各边仍相互独立。</summary>
/// <param name="Id">原生显示连线或选项边的标识。</param>
/// <param name="FromNodeId">原生起点节点 GUID。</param>
/// <param name="ToNodeId">原生终点节点 GUID。</param>
/// <param name="OptionId">原生选择分支的选项标识，无选项时可为 null。</param>
/// <param name="Text">原生分支标签，无标签时可为 null。</param>
public sealed record EdgeState(string Id, string FromNodeId, string ToNodeId, string? OptionId = null, string? Text = null);
/// <summary>单条原生边的显示方式。</summary>
public enum EdgeVisibility
{
    /// <summary>保留原生连线显示。</summary>
    Original,
    /// <summary>两个端点位于同一折叠组内，隐藏该连线。</summary>
    HiddenInternal,
    /// <summary>隐藏原生连线，绘制不参与交互的投影连线。</summary>
    Projected
}
/// <summary>投影端点的标识类型。</summary>
public enum EndpointKind
{
    /// <summary>原生节点。</summary>
    Node,
    /// <summary>独立组卡片。</summary>
    Group
}
/// <summary>仅用于显示的端点，不替代原生剧情节点。</summary>
/// <param name="Kind">节点或组。</param>
/// <param name="Id">原生节点 GUID 或独立组标识。</param>
public sealed record ProjectedEndpoint(EndpointKind Kind, string Id);
/// <summary>投影后的单条边，完整保留其原生标识。</summary>
/// <param name="Source">原生只读边，包含 OptionId 和标签。</param>
/// <param name="Visibility">显示分类。</param>
/// <param name="From">用于显示的画布端点。</param>
/// <param name="To">用于显示的画布端点。</param>
public sealed record ProjectedEdge(EdgeState Source, EdgeVisibility Visibility, ProjectedEndpoint From, ProjectedEndpoint To);
/// <summary>分类原生连接，不修改、去重或合并剧情边。</summary>
public static class EdgeProjector
{
    /// <summary>按输入顺序为每条原生边返回一条显示记录。</summary>
    /// <param name="snapshot">当前不可变的显示状态。</param>
    /// <param name="edges">原生节点连线标识；指向同一目标的不同选项仍须分别记录。</param>
    /// <returns>不参与交互的显示端点，保留每条边原有的 OptionId 和标签。</returns>
    public static IReadOnlyList<ProjectedEdge> Project(WorkspaceSnapshot snapshot, IEnumerable<EdgeState> edges)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(edges);
        var result = new List<ProjectedEdge>();
        foreach (var edge in edges)
        {
            var fromGroup = snapshot.FindGroupForNode(edge.FromNodeId);
            var toGroup = snapshot.FindGroupForNode(edge.ToNodeId);
            var from = fromGroup?.Collapsed == true ? new ProjectedEndpoint(EndpointKind.Group, fromGroup.Id) : new ProjectedEndpoint(EndpointKind.Node, edge.FromNodeId);
            var to = toGroup?.Collapsed == true ? new ProjectedEndpoint(EndpointKind.Group, toGroup.Id) : new ProjectedEndpoint(EndpointKind.Node, edge.ToNodeId);
            var visibility = from.Kind == EndpointKind.Group && to.Kind == EndpointKind.Group && from.Id == to.Id
                ? EdgeVisibility.HiddenInternal
                : from.Kind == EndpointKind.Group || to.Kind == EndpointKind.Group ? EdgeVisibility.Projected : EdgeVisibility.Original;
            // 每条输入边各保留一条记录；端点相同不能丢弃独立的 OptionId。
            result.Add(new ProjectedEdge(edge, visibility, from, to));
        }
        return result.AsReadOnly();
    }
}
