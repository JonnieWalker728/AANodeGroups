// 通过 extern alias 隔离原生编译器特性，避免污染全局类型解析。
// 运行时使用 .NET 的可空特性与集合；跨原生 API 的参数显式使用下方别名。
extern alias Il2Cpp;
global using NativeGuid = Il2Cpp::Il2CppSystem.Guid;
global using NativeEnvironment = Il2Cpp::Il2CppSystem.Environment;
