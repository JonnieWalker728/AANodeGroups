using System.Text.Json;

namespace AzureArchive.NodeGroups.Runtime;

/// <summary>读取选项目标，避免直接封送泛型可空的 16 字节 Guid。</summary>
internal static class ChoiceInterop
{
    /// <summary>
    /// 当前互操作构建的 Nullable&lt;Guid&gt; getter 会产生错误的值副本。
    /// 使用原生 Newtonsoft 序列化将该值保留在 IL2CPP 域，仅让 JSON 字符串跨越边界；
    /// 此读取路径不会修改选项或连接，不应以直接访问 TargetNodeId 替换。
    /// </summary>
    /// <param name="option">原生分支选项。</param>
    /// <returns>目标节点的 Guid 字符串；未连接目标时返回 null。</returns>
    internal static string? TargetId(ChoiceOptionData option)
    {
        using var document = JsonDocument.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(option));
        var target = document.RootElement.GetProperty(nameof(ChoiceOptionData.TargetNodeId));
        return target.ValueKind == JsonValueKind.Null ? null : target.GetString();
    }
}
