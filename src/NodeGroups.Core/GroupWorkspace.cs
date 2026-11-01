namespace NodeGroups.Core;

/// <summary>纯托管的分组状态机。适配层负责在 Unity 主线程将坐标变化同步到原生节点。</summary>
public sealed class GroupWorkspace
{
    /// <summary>根据当前原生节点几何和可选的已加载组元数据创建会话。</summary>
    /// <param name="nodes">原生编辑器适配层提供的节点几何。</param>
    /// <param name="groups">已验证的组元数据；新会话可传 null。</param>
    public GroupWorkspace(IEnumerable<NodeState> nodes, IEnumerable<GroupState>? groups = null) =>
        Snapshot = new WorkspaceSnapshot(nodes, groups ?? Array.Empty<GroupState>());
    /// <summary>不可变的当前状态，可安全用于存储和命令历史快照。</summary>
    public WorkspaceSnapshot Snapshot { get; private set; }
    /// <summary>创建空组或包含成员的组，并转移成员归属；不修改原生节点。</summary>
    /// <param name="id">调用方生成的新组独立标识。</param>
    /// <param name="name">显示名称，校验前去除首尾空白。</param>
    /// <param name="nodeIds">已存在的原生节点标识；空集合创建可继续使用的空组。</param>
    /// <param name="anchor">折叠卡片或空组边框在画布中的初始锚点。</param>
    /// <param name="color">显示颜色。</param>
    /// <param name="expandedBounds">可选的展开区域，坐标须有限且宽高为正；null 保留旧数据的自动布局回退逻辑。</param>
    /// <returns>包含建组和既有成员归属转移的一次原子命令。</returns>
    public WorkspaceChange CreateGroup(string id, string name, IEnumerable<string> nodeIds, Point2 anchor,
        string color = "#4C88B8", Rect2? expandedBounds = null)
    {
        Guard.Id(id, nameof(id));
        name = Guard.Name(name, nameof(name));
        Guard.Point(anchor, nameof(anchor));
        if (expandedBounds is { } bounds) Guard.ExpandedBounds(bounds, nameof(expandedBounds));
        if (Snapshot.Groups.Any(g => g.Id == id)) throw new ArgumentException("Group identity already exists.", nameof(id));
        var members = ValidatePresentNodes(nodeIds);
        var groups = TransferOwnership(Snapshot.Groups, members, null).ToList();
        var top = groups.Count == 0 ? 0 : groups.Max(g => g.ZIndex);
        if (top == int.MaxValue)
        {
            groups = NormalizeLayers(groups).ToList();
            top = groups.Count - 1;
        }
        groups.Add(new GroupState(id, name, members, false, anchor, color, checked(top + 1), expandedBounds));
        return Apply("创建节点组", Snapshot.Nodes, groups);
    }
    /// <summary>修改显示名称，保留组标识。</summary>
    /// <param name="groupId">已存在的独立组标识。</param>
    /// <param name="name">去除首尾空白后长度为 1 到 256 个字符的显示名称。</param>
    public WorkspaceChange RenameGroup(string groupId, string name)
    {
        var group = Snapshot.GetGroup(groupId);
        name = Guard.Name(name, nameof(name));
        return Apply("重命名节点组", Snapshot.Nodes, ReplaceGroup(group.Replace(name: name)));
    }
    /// <summary>仅切换显示状态，保留全部原生节点和连接。</summary>
    /// <param name="groupId">已存在的独立组标识。</param>
    /// <param name="collapsed">true 显示紧凑卡片，false 恢复组内节点显示。</param>
    public WorkspaceChange SetCollapsed(string groupId, bool collapsed)
    {
        var group = Snapshot.GetGroup(groupId);
        return Apply(collapsed ? "收起节点组" : "展开节点组", Snapshot.Nodes, ReplaceGroup(group.Replace(collapsed: collapsed)));
    }
    /// <summary>仅调整展开区域的位置或尺寸，不移动成员、不改变归属，也不修改折叠卡片锚点。</summary>
    /// <param name="groupId">已存在的独立组标识。</param>
    /// <param name="expandedBounds">画布中的区域；坐标与远端边界均须有限，宽高严格为正。</param>
    /// <returns>返回一次可撤销的元数据命令；区域完全相同时返回空操作。</returns>
    /// <remarks>运行时适配层限制交互缩小时仍包含当前成员；Core 不根据区域几何推断成员或节点移动。</remarks>
    /// <exception cref="ArgumentException">区域含非有限数、边界溢出或宽高非正。</exception>
    public WorkspaceChange SetExpandedBounds(string groupId, Rect2 expandedBounds)
    {
        var group = Snapshot.GetGroup(groupId);
        Guard.ExpandedBounds(expandedBounds, nameof(expandedBounds));
        return Apply("调整节点组区域", Snapshot.Nodes, ReplaceGroup(group.Replace(expandedBounds: expandedBounds)));
    }
    /// <summary>将选中的组提升到顶层，其他组的相对显示顺序保持不变。</summary>
    /// <param name="groupId">用户明确选中的组；悬浮和拖放目标高亮不得调用此命令。</param>
    /// <returns>返回元数据命令；组已处于最上层时返回空操作。</returns>
    /// <remarks>同层时按快照列表顺序决定，后列出的组在上方，与 DragSession 的命中规则一致。
    /// 最高层为 Int32.MaxValue 时先按相同稳定顺序规范化层号，再提升目标组，避免溢出。</remarks>
    public WorkspaceChange PromoteGroup(string groupId)
    {
        var group = Snapshot.GetGroup(groupId);
        var ordered = LayerOrder(Snapshot.Groups);
        if (ordered[^1].Id == groupId) return new WorkspaceChange("选中节点组", Snapshot, Snapshot);
        var highest = ordered[^1].ZIndex;
        if (highest < int.MaxValue)
            return Apply("选中节点组", Snapshot.Nodes, ReplaceGroup(group.Replace(zIndex: highest + 1)));

        // 仅将选中项移至末尾，再分配连续且唯一的层号。
        // 保留快照列表顺序；原生适配层同样依靠此顺序处理相同深度。
        var ranks = ordered.Where(g => g.Id != groupId).Append(group)
            .Select((g, index) => (g.Id, index)).ToDictionary(item => item.Id, item => item.index, StringComparer.Ordinal);
        return Apply("选中节点组", Snapshot.Nodes, Snapshot.Groups.Select(g => g.Replace(zIndex: ranks[g.Id])));
    }
    /// <summary>解散时只移除组元数据，保留全部原生节点及坐标。</summary>
    /// <param name="groupId">已存在的独立组标识。</param>
    public WorkspaceChange DissolveGroup(string groupId)
    {
        Snapshot.GetGroup(groupId);
        return Apply("解散节点组", Snapshot.Nodes, Snapshot.Groups.Where(g => g.Id != groupId));
    }
    /// <summary>原子地转移节点到目标组；目标为 null 时将其移出所有组。</summary>
    /// <param name="nodeIds">已存在的节点标识；忽略重复项并保留首次出现的顺序。</param>
    /// <param name="targetGroupId">已存在的目标组标识；null 表示移出组。</param>
    /// <remarks>此显式元数据操作不移动节点或展开组；拖放和创建命令自行处理目标组展开。</remarks>
    public WorkspaceChange TransferNodes(IEnumerable<string> nodeIds, string? targetGroupId)
    {
        if (targetGroupId != null) Snapshot.GetGroup(targetGroupId);
        var members = ValidatePresentNodes(nodeIds);
        return Apply("更改节点归属", Snapshot.Nodes, TransferOwnership(Snapshot.Groups, members, targetGroupId));
    }
    /// <summary>以同一位移移动当前存在的全部成员，保持相对位置与归属。</summary>
    /// <param name="groupId">已存在的组标识。</param>
    /// <param name="delta">有限的画布位移；若需吸附，适配层应先对整个位移统一吸附一次。</param>
    public WorkspaceChange MoveGroup(string groupId, Point2 delta)
    {
        var group = Snapshot.GetGroup(groupId);
        Guard.Point(delta, nameof(delta));
        var members = group.MemberNodeIds.ToHashSet(StringComparer.Ordinal);
        // 统一位移才能保持布局；运行时适配层不得分别吸附各个成员。
        var nodes = Snapshot.Nodes.Select(n => members.Contains(n.Id) ? n with { Position = n.Position + delta } : n);
        return Apply("移动节点组", nodes, ReplaceGroup(group.Replace(anchor: group.Anchor + delta,
            expandedBounds: group.ExpandedBounds?.Translate(delta))));
    }
    /// <summary>登记适配层已创建的真实原生节点；加入折叠组时会展开该组。</summary>
    /// <param name="node">新原生节点的标识与几何；登记前必须完成原生创建。</param>
    /// <param name="targetGroupId">要加入的组；null 表示仅同步原生生命周期。</param>
    /// <remarks>调用方将此视图命令与原生创建操作合并；Core 不分配原生 GUID，也不实例化节点。</remarks>
    public WorkspaceChange RegisterNode(NodeState node, string? targetGroupId = null)
    {
        if (Snapshot.Nodes.Any(n => n.Id == node.Id)) throw new ArgumentException("Node identity already exists.", nameof(node));
        if (targetGroupId != null) Snapshot.GetGroup(targetGroupId);
        var groups = targetGroupId == null ? Snapshot.Groups : TransferOwnership(Snapshot.Groups, new[] { node.Id }, targetGroupId);
        if (targetGroupId != null) groups = groups.Select(g => g.Id == targetGroupId ? g.Replace(collapsed: false) : g).ToArray();
        return Apply("组内创建节点", Snapshot.Nodes.Append(node), groups);
    }
    /// <summary>移除运行期几何，但保留成员标识供原生编辑器撤销恢复。</summary>
    /// <param name="nodeId">已存在的原生节点 GUID。</param>
    public WorkspaceChange RemoveNode(string nodeId)
    {
        Snapshot.GetNode(nodeId);
        // 原生 Undo 也会触发 UnregisterNode；对应历史失效前需保留成员元数据。
        return Apply("同步节点删除", Snapshot.Nodes.Where(n => n.Id != nodeId), Snapshot.Groups);
    }
    /// <summary>刷新由原生节点维护的权威坐标，不根据几何推断成员归属。</summary>
    /// <param name="nodes">当前全部原生节点，而不是仅发生变化的子集。</param>
    /// <remarks>拖拽预览期间不得调用；必须先提交或取消，避免覆盖手势开始时冻结的状态。</remarks>
    public WorkspaceChange ReconcileNodes(IEnumerable<NodeState> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        // 原生适配层按轮询提供数组。内容及顺序均不变时复用已验证的快照，
        // 避免空闲时重复分配全部节点、组和成员索引。延迟序列仍走一次枚举的原验证路径。
        if (nodes is IReadOnlyList<NodeState> list && list.Count == Snapshot.Nodes.Count)
        {
            var same = true;
            for (var index = 0; index < list.Count; index++)
                if (Snapshot.Nodes[index] != list[index]) { same = false; break; }
            if (same) return new WorkspaceChange("同步原生节点", Snapshot, Snapshot);
        }
        return Apply("同步原生节点", nodes, Snapshot.Groups);
    }
    /// <summary>恢复命令内容并增加会话版本号；此方法不重建原生节点。</summary>
    /// <param name="snapshot">本编辑器会话中命令执行前或执行后的不可变快照。</param>
    /// <remarks>原生位置变化及创建、删除的撤销由运行时另行处理；此方法仅修改托管视图状态。</remarks>
    public void Restore(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Snapshot = new WorkspaceSnapshot(snapshot.Nodes, snapshot.Groups, checked(Snapshot.Revision + 1));
    }
    /// <summary>使用冻结的内容区域或卡片区域开始用户拖拽；程序性移动不推断归属。</summary>
    /// <param name="nodeIds">当前存在且被选中的原生节点标识，必须包含主节点。</param>
    /// <param name="primaryNodeId">以此节点的 Position 决定整个多选集合的唯一目标组。</param>
    /// <param name="regions">当前组内容矩形或折叠卡片区域，在手势开始时复制。</param>
    /// <returns>不修改状态的预览事务；Dispose 会取消尚未结束的手势。</returns>
    public DragSession BeginDrag(IEnumerable<string> nodeIds, string primaryNodeId, IEnumerable<GroupHitRegion> regions) =>
        new(this, nodeIds, primaryNodeId, regions);
    internal WorkspaceChange CommitDrag(DragSession session, Point2 delta)
    {
        if (!ReferenceEquals(Snapshot, session.Before))
            throw new InvalidOperationException("Workspace changed during drag; discard the stale preview and start a new gesture.");
        var preview = session.Preview(delta);
        var proposed = preview.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var groups = TransferOwnership(Snapshot.Groups, session.NodeIds, preview.TargetGroupId);
        if (preview.TargetGroupId != null)
            groups = groups.Select(g => g.Id == preview.TargetGroupId ? g.Replace(collapsed: false) : g).ToArray();
        return Apply("拖动并更改节点归属", Snapshot.Nodes.Select(n => proposed.TryGetValue(n.Id, out var moved) ? moved : n), groups);
    }

