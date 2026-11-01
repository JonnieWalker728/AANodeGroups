# 构建边界与部署保护检查

这里的脚本检查构建入口兼容性、编译产物及部署清理行为，与单元测试和 Unity 功能测试分工。构建兼容性脚本支持 Windows PowerShell 5.1 和 PowerShell 7；PE 元数据及 Unity 部署检查使用 PowerShell 7。所有命令在仓库根目录执行，本地报告保存在被 Git 忽略的 `TestResults/`。

## 构建入口兼容性

[Verify-BuildCompatibility.ps1](Verify-BuildCompatibility.ps1) 在当前宿主中运行实际 `build.ps1` 的独立夹具，可分别验证 5.1 和 7：

```powershell
powershell.exe -NoProfile -File .\tests\build\Verify-BuildCompatibility.ps1 -ReportPath .\TestResults\build-compatibility-51.json
pwsh -NoProfile -File ./tests/build/Verify-BuildCompatibility.ps1 -ReportPath ./TestResults/build-compatibility-7.json
```

检查使用 `TestResults` 下自建的中文路径和中文 Profile，复制构建与锁脚本原始字节，模拟 `dotnet`，验证参数保护、发行与测试项目选择、安装源、JSON 编码、其他 Mod 保留、环境还原及锁释放。不会启动应用或改动真实安装；SDK 编译由真实 `build.ps1` 调用验证。可选 `-ReportPath` 写入 JSON 结果，失败会抛出异常并保留夹具证据。

## 构建边界

先生成两版 DLL：

```powershell
pwsh -NoProfile -File ./build.ps1
pwsh -NoProfile -File ./build.ps1 -TestBuild
```

[Verify-BuildSeparation.ps1](Verify-BuildSeparation.ps1) 读取实际 DLL 的 PE/CLI 元数据和方法 IL，不加载 Unity/BepInEx，不执行插件，也不构建或安装：

```powershell
pwsh -NoProfile -File ./tests/build/Verify-BuildSeparation.ps1 -ReleaseDll ./src/NodeGroups.Runtime/bin/Release/net6.0/NodeGroups.dll -TestDll ./tests/NodeGroups.Runtime.Tests/bin/Release/net6.0/NodeGroups.dll -ReportPath ./TestResults/build-separation/report.json
```

| 参数 | 作用 |
| --- | --- |
| `-ReleaseDll` | 必填，产品工程生成的发行 DLL。 |
| `-TestDll` | 必填，独立运行时测试工程生成的 DLL。 |
| `-ExpectedVersion` | 可选，默认读取根目录 `manifest.json`；三段包版本按程序集的四段版本比较。 |
| `-ReportPath` | 可选，JSON 报告路径；自动创建父目录。 |

两版必须名为 `NodeGroups`，版本匹配包清单，程序集信息版本一致，输出路径不同。可取得的源码标识包含在信息版本中；没有 Git 元数据时不要求额外源码标识。`NodeGroups.BuildFlavor` 属性必须分别为 `Release`、`RuntimeTests`。

发行程序集不得包含测试命名空间、测试类、夹具、样式检查器、测试专用 `EditorSession.CreateMember`，以及 `NODEGROUPS_TEST_REPORT`、`NODEGROUPS_TEST_FOCUS` 的 IL 字符串。测试程序集必须包含功能套件、运行器、辅助类型和环境入口。测试专用 Unity 图像模块不得出现在发行引用中；测试版必须引用用于截图编码的 `UnityEngine.ImageConversionModule`。未使用的 `ScreenCaptureModule` 不要求保留。

检查失败或程序集不可读时，脚本先写报告再抛出异常。独立 `pwsh -File` 调用返回退出码 1；通过 `&` 调用的脚本可用 `catch/finally` 清理。成功正常返回，不修改调用者退出码状态。

报告包含各项检查、SHA-256、程序集身份、信息版本、源码标识（无法取得时为空）、引用、已编译类型及实际构建类型。`testBuild` 从检查到的属性计算，不根据输入参数名称推断。此检查证明交付边界，功能行为仍需单元测试和 Unity 测试。

## 部署保护

[Verify-DeploymentGuards.ps1](Verify-DeploymentGuards.ps1) 检查实际部署脚本，要求已构建两版 DLL，并具备 [开发与测试指南](../../DEVELOPMENT.md) 所述的本地应用环境：

```powershell
pwsh -NoProfile -File ./tests/build/Verify-DeploymentGuards.ps1 -ReportPath ./TestResults/deployment-guards.json
```

唯一参数 `-ReportPath` 为可选 JSON 输出路径。脚本检查测试版正常安装被拒绝、部署锁占用时发行安装被拒绝；随后通过 `test-runtime.ps1 -SkipBuild` 启动独立测试进程，触发真实的一秒超时。该超时是预期结果，其他启动或构建失败不能当作通过。

检查同时覆盖父进程环境变量还原、原 DLL/manifest/Profile 的存在状态和 SHA-256、版本目录状态及锁释放。临时测试只管理自己的子进程，`finally` 还原安装并写入 `deployment-restore.json`。任一检查失败会抛出异常；可选报告记录文件前后哈希、检查结果和超时运行目录。

测试期间不要手工修改部署文件或当前 Profile；部署锁只约束使用同一锁的脚本入口。测试报告和备份不应提交到公开仓库。
