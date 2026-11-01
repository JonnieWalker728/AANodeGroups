using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NodeGroups.Core;

namespace NodeGroups.Storage;

/// <summary>在原生工程旁读取、校验并原子保存编辑器专用的节点组侧车数据。</summary>
public sealed class GroupStore
{
    private readonly IGroupFileSystem _fileSystem;
    private readonly GroupDocumentCodec _codec;
    private readonly GroupStoreLimits _limits;

    /// <summary>当前侧车格式版本；无法识别的版本直接拒绝导入。</summary>
    public const int SchemaVersion = 1;

    /// <summary>创建存储服务；文件读写是同步操作，输入必须是已冻结的托管快照。</summary>
    /// <param name="fileSystem">可选文件系统实现；默认使用同目录原子替换。</param>
    /// <param name="limits">可选资源上限；所有限制必须为正数，坐标上限还必须为有限值。</param>
    public GroupStore(IGroupFileSystem? fileSystem = null, GroupStoreLimits? limits = null)
    {
        _fileSystem = fileSystem ?? new PhysicalGroupFileSystem();
        _limits = limits ?? new GroupStoreLimits();
        if (_limits.MaxFileBytes <= 0 || _limits.MaxGroups <= 0 || _limits.MaxMembersPerGroup <= 0 ||
            _limits.MaxTotalMembers <= 0 || _limits.MaxNameLength <= 0 ||
            !double.IsFinite(_limits.MaxAbsoluteCoordinate) || _limits.MaxAbsoluteCoordinate <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "All storage limits must be positive and finite.");
        _codec = new GroupDocumentCodec(_limits);
    }

    /// <summary>在包含原扩展名的完整工程路径后追加侧车后缀。</summary>
    public static string GetSidecarPath(string projectFilePath)
    {
        if (string.IsNullOrWhiteSpace(projectFilePath))
            throw new ArgumentException("A saved project filename is required.", nameof(projectFilePath));
        return projectFilePath + ".nodegroups.json";
    }

    /// <summary>对实际已保存的原生工程字节求摘要；侧车存储不会修改这些字节。</summary>
    public static string ComputeCoreSnapshotHash(byte[] nativeProjectBytes)
    {
        ArgumentNullException.ThrowIfNull(nativeProjectBytes);
        return Convert.ToHexString(SHA256.HashData(nativeProjectBytes)).ToLowerInvariant();
    }

