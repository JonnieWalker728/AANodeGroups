using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using NodeGroups.Core;

namespace NodeGroups.Storage;

/// <summary>在版本化 JSON 与经过校验的不可变组显示模型之间转换。</summary>
internal sealed class GroupDocumentCodec
{
    private readonly GroupStoreLimits _limits;
    // 省略未设置的区域字段，让从未调整边框的组保持原有 schema 1 表示。
    // expandedBounds 是兼容旧数据的可选显示字段，不需要提升格式版本。
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal GroupDocumentCodec(GroupStoreLimits limits) => _limits = limits;

    /// <summary>序列化已校验快照；仅输出侧车字段，不能加入原生节点坐标或连接。</summary>
    internal string Serialize(GroupFileSnapshot snapshot) => JsonSerializer.Serialize(new
    {
        schemaVersion = GroupStore.SchemaVersion,
        projectId = snapshot.ProjectId,
        coreSnapshotHash = snapshot.CoreSnapshotHash,
        groups = snapshot.Groups.Select(g => new
        {
            groupId = g.Id, name = g.Name, memberNodeIds = g.MemberNodeIds, collapsed = g.Collapsed,
            anchor = new { x = g.Anchor.X, y = g.Anchor.Y }, color = g.Color, zIndex = g.ZIndex,
            expandedBounds = g.ExpandedBounds is { } bounds
                ? new { x = bounds.X, y = bounds.Y, width = bounds.Width, height = bounds.Height }
                : null
        })
    }, WriteOptions);

