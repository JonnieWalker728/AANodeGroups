<#
.SYNOPSIS
还原依赖并验证 NodeGroups 托管逻辑，无需启动 AzureArchive。
.DESCRIPTION
使用仓库 NuGet 配置与本地 SDK/包缓存，依次运行 Core 和 Storage 测试。
依赖还原与测试串行执行，任一步失败即终止；原生编辑器/IL2CPP 集成验证由 test-runtime.ps1 承担。
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryDirectory = [IO.Path]::GetFullPath($PSScriptRoot)
$configurationPath = Join-Path $repositoryDirectory 'NuGet.Config'
$packageDirectory = Join-Path $repositoryDirectory '.nuget\packages'
$resultsDirectory = Join-Path $repositoryDirectory 'TestResults'
$projects = @(
    [pscustomobject]@{
        Name = 'core'
        Path = Join-Path $repositoryDirectory 'tests\NodeGroups.Core.Tests\NodeGroups.Core.Tests.csproj'
    },
    [pscustomobject]@{
        Name = 'storage'
        Path = Join-Path $repositoryDirectory 'tests\NodeGroups.Storage.Tests\NodeGroups.Storage.Tests.csproj'
    }
)

# dotnet 以退出码报告失败，统一转成异常，让外层 finally 总能恢复环境变量。
function Invoke-CheckedDotNet {
    param(
        [Parameter(Mandatory = $true)][string]$Step,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )
    Write-Host $Step
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Step failed (dotnet exit code $LASTEXITCODE). No subsequent validation was run."
    }
}

$environmentBefore = @{}
$localEnvironment = @{
    DOTNET_CLI_HOME = Join-Path $repositoryDirectory '.dotnet-cli'
    NUGET_PACKAGES = $packageDirectory
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_NOLOGO = '1'
}

try {
    Get-Command dotnet -ErrorAction Stop | Out-Null
    if (-not (Test-Path -LiteralPath $configurationPath -PathType Leaf)) {
        throw "Local NuGet configuration is missing: $configurationPath"
    }
    foreach ($entry in $localEnvironment.GetEnumerator()) {
        $environmentBefore[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
    New-Item -ItemType Directory -Path $resultsDirectory -Force | Out-Null

    # 先还原两个项目，再串行测试，避免并行构建争用 Core 输出文件。
    foreach ($project in $projects) {
        Invoke-CheckedDotNet -Step "Restore NodeGroups $($project.Name) tests" -Arguments @(
            'restore', $project.Path, '--configfile', $configurationPath, '--packages', $packageDirectory
        )
    }
    foreach ($project in $projects) {
        $projectResults = Join-Path $resultsDirectory $project.Name
        Invoke-CheckedDotNet -Step "Verify NodeGroups $($project.Name) (Release)" -Arguments @(
            'test', $project.Path, '-c', 'Release', '--no-restore',
            '--logger', "trx;LogFileName=$($project.Name)-release.trx", '--results-directory', $projectResults
        )
    }
    Write-Host "Managed verification passed. TRX results: $resultsDirectory"
}
finally {
    # 即使依赖还原或测试失败，也恢复调用终端原有的进程环境。
    foreach ($entry in $environmentBefore.GetEnumerator()) {
        # 明确传入空引用：新版 PowerShell/.NET 中普通 $null 可能被转换为空字符串。
        if ($null -eq $entry.Value) {
            [Environment]::SetEnvironmentVariable($entry.Key, [NullString]::Value, 'Process')
        } else {
            [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
        }
    }
}
