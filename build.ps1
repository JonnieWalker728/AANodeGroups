#Requires -Version 5.1
<#
.SYNOPSIS
构建 NodeGroups 发行 DLL 或独立运行时测试 DLL，可选安装并启用发行版。
.DESCRIPTION
使用仓库本地 NuGet/SDK 缓存；退出时恢复调用终端的环境变量。
测试构建使用独立项目和输出目录，不能通过此脚本安装，临时测试部署请使用 test-runtime.ps1。
脚本按仓库上两级定位游戏目录，安装与测试共享部署锁。
支持 Windows PowerShell 5.1 与 PowerShell 7；含中文的脚本须保存为带 BOM 的 UTF-8。
.PARAMETER Install
将发行 DLL、manifest.json 和 icon.png 复制到当前版本的 mods/NodeGroups 目录。
.PARAMETER Enable
在当前 Profile 中启用已安装的版本；必须同时指定 Install。
.PARAMETER TestBuild
仅构建独立运行时测试项目；与 Install、Enable 互斥。
#>
[CmdletBinding()]
param(
    [switch]$Install,
    [switch]$Enable,
    # 测试项目与输出目录独立，禁止把测试 DLL 作为发行版安装。
    [switch]$TestBuild
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($Enable -and -not $Install) { throw '-Enable requires -Install.' }
if ($TestBuild -and ($Install -or $Enable)) { throw 'Test builds cannot use -Install or -Enable. Use test-runtime.ps1 for temporary test deployment.' }
$gameDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$releaseVersion = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json).version_number
if ($releaseVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release version in manifest.' }
. (Join-Path $PSScriptRoot 'build/ModDeployment.ps1')
$deploymentLock = if ($Install) { Enter-NodeGroupsDeployment -RepositoryDirectory $PSScriptRoot } else { $null }
$savedEnvironment = @{}
foreach ($name in @('DOTNET_CLI_HOME', 'NUGET_PACKAGES', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_NOLOGO')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
try {
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet-cli'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.nuget\packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$project = if ($TestBuild) {
    Join-Path $PSScriptRoot 'tests\NodeGroups.Runtime.Tests\NodeGroups.Runtime.Tests.csproj'
} else {
    Join-Path $PSScriptRoot 'src\NodeGroups.Runtime\NodeGroups.Runtime.csproj'
}
Write-Host $(if ($TestBuild) { 'Building isolated runtime tests (not for release).' } else { 'Building production release (runtime tests excluded).' })
& dotnet restore $project --configfile (Join-Path $PSScriptRoot 'NuGet.Config')
if ($LASTEXITCODE -ne 0) { throw 'Runtime restore failed.' }
& dotnet build $project -c Release --no-restore --nologo
if ($LASTEXITCODE -ne 0) { throw 'Runtime build failed.' }
if ($Install) {
    $destination = Join-Path $gameDirectory "mods\NodeGroups\$releaseVersion"
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'src\NodeGroups.Runtime\bin\Release\net6.0\NodeGroups.dll') -Destination $destination -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'manifest.json') -Destination $destination -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'src\NodeGroups.Runtime\bin\Release\net6.0\icon.png') -Destination $destination -Force
}
if ($Enable) {
    $profileName = (Get-Content -LiteralPath (Join-Path $gameDirectory 'ActiveProfile.txt') -Raw -Encoding UTF8).Trim()
    if ([string]::IsNullOrWhiteSpace($profileName) -or $profileName -in @('.', '..') -or
        $profileName.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) { throw 'Invalid active profile name.' }
    $profilePath = [IO.Path]::GetFullPath((Join-Path $gameDirectory "profiles\$profileName\modconfig.json"))
    $profile = Get-Content -LiteralPath $profilePath -Raw -Encoding UTF8 | ConvertFrom-Json
    $existing = @($profile.EnabledMods | Where-Object name -ne 'NodeGroups')
    $profile.EnabledMods = @($existing) + @([pscustomobject]@{ name = 'NodeGroups'; version = $releaseVersion })
    [IO.File]::WriteAllText($profilePath, ($profile | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
}
} finally {
    # 构建或安装失败也要释放部署锁，并恢复调用者的进程环境。
    if ($null -ne $deploymentLock) { $deploymentLock.Dispose() }
    foreach ($name in $savedEnvironment.Keys) {
        # 新版 PowerShell/.NET 区分未设置和空字符串；用明确的空引用删除原本不存在的变量。
        if ($null -eq $savedEnvironment[$name]) {
            [Environment]::SetEnvironmentVariable($name, [NullString]::Value, 'Process')
        } else {
            [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
        }
    }
}
