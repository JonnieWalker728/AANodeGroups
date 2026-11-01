#Requires -Version 7.0
<#
.SYNOPSIS
构建并运行隔离的 Unity 集成测试，结束后恢复原安装和 Profile。
.DESCRIPTION
默认构建发行与测试两个独立项目，并检查实际 PE 元数据以验证构建边界。
临时部署前按字节备份 DLL、manifest 和当前 Profile，所有退出路径均尝试恢复。
测试环境变量只在启动子进程期间设置，随后恢复调用者环境。
SkipBuild 仍会验证现有二进制；Focus 默认 Full，不继承外部环境中的测试筛选。
.PARAMETER TimeoutSeconds
本次隐藏测试进程的最长运行秒数；超时后仅终止脚本启动的该进程并恢复部署。
.PARAMETER Focus
测试范围：Full 为全套，Hover/Visual/Palette/Gestures 分别验证相应交互场景，Stress 运行独立压力与性能测量，SaveState 验证保存状态和退出确认，QuitConfirm 独立验证真实确认退出，Trash 验证原生回收站计数、取回及撤销联动。
.PARAMETER SkipBuild
跳过构建但仍验证发行与测试 DLL 的隔离和版本，适用于已构建且源码未变化的情况。
#>
[CmdletBinding()]
param(
    [ValidateRange(1,3600)][int]$TimeoutSeconds = 180,
    [ValidateSet('Full','Hover','Visual','Palette','Gestures','Stress','SaveState','QuitConfirm','Trash')][string]$Focus = 'Full',
    [switch]$SkipBuild
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'build/ModDeployment.ps1')
$gameDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$releaseVersion = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'manifest.json') -Raw | ConvertFrom-Json).version_number
if ($releaseVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release version in manifest.' }
$profileName = (Get-Content -LiteralPath (Join-Path $gameDirectory 'ActiveProfile.txt') -Raw).Trim()
if ([string]::IsNullOrWhiteSpace($profileName) -or $profileName -in @('.','..') -or
    $profileName.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) { throw 'Invalid active profile name.' }
$profilePath = [IO.Path]::GetFullPath((Join-Path $gameDirectory "profiles/$profileName/modconfig.json"))
$modDirectory = [IO.Path]::GetFullPath((Join-Path $gameDirectory "mods/NodeGroups/$releaseVersion"))
$modRoot = [IO.Path]::GetFullPath((Join-Path $gameDirectory 'mods/NodeGroups')) + [IO.Path]::DirectorySeparatorChar
if (-not $modDirectory.StartsWith($modRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe temporary deployment directory.' }
$directory = Join-Path $PSScriptRoot ('TestResults/runtime-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))
[IO.Directory]::CreateDirectory($directory) | Out-Null
$reportPath = Join-Path $directory 'report.json'
$boundaryReportPath = Join-Path $directory 'build-boundary.json'
$releaseDll = Join-Path $PSScriptRoot 'src/NodeGroups.Runtime/bin/Release/net6.0/NodeGroups.dll'
$testDll = Join-Path $PSScriptRoot 'tests/NodeGroups.Runtime.Tests/bin/Release/net6.0/NodeGroups.dll'
$deploymentLock = $null
$gameProcess = $null
$snapshots = @()
$restorationErrors = [Collections.Generic.List[string]]::new()
$quitRecoveryCleanup = $null
$testFailure = $null
$modDirectoryExisted = Test-Path -LiteralPath $modDirectory
$environmentBefore = @{}
foreach ($name in @('NODEGROUPS_TEST_REPORT','NODEGROUPS_TEST_FOCUS')) {
    $environmentBefore[$name] = [Environment]::GetEnvironmentVariable($name,'Process')
}
$allowedPaths = @((Join-Path $modDirectory 'NodeGroups.dll'),(Join-Path $modDirectory 'manifest.json'),$profilePath)
try {
    $deploymentLock = Enter-NodeGroupsDeployment -RepositoryDirectory $PSScriptRoot
    $modDirectoryExisted = Test-Path -LiteralPath $modDirectory
    if (-not $SkipBuild) {
        & (Join-Path $PSScriptRoot 'build.ps1')
        & (Join-Path $PSScriptRoot 'build.ps1') -TestBuild
    }
    # 只读取 PE 元数据，不把 BepInEx 或 Unity 程序集加载进 PowerShell 进程。
    & (Join-Path $PSScriptRoot 'tests/build/Verify-BuildSeparation.ps1') -ReleaseDll $releaseDll -TestDll $testDll -ExpectedVersion $releaseVersion -ReportPath $boundaryReportPath
    $boundary = Get-Content -LiteralPath $boundaryReportPath -Raw | ConvertFrom-Json
    if (-not $boundary.success) { throw "Invalid build boundary. Inspect $boundaryReportPath" }
    foreach ($path in $allowedPaths) {
        $exists = Test-Path -LiteralPath $path
        $bytes = if ($exists) { [IO.File]::ReadAllBytes($path) } else { $null }
        $sha256 = if ($exists) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$bytes)) } else { $null }
        $snapshot = [pscustomobject]@{Path=$path;Existed=$exists;Bytes=$bytes;SHA256=$sha256}
        $snapshots += $snapshot
        if ($exists) {
            # 保留恢复副本，便于异常 IO 导致自动恢复失败时手动还原。
            $backupName = if ($path -eq $profilePath) { 'profile-modconfig.before' } else { [IO.Path]::GetFileName($path) + '.before' }
            [IO.File]::WriteAllBytes((Join-Path $directory $backupName),[byte[]]$bytes)
        }
    }
    $profile = Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json
    [IO.Directory]::CreateDirectory($modDirectory) | Out-Null
    Copy-Item -LiteralPath $testDll -Destination (Join-Path $modDirectory 'NodeGroups.dll') -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'manifest.json') -Destination (Join-Path $modDirectory 'manifest.json') -Force
    $profile.EnabledMods = @($profile.EnabledMods | Where-Object name -ne 'NodeGroups') + @([pscustomobject]@{name='NodeGroups';version=$releaseVersion})
    [IO.File]::WriteAllText($profilePath,($profile | ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
    try {
        [Environment]::SetEnvironmentVariable('NODEGROUPS_TEST_REPORT',$reportPath,'Process')
        # Full 必须删除筛选变量，不能在新版 PowerShell/.NET 下留下空字符串。
        if ($Focus -eq 'Full') {
            [Environment]::SetEnvironmentVariable('NODEGROUPS_TEST_FOCUS',[NullString]::Value,'Process')
        } else {
            [Environment]::SetEnvironmentVariable('NODEGROUPS_TEST_FOCUS',$Focus.ToLowerInvariant(),'Process')
        }
        Write-Host "Running isolated $Focus tests; the original installation and Profile will be restored."
        $gameProcess = Start-Process -FilePath (Join-Path $gameDirectory 'AzureArchive.exe') -ArgumentList @('-logFile',('"' + (Join-Path $directory 'Player.log') + '"')) -WorkingDirectory $gameDirectory -WindowStyle Hidden -PassThru
    } finally {
        foreach ($name in $environmentBefore.Keys) {
            if ($null -eq $environmentBefore[$name]) {
                [Environment]::SetEnvironmentVariable($name,[NullString]::Value,'Process')
            } else {
                [Environment]::SetEnvironmentVariable($name,$environmentBefore[$name],'Process')
            }
        }
    }
    if (-not $gameProcess.WaitForExit($TimeoutSeconds * 1000)) { throw "Runtime test timed out. Report directory: $directory" }
    if (-not (Test-Path -LiteralPath $reportPath)) { throw "No integration report (exit $($gameProcess.ExitCode)). Inspect $directory/Player.log" }
    $result = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if (-not $result.success) { throw $result.error }
    if ($gameProcess.ExitCode -ne 0) { throw "Test process exited with code $($gameProcess.ExitCode)." }
    # 报告必须来自本次验证过的测试构建，不能把旧报告或发行 DLL 当成通过依据。
    if (-not $result.testBuild -or $result.pluginVersion -ne $releaseVersion -or
        $result.buildVersion -ne $boundary.test.informationalVersion) { throw 'The report did not originate from the verified runtime-test DLL.' }
    if ($Focus -eq 'QuitConfirm') {
        # 关闭前批准标记还须匹配已经正常退出的同一 PID；单独的子进程报告不能证明退出。
        $quitEvidence = Get-Content -LiteralPath (Join-Path $directory 'quit-confirmation.json') -Raw | ConvertFrom-Json
        if (-not $quitEvidence.processExitRequired -or
            -not $quitEvidence.VetoObserved -or -not $quitEvidence.ConfirmClicked -or -not $quitEvidence.ApprovedQueryObserved -or
            $quitEvidence.processId -ne $gameProcess.Id -or $quitEvidence.dllSha256 -ne $boundary.test.sha256 -or
            $quitEvidence.pluginVersion -ne $releaseVersion) { throw 'Real quit confirmation evidence is incomplete or from another process/build.' }
        # 上方 WaitForExit 与退出码检查均已通过，现在才补充最终成功证据。
        $quitEvidence.success = $true
        $quitEvidence.error = $null
        $quitEvidence | Add-Member -NotePropertyName processExitConfirmed -NotePropertyValue $true -Force
        $quitEvidence | Add-Member -NotePropertyName exitCode -NotePropertyValue $gameProcess.ExitCode -Force
        $quitEvidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $directory 'quit-confirmation.json') -Encoding utf8NoBOM
    }
    Write-Host "PASS: $($result.passed.Count) runtime assertions. $reportPath"
} catch {
    $testFailure = $_.Exception.Message
    throw
} finally {
    # 仅停止本脚本启动的进程；逐文件独立恢复，某个文件失败不会阻止其余文件还原。
    if ($null -ne $gameProcess -and -not $gameProcess.HasExited) {
        try { Stop-Process -Id $gameProcess.Id -ErrorAction Stop; $gameProcess.WaitForExit() }
        catch { $restorationErrors.Add("Test process shutdown failed: $($_.Exception.Message)") }
    }
    if ($Focus -eq 'QuitConfirm' -and $null -ne $gameProcess -and (Test-Path -LiteralPath (Join-Path $directory 'quit-confirmation.json'))) {
        try {
            $quitEvidence = Get-Content -LiteralPath (Join-Path $directory 'quit-confirmation.json') -Raw | ConvertFrom-Json
            $fixtureIdentity = [guid]::Empty
            if ($quitEvidence.processId -ne $gameProcess.Id -or -not $quitEvidence.recoveryInitiallyAbsent -or
                -not [guid]::TryParse($quitEvidence.projectId, [ref]$fixtureIdentity)) { throw 'Untrusted quit-fixture cleanup identity.' }
            $recoveryRoot = [IO.Path]::GetFullPath((Join-Path $gameDirectory 'ModDev/NodeGroups/artifacts/recovery')) + [IO.Path]::DirectorySeparatorChar
            $identityHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($quitEvidence.projectId)))
            $recoveryPath = [IO.Path]::GetFullPath((Join-Path $recoveryRoot ($identityHash + '.project.nodegroups.json')))
            if (-not $recoveryPath.StartsWith($recoveryRoot, [StringComparison]::OrdinalIgnoreCase) -or
                $recoveryPath -ne [IO.Path]::GetFullPath($quitEvidence.recoveryPath)) { throw 'Unsafe quit-fixture recovery path.' }
            $existed = Test-Path -LiteralPath $recoveryPath
            if ($existed) {
                $recovery = Get-Content -LiteralPath $recoveryPath -Raw | ConvertFrom-Json
                if ($recovery.projectId -cne $quitEvidence.projectId -or
                    @($recovery.groups | Where-Object groupId -EQ $quitEvidence.groupId).Count -ne 1) { throw 'Quit recovery does not belong to this generated fixture.' }
                Copy-Item -LiteralPath $recoveryPath -Destination (Join-Path $directory 'quit-recovery.before')
                Remove-Item -LiteralPath $recoveryPath -Force
            }
            $quitRecoveryCleanup = [ordered]@{success=$true;path=$recoveryPath;existedAfterQuit=$existed;existsAfterCleanup=(Test-Path -LiteralPath $recoveryPath)}
            if ($quitRecoveryCleanup.existsAfterCleanup) { throw 'Quit fixture recovery cleanup did not complete.' }
        } catch { $restorationErrors.Add("Quit fixture recovery cleanup failed: $($_.Exception.Message)") }
    }
    foreach ($snapshot in $snapshots) {
        try {
            if ($snapshot.Path -notin $allowedPaths) { throw 'Unexpected restore target.' }
            if ($snapshot.Existed) { [IO.File]::WriteAllBytes($snapshot.Path,[byte[]]$snapshot.Bytes) }
            elseif (Test-Path -LiteralPath $snapshot.Path) { Remove-Item -LiteralPath $snapshot.Path -Force }
            $existsNow = Test-Path -LiteralPath $snapshot.Path
            if ($existsNow -ne $snapshot.Existed) { throw 'File existence was not restored.' }
            if ($existsNow -and (Get-FileHash -LiteralPath $snapshot.Path -Algorithm SHA256).Hash -ne $snapshot.SHA256) { throw 'Original file bytes were not restored.' }
        } catch { $restorationErrors.Add("$($snapshot.Path): $($_.Exception.Message)") }
    }
    # 只删除本次新建且仍为空的版本目录，不递归清理任何现存内容。
    if (-not $modDirectoryExisted -and $snapshots.Count -gt 0 -and (Test-Path -LiteralPath $modDirectory)) {
        try {
            if ([IO.Directory]::GetFileSystemEntries($modDirectory).Length -eq 0) { Remove-Item -LiteralPath $modDirectory }
        } catch { $restorationErrors.Add("Empty temporary directory cleanup failed: $($_.Exception.Message)") }
    }
    foreach ($name in $environmentBefore.Keys) {
        try {
            if ($null -eq $environmentBefore[$name]) {
                [Environment]::SetEnvironmentVariable($name,[NullString]::Value,'Process')
            } else {
                [Environment]::SetEnvironmentVariable($name,$environmentBefore[$name],'Process')
            }
        }
        catch { $restorationErrors.Add("Environment restore failed: $name") }
    }
    # 恢复结果单独记录；测试失败和清理失败都必须对调用者可见。
    $restoreEvidence = [ordered]@{
        success=$restorationErrors.Count -eq 0
        originalModDirectoryExisted=$modDirectoryExisted
        restoredModDirectoryExists=(Test-Path -LiteralPath $modDirectory)
        files=@($snapshots | Select-Object Path,Existed,SHA256)
        quitRecoveryCleanup=$quitRecoveryCleanup
        errors=@($restorationErrors)
    }
    try {
        $restoreEvidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $directory 'deployment-restore.json') -Encoding utf8NoBOM
    } catch {
        $restorationErrors.Add("Restore evidence could not be written: $($_.Exception.Message)")
    } finally { if ($null -ne $deploymentLock) { $deploymentLock.Dispose() } }
    if ($restorationErrors.Count -gt 0) { throw ("Temporary deployment cleanup failed. Original test failure: $testFailure. Recovery copies: $directory. " + ($restorationErrors -join '; ')) }
}