    private string[] ValidatePresentNodes(IEnumerable<string> nodeIds)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        var ids = nodeIds.Distinct(StringComparer.Ordinal).ToArray();
        var present = Snapshot.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            Guard.Id(id, nameof(nodeIds));
            if (!present.Contains(id)) throw new ArgumentException($"Node '{id}' does not exist.", nameof(nodeIds));
        }
        return ids;
    }

    private IEnumerable<GroupState> ReplaceGroup(GroupState replacement) => Snapshot.Groups.Select(g => g.Id == replacement.Id ? replacement : g);

    private static GroupState[] LayerOrder(IReadOnlyList<GroupState> groups) => groups
        .Select((group, index) => (group, index)).OrderBy(item => item.group.ZIndex).ThenBy(item => item.index)
        .Select(item => item.group).ToArray();

    private static IReadOnlyList<GroupState> NormalizeLayers(IReadOnlyList<GroupState> groups)
    {
        var ranks = LayerOrder(groups).Select((group, index) => (group.Id, index))
            .ToDictionary(item => item.Id, item => item.index, StringComparer.Ordinal);
        return groups.Select(group => group.Replace(zIndex: ranks[group.Id])).ToArray();
    }

    private static IReadOnlyList<GroupState> TransferOwnership(IEnumerable<GroupState> groups, IReadOnlyList<string> ids, string? target)
    {
        var selected = ids.ToHashSet(StringComparer.Ordinal);
        return groups.Select(group =>
        {
            // 目标组原成员保持顺序，新成员按选择顺序追加。用集合查重，避免每个新节点扫描整个组。
            if (group.Id == target)
            {
                var existing = group.MemberNodeIds.ToHashSet(StringComparer.Ordinal);
                return group.Replace(members: group.MemberNodeIds.Concat(ids.Where(id => !existing.Contains(id))));
            }
            return group.Replace(members: group.MemberNodeIds.Where(id => !selected.Contains(id)));
        }).ToArray();
    }

    private WorkspaceChange Apply(string name, IEnumerable<NodeState> nodes, IEnumerable<GroupState> groups)
    {
        var before = Snapshot;
        // 先完整构造并验证新快照，防止非法输入导致工作区被部分修改。
        var after = new WorkspaceSnapshot(nodes, groups, checked(before.Revision + 1));
        var sameGroups = before.Groups.Count == after.Groups.Count && before.Groups.Zip(after.Groups).All(pair =>
            pair.First.Id == pair.Second.Id && pair.First.Name == pair.Second.Name && pair.First.Collapsed == pair.Second.Collapsed &&
            pair.First.Anchor == pair.Second.Anchor && pair.First.Color == pair.Second.Color && pair.First.ZIndex == pair.Second.ZIndex &&
            pair.First.ExpandedBounds == pair.Second.ExpandedBounds &&
            pair.First.MemberNodeIds.SequenceEqual(pair.Second.MemberNodeIds, StringComparer.Ordinal));
        if (sameGroups && before.Nodes.SequenceEqual(after.Nodes)) return new WorkspaceChange(name, before, before);
        Snapshot = after;
        return new WorkspaceChange(name, before, after);
    }
}
