#Requires -Version 7.0
<#
.SYNOPSIS
Checks rejected test installation, deployment locking and timeout restoration.
.DESCRIPTION
Run after both projects have been built. Uses one short-lived independent game
process, deliberately times it out, and verifies exact installation/Profile and
process-environment restoration. Only a caught expected timeout counts as PASS.
#>
[CmdletBinding()]
param([string]$ReportPath)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$repositoryDirectory=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$gameDirectory=[IO.Path]::GetFullPath((Join-Path $repositoryDirectory '../..'))
. (Join-Path $repositoryDirectory 'build/ModDeployment.ps1')
$version=(Get-Content (Join-Path $repositoryDirectory 'manifest.json') -Raw|ConvertFrom-Json).version_number
$profileName=(Get-Content (Join-Path $gameDirectory 'ActiveProfile.txt') -Raw).Trim()
$modDirectory=Join-Path $gameDirectory "mods/NodeGroups/$version"
$paths=@((Join-Path $modDirectory 'NodeGroups.dll'),(Join-Path $modDirectory 'manifest.json'),(Join-Path $gameDirectory "profiles/$profileName/modconfig.json"))
$checks=[Collections.Generic.List[object]]::new()
function Assert-Guard([bool]$condition,[string]$name) {
    $checks.Add([pscustomobject]@{name=$name;passed=$condition})
    if(-not $condition){throw "Deployment guard assertion failed: $name"}
    Write-Host "PASS: $name"
}
function Read-State {
    @($paths | ForEach-Object {
        [pscustomobject]@{path=$_;exists=(Test-Path -LiteralPath $_);sha256=$(if(Test-Path -LiteralPath $_){(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}else{$null})}
    })
}
$before=Read-State
$directoryBefore=Test-Path -LiteralPath $modDirectory
$environmentBefore=@{}
foreach($name in @('NODEGROUPS_TEST_REPORT','NODEGROUPS_TEST_FOCUS')){$environmentBefore[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
$deploymentLock=$null
$errorText=$null
$timeoutDirectory=$null
try {
    $rejected=$false
    try { & (Join-Path $repositoryDirectory 'build.ps1') -TestBuild -Install }
    catch { $rejected=$_.Exception.Message -like '*Test builds cannot*'; if(-not $rejected){throw} }
    Assert-Guard $rejected 'Ordinary installation rejects the runtime-test build before deployment.'

    $deploymentLock=Enter-NodeGroupsDeployment -RepositoryDirectory $repositoryDirectory
    try {
        $rejected=$false
        try { & (Join-Path $repositoryDirectory 'build.ps1') -Install }
        catch { $rejected=$_.Exception.Message -like '*deployment lock*'; if(-not $rejected){throw} }
        Assert-Guard $rejected 'Production installation refuses to overlap a held deployment lock.'

        # 在部署锁失败的路径验证“未设置”和空字符串，避免只验证非空哨兵而漏掉环境污染。
        # 此处不会启动应用；原有真实超时测试仍单独覆盖子进程启动后的恢复。
        foreach($state in @('absent','empty')) {
            foreach($name in $environmentBefore.Keys) {
                if($state -eq 'absent'){[Environment]::SetEnvironmentVariable($name,[NullString]::Value,'Process')}
                else{[Environment]::SetEnvironmentVariable($name,'','Process')}
            }
            $supportsState=$state -eq 'absent' -or $null -ne [Environment]::GetEnvironmentVariable('NODEGROUPS_TEST_FOCUS','Process')
            if(-not $supportsState){continue} # 旧版 .NET 将空字符串等同于删除，无法单独表达此状态。
            $rejected=$false
            try { & (Join-Path $repositoryDirectory 'test-runtime.ps1') -SkipBuild }
            catch { $rejected=$_.Exception.Message -like '*deployment lock*'; if(-not $rejected){throw} }
            Assert-Guard $rejected "Runtime verification refuses a held deployment lock with $state parent variables."
            foreach($name in $environmentBefore.Keys) {
                $actual=[Environment]::GetEnvironmentVariable($name,'Process')
                $restored=if($state -eq 'absent'){$null -eq $actual}else{$null -ne $actual -and $actual.Length -eq 0}
                Assert-Guard $restored "Rejected runtime verification preserves $state parent variable $name."
            }
        }
    } finally { $deploymentLock.Dispose(); $deploymentLock=$null }

    # A previous native nonzero status must not falsely reject metadata verification.
    $LASTEXITCODE=1
    [Environment]::SetEnvironmentVariable('NODEGROUPS_TEST_REPORT','parent-report-sentinel','Process')
    [Environment]::SetEnvironmentVariable('NODEGROUPS_TEST_FOCUS','palette','Process')
    $timedOut=$false
    try { & (Join-Path $repositoryDirectory 'test-runtime.ps1') -SkipBuild -TimeoutSeconds 1 }
    catch {
        $timedOut=$_.Exception.Message -like '*Runtime test timed out*'
        if(-not $timedOut){throw}
        $timeoutDirectory=($_.Exception.Message -replace '^[\s\S]*Report directory:\s*','').Trim()
    }
    Assert-Guard $timedOut 'A real child-process timeout reaches cleanup rather than a stale LASTEXITCODE rejection.'
    Assert-Guard ([Environment]::GetEnvironmentVariable('NODEGROUPS_TEST_REPORT','Process') -eq 'parent-report-sentinel') 'The parent report variable survives the failed test.'
    Assert-Guard ([Environment]::GetEnvironmentVariable('NODEGROUPS_TEST_FOCUS','Process') -eq 'palette') 'The parent focus variable survives the failed test.'
    $restore=Get-Content -LiteralPath (Join-Path $timeoutDirectory 'deployment-restore.json') -Raw|ConvertFrom-Json
    Assert-Guard ($restore.success -and $restore.errors.Count -eq 0) 'The failed test records successful deployment restoration.'
    Assert-Guard ((Test-Path -LiteralPath $modDirectory) -eq $directoryBefore) 'The version-directory existence matches its original state.'
    $after=Read-State
    for($i=0;$i -lt $before.Count;$i++){
        Assert-Guard ($before[$i].exists -eq $after[$i].exists) "Original file existence is restored: $($before[$i].path)"
        Assert-Guard ($before[$i].sha256 -eq $after[$i].sha256) "Original file bytes are restored: $($before[$i].path)"
    }
    $deploymentLock=Enter-NodeGroupsDeployment -RepositoryDirectory $repositoryDirectory
    Assert-Guard ($null -ne $deploymentLock) 'Cleanup releases the deployment lock for the next run.'
} catch {
    $errorText=$_.Exception.Message
    throw
} finally {
    if($null -ne $deploymentLock){$deploymentLock.Dispose()}
    foreach($name in $environmentBefore.Keys){
        if($null -eq $environmentBefore[$name]){[Environment]::SetEnvironmentVariable($name,[NullString]::Value,'Process')}
        else{[Environment]::SetEnvironmentVariable($name,$environmentBefore[$name],'Process')}
    }
    if($ReportPath){
        $absolute=[IO.Path]::GetFullPath($ReportPath)
        [IO.Directory]::CreateDirectory((Split-Path $absolute))|Out-Null
        [ordered]@{success=($null -eq $errorText);checks=@($checks);timeoutDirectory=$timeoutDirectory;before=$before;after=(Read-State);error=$errorText}|
            ConvertTo-Json -Depth 8|Set-Content -LiteralPath $absolute -Encoding utf8NoBOM
    }
}