    /// <summary>解析外部 JSON，并统一执行格式、数量及唯一性校验；任何错误均拒绝整份快照。</summary>
    internal GroupFileSnapshot Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        try { return ParseDocument(document.RootElement); }
        catch (InvalidOperationException exception)
        {
            // JsonDocument 可接受含孤立代理项的转义文本，直到读取字符串/属性名时才报错。
            // 只转换本解析器内部的解码错误；不能在 Load 外层吞掉调用方节点枚举器的异常。
            throw new GroupValidationException("InvalidJson", "The group file contains invalid JSON string encoding. " + exception.Message);
        }
    }

    private GroupFileSnapshot ParseDocument(JsonElement root)
    {
        Require(root.ValueKind == JsonValueKind.Object, "InvalidDocument", "The sidecar root must be a JSON object.");
        ValidateUniqueProperties(root);
        Require(root.TryGetProperty("schemaVersion", out var schema) && schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out var version) &&
            version == GroupStore.SchemaVersion, "UnsupportedSchema", "Missing or unsupported sidecar schema; no metadata was imported.");
        var projectId = String(root, "projectId", "InvalidProjectId");
        var hash = String(root, "coreSnapshotHash", "InvalidHash");
        Require(root.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Array,
            "InvalidGroups", "The sidecar must contain a groups array.");
        Require(groups.GetArrayLength() <= _limits.MaxGroups, "TooManyGroups", "The sidecar contains too many groups.");
        var parsed = new List<GroupState>();
        var totalMembers = 0;
        foreach (var group in groups.EnumerateArray())
        {
            Require(group.ValueKind == JsonValueKind.Object, "InvalidGroup", "Each group must be a JSON object.");
            var id = String(group, "groupId", "InvalidGroupId");
            var name = String(group, "name", "InvalidName");
            var color = String(group, "color", "InvalidColor");
            Require(group.TryGetProperty("memberNodeIds", out var members) && members.ValueKind == JsonValueKind.Array,
                "InvalidMembers", "Group members must be an array of native node GUID strings.");
            Require(members.GetArrayLength() <= _limits.MaxMembersPerGroup, "TooManyMembers", "A group exceeds its member limit.");
            totalMembers += members.GetArrayLength();
            Require(totalMembers <= _limits.MaxTotalMembers, "TooManyMembers", "The sidecar exceeds the total member limit.");
            var memberIds = new List<string>();
            foreach (var member in members.EnumerateArray())
            {
                Require(member.ValueKind == JsonValueKind.String, "InvalidMemberId", "Member identity must be a GUID string.");
                memberIds.Add(member.GetString()!);
            }
            Require(group.TryGetProperty("collapsed", out var collapsed) &&
                collapsed.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "InvalidCollapsedState", "Collapsed must be an explicit boolean.");
            Require(group.TryGetProperty("anchor", out var anchor) && anchor.ValueKind == JsonValueKind.Object,
                "InvalidAnchor", "Group anchor must contain finite x/y coordinates.");
            var x = Coordinate(anchor, "x");
            var y = Coordinate(anchor, "y");
            var zIndex = 0; // schema 1 的可选字段；缺省值兼容早期侧车。
            if (group.TryGetProperty("zIndex", out var z))
                Require(z.ValueKind == JsonValueKind.Number && z.TryGetInt32(out zIndex), "InvalidZIndex", "Stacking order must be a 32-bit integer.");
            Rect2? expandedBounds = null;
            if (group.TryGetProperty("expandedBounds", out var region) && region.ValueKind != JsonValueKind.Null)
            {
                Require(region.ValueKind == JsonValueKind.Object, "InvalidExpandedBounds", "Expanded group bounds must be an x/y/width/height object or null.");
                expandedBounds = new Rect2(BoundsNumber(region, "x"), BoundsNumber(region, "y"),
                    BoundsNumber(region, "width"), BoundsNumber(region, "height"));
                ValidateExpandedBounds(expandedBounds.Value);
            }
            parsed.Add(new(id, name, memberIds, collapsed.GetBoolean(), new Point2(x, y), color, zIndex, expandedBounds));
        }
        return Validate(new(projectId, hash, parsed));
    }

    /// <summary>校验完整快照并规范化 GUID 和摘要；保存与加载共用规则，避免两条路径产生差异。</summary>
    internal GroupFileSnapshot Validate(GroupFileSnapshot snapshot)
    {
        Require(snapshot is not null, "InvalidDocument", "Group snapshot cannot be null.");
        Require(IsValidText(snapshot.ProjectId, 128), "InvalidProjectId",
            "A native project identity of 1 to 128 valid UTF-16 characters without control characters is required.");
        ValidateHash(snapshot.CoreSnapshotHash);
        Require(snapshot.Groups.Count <= _limits.MaxGroups, "TooManyGroups", "The snapshot contains too many groups.");
        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        var memberIds = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<GroupState>();
        foreach (var group in snapshot.Groups)
        {
            Require(group is not null, "InvalidGroup", "Group metadata cannot be null.");
            var id = NormalizeGroupId(group.Id);
            Require(groupIds.Add(id), "DuplicateGroup", "A group identity occurs more than once; no metadata was imported.");
            Require(IsValidText(group.Name, _limits.MaxNameLength), "InvalidName",
                "Group names must be non-empty, within the name limit and contain valid UTF-16 without control characters.");
            Require(group.Color is not null && group.Color.Length == 7 && group.Color[0] == '#' &&
                group.Color[1..].All(Uri.IsHexDigit), "InvalidColor", "Group color must use #RRGGBB hexadecimal syntax.");
            ValidateCoordinate(group.Anchor.X);
            ValidateCoordinate(group.Anchor.Y);
            if (group.ExpandedBounds is { } bounds) ValidateExpandedBounds(bounds);
            Require(group.MemberNodeIds.Count <= _limits.MaxMembersPerGroup, "TooManyMembers", "A group exceeds its member limit.");
            var members = new List<string>();
            foreach (var member in group.MemberNodeIds)
            {
                var canonical = NormalizeMemberId(member);
                Require(memberIds.Add(canonical), "DuplicateMember", "A node belongs to multiple groups or occurs twice in one group; no metadata was imported.");
                members.Add(canonical);
                Require(memberIds.Count <= _limits.MaxTotalMembers, "TooManyMembers", "The snapshot exceeds the total member limit.");
            }
            normalized.Add(new(id, group.Name, members, group.Collapsed, group.Anchor, group.Color, group.ZIndex, group.ExpandedBounds));
        }
        return new(snapshot.ProjectId, snapshot.CoreSnapshotHash.ToLowerInvariant(), normalized);
    }

    /// <summary>限制为 SHA-256 的完整十六进制表示，允许大小写差异。</summary>
    internal static void ValidateHash(string hash) => Require(hash is not null && hash.Length == 64 && hash.All(Uri.IsHexDigit),
        "InvalidHash", "The native project digest must be a 64-character SHA-256 hexadecimal string.");

    /// <summary>组身份由 Mod 创建；零 GUID 不能作为独立组身份。</summary>
    internal static string NormalizeGroupId(string id) => NormalizeGuid(id, "InvalidGroupId", allowEmpty: false);

    /// <summary>
    /// 节点身份由 AzureArchive 管理：Guid.Empty 是合法的保留入口节点身份。
    /// 必须保留零 GUID，不能将其视为缺失身份或生成新 GUID 替换。
    /// </summary>
    internal static string NormalizeMemberId(string id) => NormalizeGuid(id, "InvalidMemberId", allowEmpty: true);

    private static string NormalizeGuid(string id, string code, bool allowEmpty)
    {
        Require(Guid.TryParse(id, out var guid) && (allowEmpty || guid != Guid.Empty), code,
            allowEmpty ? "Native member identities must use GUID syntax, including the reserved zero entry GUID."
                : "Independent group identities must be non-empty GUID strings.");
        // 运行期绝大多数标识已经是标准 D 格式。先写入栈上缓冲区比较，
        // 规范输入直接复用原字符串，避免每次保存/加载为数万成员再分配同样的文本。
        // 其他格式、大小写或首尾空白仍统一输出小写 D 格式。
        Span<char> canonical = stackalloc char[36];
        guid.TryFormat(canonical, out _, "D");
        return id.AsSpan().SequenceEqual(canonical) ? id : new string(canonical);
    }

    /// <summary>序列化前拒绝孤立代理项，避免 JSON 编码器静默替换名称或工程身份。</summary>
    private static bool IsValidText(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > maxLength) return false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (char.IsControl(character)) return false;
            if (!char.IsSurrogate(character)) continue;
            // emoji 等补充平面字符使用完整的高/低代理对，必须原样保留。
            if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            { index++; continue; }
            return false;
        }
        return true;
    }

    private double Coordinate(JsonElement anchor, string property)
    {
        Require(anchor.TryGetProperty(property, out var coordinate) && coordinate.ValueKind == JsonValueKind.Number &&
            coordinate.TryGetDouble(out _), "InvalidAnchor", "Group anchor must contain numeric x/y coordinates.");
        var value = coordinate.GetDouble();
        ValidateCoordinate(value);
        return value;
    }

    private void ValidateCoordinate(double value) => Require(double.IsFinite(value) && Math.Abs(value) <= _limits.MaxAbsoluteCoordinate,
        "InvalidAnchor", "Group anchor coordinates must be finite and within canvas safety limits.");

    private static double BoundsNumber(JsonElement region, string property)
    {
        Require(region.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out _), "InvalidExpandedBounds", "Expanded group bounds must contain numeric x/y/width/height values.");
        return value.GetDouble();
    }

    private void ValidateExpandedBounds(Rect2 bounds)
    {
        // 只校验原点或宽高会漏掉跨越安全画布的矩形。
        // 同时校验两个对角，覆盖 x+width/y+height 越界和溢出为无穷大的情况。
        Require(bounds.IsValid && bounds.Width > 0 && bounds.Height > 0 &&
            Math.Abs(bounds.X) <= _limits.MaxAbsoluteCoordinate && Math.Abs(bounds.Y) <= _limits.MaxAbsoluteCoordinate &&
            Math.Abs(bounds.X + bounds.Width) <= _limits.MaxAbsoluteCoordinate &&
            Math.Abs(bounds.Y + bounds.Height) <= _limits.MaxAbsoluteCoordinate,
            "InvalidExpandedBounds", "Expanded group bounds require positive finite dimensions and all edges within canvas safety limits.");
    }

    private static string String(JsonElement element, string property, string code)
    {
        Require(element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String,
            code, $"{property} must be a non-null JSON string.");
        return value.GetString()!;
    }

    // 在读取身份字段前递归拒绝重复属性，避免 JSON 的“后者覆盖前者”产生歧义。
    private static void ValidateUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                Require(names.Add(property.Name), "DuplicateProperty", "The group file contains duplicate JSON properties; ambiguous metadata was not imported.");
                ValidateUniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray()) ValidateUniqueProperties(child);
        }
    }

    private static void Require([DoesNotReturnIf(false)] bool condition, string code, string message)
    {
        if (!condition) throw new GroupValidationException(code, message);
    }
}

/// <summary>携带稳定诊断代码的校验异常，由存储入口转换为可报告结果。</summary>
internal sealed class GroupValidationException : Exception
{
    internal string Code { get; }
    internal GroupValidationException(string code, string message) : base(message) => Code = code;
}
