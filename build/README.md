# 共享构建配置与部署锁

[RuntimeShared.props](RuntimeShared.props) 定义发行版和测试版共用的 Core/Storage 产品源码及 BepInEx/Unity 引用。路径相对于配置文件解析，使用 [Directory.Build.props](../Directory.Build.props) 中的 `GameDir`；链接源码排除 `bin/obj`。

| 构建 | 工程 | 根目录入口 |
| --- | --- | --- |
| 默认发行版 | [NodeGroups.Runtime.csproj](../src/NodeGroups.Runtime/NodeGroups.Runtime.csproj) | `./build.ps1` |
| 独立运行时测试版 | [NodeGroups.Runtime.Tests.csproj](../tests/NodeGroups.Runtime.Tests/NodeGroups.Runtime.Tests.csproj) | `./build.ps1 -TestBuild` |

两版程序集均命名为 `NodeGroups`，版本来自仓库配置，输出位于各自工程的 `bin/Release/net6.0/`。程序集元数据 `NodeGroups.BuildFlavor` 分别为 `Release` 和 `RuntimeTests`。

## PowerShell 版本与编码

`build.ps1` 及其共享部署锁支持 Windows PowerShell 5.1 和 PowerShell 7，包括默认构建、`-TestBuild`、`-Install`、`-Enable`。例如：

```powershell
powershell.exe -NoProfile -File .\build.ps1
powershell.exe -NoProfile -File .\build.ps1 -TestBuild
pwsh -NoProfile -File ./build.ps1
```

供 5.1 执行的中文脚本须使用 UTF-8 BOM。读取清单、ActiveProfile 与 Profile JSON 时显式指定 UTF-8；写入 JSON 使用 `File.WriteAllText` 和 `UTF8Encoding(false)`，保持无 BOM 的 UTF-8，并避免使用 5.1 不支持的 `utf8NoBOM` 参数值。

构建只调用独立的 .NET SDK，目标框架仍为 `net6.0`。Unity 集成与 PE 构建隔离检查继续使用 PowerShell 7；两种构建宿主的隔离契约见 [Verify-BuildCompatibility.ps1](../tests/build/Verify-BuildCompatibility.ps1)。

发行工程不导入测试目录、不定义 `NODEGROUPS_RUNTIME_TESTS`。测试工程链接同一产品源码，定义该符号，并包含运行器、场景、夹具、辅助方法和测试专用 `UnityEngine.ImageConversionModule` 引用。发行安装始终使用发行工程输出；`-TestBuild` 禁止与 `-Install` 或 `-Enable` 组合。

根目录的 [icon.png](../icon.png) 作为发行资产复制到产品工程的输出与 publish 目录。`-Install` 将图标与发行 DLL、manifest 放在同一 Mod 版本目录；测试工程不复制图标。

## 部署锁接口

[ModDeployment.ps1](ModDeployment.ps1) 提供：

```powershell
Enter-NodeGroupsDeployment -RepositoryDirectory <仓库根目录>
```

返回持有独占锁的 `FileStream`。调用者应将其持有至部署和还原结束，并在 `finally` 中调用 `Dispose()`。锁文件为仓库下的 `TestResults/mod-deployment.lock`；同一锁已被占用时立即抛出异常，不等待重试。

[build.ps1](../build.ps1) 的 `-Install` 与 [test-runtime.ps1](../test-runtime.ps1) 的临时部署使用同一锁。单纯构建不需要部署锁；临时测试在还原结束后才释放锁。此锁不约束外部手工文件操作。

## 临时测试部署

`test-runtime.ps1` 默认构建并校验两版 DLL，然后备份当前版本的 DLL、manifest 和当前 Profile，临时部署测试版。结束、失败或超时后恢复文件原始字节及存在状态；恢复失败会报错并保留备份。它只管理自己的子进程，并还原父进程测试环境变量。

`-SkipBuild` 只跳过构建，产物边界和加载身份检查仍执行。`-Focus` 默认 `Full`，专项包括 `Hover`、`Visual`、`Palette`、`Gestures`；`-TimeoutSeconds` 默认 180，范围 1–3600。

开发环境、项目放置方式和完整构建流程见 [开发与测试指南](../DEVELOPMENT.md)。功能与使用方法见 [主 README](../README.md)，检查脚本参数见 [构建与部署检查](../tests/build/README.md)。
