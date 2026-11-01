using NodeGroups.Core;

namespace NodeGroups.Storage;

/// <summary>与某次成功的原生工程保存对应的不可变分组显示快照。</summary>
/// <remarks>仅记录分组元数据；原生节点坐标、连接和台词不属于侧车数据。</remarks>
public sealed record GroupFileSnapshot
{
    /// <summary>复制组集合，防止文件读写期间调用方改变集合内容。</summary>
    /// <param name="projectId">原生工程身份，用于拒绝其他工程的侧车数据。</param>
    /// <param name="coreSnapshotHash">成功保存的原生工程字节对应的 SHA-256 十六进制摘要。</param>
    /// <param name="groups">仅包含显示和成员关系的不可变组对象。</param>
    public GroupFileSnapshot(string projectId, string coreSnapshotHash, IEnumerable<GroupState> groups)
    {
        ProjectId = projectId;
        CoreSnapshotHash = coreSnapshotHash;
        ArgumentNullException.ThrowIfNull(groups);
        Groups = Array.AsReadOnly(groups.ToArray());
    }

    /// <summary>原生工程身份，加载时必须与当前编辑会话完全一致。</summary>
    public string ProjectId { get; }
    /// <summary>实际成功保存的原生工程字节对应的 SHA-256 十六进制摘要。</summary>
    public string CoreSnapshotHash { get; }
    /// <summary>
    /// 只读组元数据，包括可选的手动调整区域及层序。
    /// 成员 ID 关联原生节点，不能替代节点的真实坐标或连接。
    /// </summary>
    public IReadOnlyList<GroupState> Groups { get; }
}

/// <summary>侧车读取状态；读取失败时不会修改或删除输入文件。</summary>
public enum GroupLoadStatus
{
    /// <summary>侧车文件不存在，可建立空的组集合。</summary>
    Missing,
    /// <summary>元数据与当前已保存工程版本一致，可以直接导入。</summary>
    Loaded,
    /// <summary>已恢复合法组，移除了失效成员或需要按当前原生节点重新计算布局。</summary>
    Recovered,
    /// <summary>文件格式损坏、版本不支持或属于其他工程；不得导入。</summary>
    Invalid,
    /// <summary>文件读取失败；调用方应保留内存状态并显示诊断信息。</summary>
    IoError
}

/// <summary>稳定诊断代码及便于排查问题的说明；调用方按代码区分处理方式。</summary>
public sealed record GroupStoreDiagnostic(string Code, string Message);

/// <summary>读取结果；只有 Loaded 和 Recovered 状态携带可导入的快照。</summary>
/// <param name="Status">读取或恢复的结果状态。</param>
/// <param name="Snapshot">校验并恢复后的快照；缺失或失败时为 null。</param>
/// <param name="RequiresLayoutRecalculation">工程摘要不同或成员缺失时，提示按当前原生节点重新计算显示布局。</param>
/// <param name="Diagnostics">只读诊断集合，可包含恢复原因。</param>
public sealed record GroupLoadResult(
    GroupLoadStatus Status,
    GroupFileSnapshot? Snapshot,
    bool RequiresLayoutRecalculation,
    IReadOnlyList<GroupStoreDiagnostic> Diagnostics);

/// <summary>保存结果；失败时调用方必须保留 GroupDirty 状态，由用户明确重试。</summary>
/// <param name="Success">侧车是否已成功提交；临时文件清理告警不撤销已成功的提交。</param>
/// <param name="Diagnostics">只读诊断集合，包含失败原因或清理告警。</param>
public sealed record GroupSaveResult(bool Success, IReadOnlyList<GroupStoreDiagnostic> Diagnostics);

/// <summary>
/// 导入外部元数据前执行的资源上限。
/// MaxAbsoluteCoordinate 限制锚点及展开区域的两个对角，不能仅检查原点而忽略宽高。
/// </summary>
public sealed record GroupStoreLimits(
    int MaxFileBytes = 8 * 1024 * 1024,
    int MaxGroups = 1024,
    int MaxMembersPerGroup = 10000,
    int MaxTotalMembers = 50000,
    int MaxNameLength = 256,
    double MaxAbsoluteCoordinate = 10000000);

/// <summary>文件系统抽象，用于注入原子保存故障或替换存储后端。</summary>
public interface IGroupFileSystem
{
    /// <summary>判断侧车文件是否存在。</summary>
    bool FileExists(string path);
    /// <summary>按 UTF-8 读取 JSON；分配完整内容缓冲区前拒绝超过 <paramref name="maxBytes"/> 的文件。</summary>
    string ReadText(string path, int maxBytes);
    /// <summary>独占创建指定临时文件并刷新到存储设备；不得覆盖任何已存在文件。</summary>
    void WriteNewText(string path, string text);
    /// <summary>
    /// 将同目录临时文件原子替换为正式侧车。发生异常时，已有目标必须保留完整原内容。
    /// 该约定是保存失败后继续恢复旧数据的基础，不能改成截断目标后再写入。
    /// </summary>
    void Replace(string temporaryPath, string destinationPath);
    /// <summary>仅删除指定的临时文件；文件已经不存在时不执行操作。</summary>
    void DeleteIfExists(string path);
}
