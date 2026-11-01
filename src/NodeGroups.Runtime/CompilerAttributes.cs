// Unity 生成的 CoreModule 含有不完整的 NullableAttribute 占位类型。
// 在当前程序集补齐编译器元数据；这些特性只表达可空性，不参与游戏行为。
namespace System.Runtime.CompilerServices;

[AttributeUsage(AttributeTargets.All, Inherited = false)]
internal sealed class NullableAttribute : Attribute
{
    public readonly byte[] NullableFlags;
    public NullableAttribute(byte flag) => NullableFlags = new[] { flag };
    public NullableAttribute(byte[] flags) => NullableFlags = flags;
}

[AttributeUsage(AttributeTargets.All, Inherited = false)]
internal sealed class NullableContextAttribute : Attribute
{
    public readonly byte Flag;
    public NullableContextAttribute(byte flag) => Flag = flag;
}