    /// <summary>校验并保存组元数据；提交失败时必须完整保留上一次成功保存的侧车。</summary>
    /// <param name="projectFilePath">原生工程路径，仅用于计算相邻侧车路径。</param>
    /// <param name="snapshot">对应某次原生保存成功回执的组快照。</param>
    /// <returns>提交结果及诊断；失败时调用方应继续保留待保存状态。</returns>
    public GroupSaveResult Save(string projectFilePath, GroupFileSnapshot snapshot)
    {
        string? temporaryPath = null;
        var diagnostics = new List<GroupStoreDiagnostic>();
        try
        {
            var destination = Path.GetFullPath(GetSidecarPath(projectFilePath));
            var validated = _codec.Validate(snapshot);
            var json = _codec.Serialize(validated);
            if (Encoding.UTF8.GetByteCount(json) > _limits.MaxFileBytes)
                throw new GroupValidationException("FileTooLarge", "Group metadata exceeds the sidecar byte limit.");

            // 临时文件与目标处于同一目录和卷，这是 File.Replace 原子替换的前提。
            // 不得以截断或覆盖模式直接打开正式侧车，以免失败时丢失旧数据。
            temporaryPath = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            _fileSystem.WriteNewText(temporaryPath, json);
            _fileSystem.Replace(temporaryPath, destination);
        }
        catch (GroupValidationException exception)
        {
            diagnostics.Add(new(exception.Code, exception.Message));
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            diagnostics.Add(new("SaveIoError", "Could not save node groups; the previous sidecar was retained. " + exception.Message));
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { _fileSystem.DeleteIfExists(temporaryPath); }
                catch (Exception exception) when (IsFileError(exception))
                {
                    // 清理失败不会撤销已完成的原子提交；单独报告残留临时文件即可。
                    // 不能因此要求调用方回滚原生工程或把成功保存误判为失败。
                    diagnostics.Add(new("TemporaryCleanupError", "Could not remove temporary group file: " + exception.Message));
                }
            }
        }
        var success = diagnostics.All(d => d.Code == "TemporaryCleanupError");
        return new(success, diagnostics.AsReadOnly());
    }

    /// <summary>
    /// 读取指定工程的元数据，并依据当前原生节点身份剔除失效成员。
    /// 摘要不一致仅要求重新计算显示布局，不会将旧侧车变成原生坐标来源。
    /// </summary>
    /// <param name="projectFilePath">原生工程路径，用于定位相邻侧车。</param>
    /// <param name="expectedProjectId">当前编辑会话的工程身份，区分大小写逐字符精确匹配。</param>
    /// <param name="currentCoreSnapshotHash">当前已保存原生工程字节的 SHA-256 摘要。</param>
    /// <param name="availableNodeIds">当前原生节点 GUID 集合，允许原生入口节点使用零 GUID。</param>
    /// <returns>读取状态、可导入快照和恢复诊断；任何失败均不改写输入文件。</returns>
    public GroupLoadResult Load(string projectFilePath, string expectedProjectId, string currentCoreSnapshotHash,
        IEnumerable<string> availableNodeIds)
    {
        try
        {
            var path = Path.GetFullPath(GetSidecarPath(projectFilePath));
            if (!_fileSystem.FileExists(path)) return new(GroupLoadStatus.Missing, null, false, Array.Empty<GroupStoreDiagnostic>());
            var saved = _codec.Parse(_fileSystem.ReadText(path, _limits.MaxFileBytes));
            if (!string.Equals(saved.ProjectId, expectedProjectId, StringComparison.Ordinal))
                throw new GroupValidationException("ProjectMismatch", "The group file belongs to another project; it was not imported. Keep it for manual recovery.");
            GroupDocumentCodec.ValidateHash(currentCoreSnapshotHash);

            // 将标准 GUID 映射回原生适配器使用的原始字符串，保持后续区分大小写的查找有效。
            // 同一 GUID 在 JSON 和原生节点中可能分别采用带括号或大小写不同的表示。
            var available = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var id in availableNodeIds)
                available.TryAdd(GroupDocumentCodec.NormalizeMemberId(id), id);
            var diagnostics = new List<GroupStoreDiagnostic>();
            var mismatch = !string.Equals(saved.CoreSnapshotHash, currentCoreSnapshotHash, StringComparison.OrdinalIgnoreCase);
            if (mismatch) diagnostics.Add(new("CoreSnapshotMismatch", "The native project changed since group metadata was saved. Existing members were recovered; recalculate group layout from current native node coordinates."));

            var missingCount = 0;
            var recoveredGroups = new List<GroupState>();
            foreach (var group in saved.Groups)
            {
                var members = new List<string>();
                foreach (var id in group.MemberNodeIds)
                {
                    if (available.TryGetValue(id, out var nativeId)) members.Add(nativeId);
                    else missingCount++;
                }
                // 恢复时保留空组，便于用户后续重新加入节点。
                // 对象不包含原生节点坐标，因此导入不会覆盖工程中的真实位置。
                recoveredGroups.Add(new(group.Id, group.Name, members, group.Collapsed, group.Anchor, group.Color,
                    group.ZIndex, group.ExpandedBounds));
            }
            if (missingCount > 0) diagnostics.Add(new("MissingMembers", $"{missingCount} group member(s) no longer exist in the native project. Their memberships were omitted; empty groups were retained."));
            var recovered = mismatch || missingCount > 0;
            return new(recovered ? GroupLoadStatus.Recovered : GroupLoadStatus.Loaded,
                new(saved.ProjectId, saved.CoreSnapshotHash, recoveredGroups), recovered, diagnostics.AsReadOnly());
        }
        catch (GroupValidationException exception)
        {
            return Failure(GroupLoadStatus.Invalid, exception.Code, exception.Message);
        }
        catch (JsonException exception)
        {
            return Failure(GroupLoadStatus.Invalid, "InvalidJson", "The group file is damaged or malformed. It was left untouched for manual recovery. " + exception.Message);
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            return Failure(GroupLoadStatus.IoError, "LoadIoError", "Could not read node groups. The file was left untouched. " + exception.Message);
        }
    }

    private static GroupLoadResult Failure(GroupLoadStatus status, string code, string message) =>
        new(status, null, false, new[] { new GroupStoreDiagnostic(code, message) });

    private static bool IsFileError(Exception exception) => exception is IOException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException or System.Security.SecurityException;
}
