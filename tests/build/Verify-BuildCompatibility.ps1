#Requires -Version 5.1
<#
.SYNOPSIS
在独立临时工程中验证构建入口的 Windows PowerShell 5.1 / PowerShell 7 契约。
.DESCRIPTION
直接复制实际构建脚本，不改变其编码。使用 dotnet 命令夹具模拟 SDK，
所有安装与启用操作仅作用于 TestResults 内的假应用，不启动或修改真实应用。
失败保留工程及报告，成功仅清理本次创建的假应用。真实 SDK 构建仍需单独验证。
.PARAMETER ReportPath
可选 JSON 结果路径，默认写入本次 TestResults/build-compatibility 目录。
#>
[CmdletBinding()]
param([string]$ReportPath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$resultsRoot = Join-Path $repository 'TestResults/build-compatibility'
$runDirectory = Join-Path $resultsRoot ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))
$fixtureRoot = Join-Path $runDirectory 'fixtures'
$checks = [Collections.Generic.List[object]]::new()
$notApplicable = [Collections.Generic.List[object]]::new()
$emptyStringEnvironmentSupported = $null
$utf8 = [Text.UTF8Encoding]::new($false)
$strictUtf8 = [Text.UTF8Encoding]::new($false,$true)
$failure = $null
$cleanupError = $null
$environmentNames = @('DOTNET_CLI_HOME','NUGET_PACKAGES','DOTNET_CLI_TELEMETRY_OPTOUT','DOTNET_NOLOGO')
$incomingEnvironment = @{}
foreach ($variableName in $environmentNames) {
    $incomingEnvironment[$variableName] = [Environment]::GetEnvironmentVariable($variableName,'Process')
}
$incomingExitCode = Get-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
$incomingExitCodeExists = $null -ne $incomingExitCode
$incomingExitCodeValue = if ($incomingExitCodeExists) { $incomingExitCode.Value } else { $null }
$parentEnvironment = @{
    DOTNET_CLI_HOME = 'parent SDK cache 中文'
    NUGET_PACKAGES = $null
    DOTNET_CLI_TELEMETRY_OPTOUT = 'parent-telemetry'
    DOTNET_NOLOGO = $null
}

# 对象由父测试作用域拥有。子脚本调用命令时修改对象成员，不使用会指向子脚本的 $script:。
$buildCompatibilityContext = @{
    Calls = [Collections.Generic.List[object]]::new()
    RestoreExitCode = 0
    BuildExitCode = 0
}

function Assert-Compatibility {
    param([bool]$Condition,[string]$Name)
    $checks.Add([pscustomobject]@{name=$Name;passed=$Condition})
    if (-not $Condition) { throw "Build compatibility assertion failed: $Name" }
    Write-Host "PASS: $Name"
}

# 仅覆盖本测试作用域的命令查找；不修改 PATH、SDK 文件或调用者的全局函数。
function dotnet {
    $arguments = @($args)
    $observed = @{}
    foreach ($variableName in $environmentNames) {
        $observed[$variableName] = [Environment]::GetEnvironmentVariable($variableName,'Process')
    }
    $buildCompatibilityContext.Calls.Add([pscustomobject]@{arguments=$arguments;environment=$observed})
    if ($arguments[0] -eq 'restore') { $global:LASTEXITCODE = $buildCompatibilityContext.RestoreExitCode }
    elseif ($arguments[0] -eq 'build') { $global:LASTEXITCODE = $buildCompatibilityContext.BuildExitCode }
    else { throw "Unexpected SDK invocation: $($arguments -join ' ')" }
}

function Reset-CommandFixture {
    $buildCompatibilityContext.Calls.Clear()
    $buildCompatibilityContext.RestoreExitCode = 0
    $buildCompatibilityContext.BuildExitCode = 0
    foreach ($variableName in $environmentNames) {
        $value = $parentEnvironment[$variableName]
        if ($null -eq $value) { [Environment]::SetEnvironmentVariable($variableName,[NullString]::Value,'Process') }
        else { [Environment]::SetEnvironmentVariable($variableName,$value,'Process') }
    }
}

function Assert-EnvironmentRestored {
    param([string]$CaseName)
    foreach ($variableName in $environmentNames) {
        Assert-Compatibility ([Environment]::GetEnvironmentVariable($variableName,'Process') -eq $parentEnvironment[$variableName]) "$CaseName restores parent environment $variableName."
    }
}

function New-BuildFixture {
    param([string]$CaseName)
    $game = Join-Path $fixtureRoot ($CaseName + '/应用目录 中文')
    $project = Join-Path $game 'ModDev/AANodeGroups'
    $profileName = '开发配置 中文'
    $profilePath = Join-Path $game ('profiles/' + $profileName + '/modconfig.json')
    [IO.Directory]::CreateDirectory((Join-Path $project 'build')) | Out-Null
    [IO.Directory]::CreateDirectory((Split-Path -Parent $profilePath)) | Out-Null
    # 保留产品文件原始字节，不能通过重编码副本掩盖 5.1 解析问题。
    foreach ($relative in @('build.ps1','build/ModDeployment.ps1','manifest.json','NuGet.Config')) {
        Copy-Item -LiteralPath (Join-Path $repository $relative) -Destination (Join-Path $project $relative)
    }
    [IO.File]::WriteAllText((Join-Path $game 'ActiveProfile.txt'),$profileName,$utf8)
    $configuration = [ordered]@{
        EnabledMods = @([ordered]@{name='已有模组';version='9.1.0';note='不可丢失'},[ordered]@{name='NodeGroups';version='0.0.1'})
        Comment = '需要保留的中文配置'
        Additional = [ordered]@{language='简体中文';enabled=$true}
    }
    [IO.File]::WriteAllText($profilePath,($configuration | ConvertTo-Json -Depth 8),$utf8)
    $version = (Get-Content -LiteralPath (Join-Path $project 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json).version_number
    $release = Join-Path $project 'src/NodeGroups.Runtime/bin/Release/net6.0'
    $testOutput = Join-Path $project 'tests/NodeGroups.Runtime.Tests/bin/Release/net6.0'
    foreach ($directory in @($release,$testOutput)) { [IO.Directory]::CreateDirectory($directory) | Out-Null }
    [IO.File]::WriteAllText((Join-Path $release 'NodeGroups.dll'),'release-dll-marker',$utf8)
    [IO.File]::WriteAllText((Join-Path $release 'icon.png'),'release-icon-marker',$utf8)
    [IO.File]::WriteAllText((Join-Path $testOutput 'NodeGroups.dll'),'test-dll-must-not-be-installed',$utf8)
    $installed = Join-Path $game ('mods/NodeGroups/' + $version)
    [IO.Directory]::CreateDirectory($installed) | Out-Null
    foreach ($name in @('NodeGroups.dll','manifest.json','icon.png')) {
        [IO.File]::WriteAllText((Join-Path $installed $name),('old-install-' + $name),$utf8)
    }
    return [pscustomobject]@{
        name=$CaseName;game=$game;project=$project;script=(Join-Path $project 'build.ps1')
        profile=$profilePath;version=$version;installed=$installed
        protectedPaths=@($profilePath,(Join-Path $installed 'NodeGroups.dll'),(Join-Path $installed 'manifest.json'),(Join-Path $installed 'icon.png'))
    }
}

function Read-ProtectedState {
    param($Fixture)
    return @($Fixture.protectedPaths | ForEach-Object {
        [pscustomobject]@{path=$_;sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}
    })
}

function Assert-ProtectedState {
    param($Fixture,$Before)
    foreach ($entry in $Before) {
        Assert-Compatibility ((Test-Path -LiteralPath $entry.path) -and (Get-FileHash -LiteralPath $entry.path -Algorithm SHA256).Hash -eq $entry.sha256) "$($Fixture.name) leaves protected file unchanged: $([IO.Path]::GetFileName($entry.path))."
    }
}

function Assert-ScriptFailure {
    param([scriptblock]$Action,[string]$MessagePattern,[string]$Name)
    $caught = $null
    try { & $Action }
    catch { $caught = $_.Exception.Message }
    Assert-Compatibility ($null -ne $caught -and $caught -match $MessagePattern) $Name
}

function Assert-LockReleased {
    param($Fixture)
    $directory = Join-Path $Fixture.project 'TestResults'
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $stream = $null
    try {
        $stream = [IO.File]::Open((Join-Path $directory 'mod-deployment.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        Assert-Compatibility ($null -ne $stream) "$($Fixture.name) releases its deployment lock."
    } finally { if ($null -ne $stream) { $stream.Dispose() } }
}

function Assert-BuildInvocation {
    param($Fixture,[bool]$TestBuild)
    $calls = $buildCompatibilityContext.Calls
    $projectName = if ($TestBuild) { 'tests/NodeGroups.Runtime.Tests/NodeGroups.Runtime.Tests.csproj' } else { 'src/NodeGroups.Runtime/NodeGroups.Runtime.csproj' }
    $expectedProject = Join-Path $Fixture.project $projectName
    Assert-Compatibility ($calls.Count -eq 2 -and $calls[0].arguments[0] -eq 'restore' -and $calls[1].arguments[0] -eq 'build') "$($Fixture.name) invokes restore before build."
    Assert-Compatibility ($calls[0].arguments[1] -eq $expectedProject -and $calls[1].arguments[1] -eq $expectedProject) "$($Fixture.name) uses the correct independent project."
    Assert-Compatibility ($calls[0].arguments -contains '--configfile' -and $calls[0].arguments -contains (Join-Path $Fixture.project 'NuGet.Config')) "$($Fixture.name) supplies the local NuGet configuration."
    Assert-Compatibility ($calls[1].arguments -contains 'Release' -and $calls[1].arguments -contains '--no-restore') "$($Fixture.name) requests a Release build without a second restore."
    foreach ($call in $calls) {
        Assert-Compatibility ($call.environment.DOTNET_CLI_HOME -eq (Join-Path $Fixture.project '.dotnet-cli') -and
            $call.environment.NUGET_PACKAGES -eq (Join-Path $Fixture.project '.nuget\packages') -and
            $call.environment.DOTNET_CLI_TELEMETRY_OPTOUT -eq '1' -and $call.environment.DOTNET_NOLOGO -eq '1') "$($Fixture.name) scopes SDK environment to the fixture during $($call.arguments[0])."
    }
}

try {
    $case = New-BuildFixture 'install-enable'
    Reset-CommandFixture
    & $case.script -Install -Enable
    Assert-BuildInvocation $case $false
    Assert-Compatibility ((Get-Content -LiteralPath (Join-Path $case.installed 'NodeGroups.dll') -Raw -Encoding UTF8) -eq 'release-dll-marker') 'Install copies the release DLL instead of the test DLL.'
    Assert-Compatibility ((Get-Content -LiteralPath (Join-Path $case.installed 'icon.png') -Raw -Encoding UTF8) -eq 'release-icon-marker') 'Install copies the release icon.'
    Assert-Compatibility ((Get-FileHash -LiteralPath (Join-Path $case.installed 'manifest.json')).Hash -eq (Get-FileHash -LiteralPath (Join-Path $case.project 'manifest.json')).Hash) 'Install preserves exact manifest bytes.'
    $bytes = [IO.File]::ReadAllBytes($case.profile)
    $updated = $strictUtf8.GetString($bytes) | ConvertFrom-Json
    Assert-Compatibility (@($updated.EnabledMods | Where-Object name -eq 'NodeGroups').Count -eq 1 -and @($updated.EnabledMods | Where-Object name -eq 'NodeGroups')[0].version -eq $case.version) 'Enable replaces only the NodeGroups entry with the manifest version.'
    $otherMods = @($updated.EnabledMods | Where-Object name -eq '已有模组')
    Assert-Compatibility ($otherMods.Count -eq 1 -and $otherMods[0].version -eq '9.1.0' -and $otherMods[0].note -eq '不可丢失' -and
        $updated.Comment -eq '需要保留的中文配置' -and $updated.Additional.language -eq '简体中文' -and $updated.Additional.enabled) 'Enable retains unrelated mods and non-ASCII configuration values.'
    Assert-Compatibility (-not ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)) 'Enable writes valid UTF-8 without a BOM on both PowerShell editions.'
    Assert-EnvironmentRestored $case.name
    Assert-LockReleased $case

    foreach ($testBuild in @($false,$true)) {
        $caseName = if ($testBuild) { 'test-build-only' } else { 'release-build-only' }
        $case = New-BuildFixture $caseName
        $before = Read-ProtectedState $case
        Reset-CommandFixture
        if ($testBuild) { & $case.script -TestBuild } else { & $case.script }
        Assert-BuildInvocation $case $testBuild
        Assert-ProtectedState $case $before
        Assert-EnvironmentRestored $case.name
    }

    foreach ($entry in @(
        @{name='enable-requires-install';flags=@{Enable=$true};pattern='Enable requires'},
        @{name='test-install-rejected';flags=@{TestBuild=$true;Install=$true};pattern='Test builds cannot'},
        @{name='test-enable-rejected';flags=@{TestBuild=$true;Install=$true;Enable=$true};pattern='Test builds cannot'}
    )) {
        $case = New-BuildFixture $entry.name
        $before = Read-ProtectedState $case
        Reset-CommandFixture
        $flags = $entry.flags
        Assert-ScriptFailure { & $case.script @flags } $entry.pattern "$($case.name) is rejected before SDK invocation."
        Assert-Compatibility ($buildCompatibilityContext.Calls.Count -eq 0) "$($case.name) never invokes the SDK."
        Assert-ProtectedState $case $before
        Assert-EnvironmentRestored $case.name
    }

    $case = New-BuildFixture 'lock-conflict'
    $before = Read-ProtectedState $case
    Reset-CommandFixture
    $lockDirectory = Join-Path $case.project 'TestResults'
    [IO.Directory]::CreateDirectory($lockDirectory) | Out-Null
    $lockPath = Join-Path $lockDirectory 'mod-deployment.lock'
    $ownedLock = [IO.File]::Open($lockPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try {
        Assert-ScriptFailure { & $case.script -Install -Enable } 'deployment lock' 'A held deployment lock rejects install before any SDK invocation.'
        Assert-Compatibility ($buildCompatibilityContext.Calls.Count -eq 0) 'Lock conflict does not invoke the SDK.'
        Assert-ProtectedState $case $before
        Assert-EnvironmentRestored $case.name
    } finally { $ownedLock.Dispose() }
    Assert-LockReleased $case

    foreach ($stage in @('restore','build')) {
        $case = New-BuildFixture ($stage + '-failure')
        $before = Read-ProtectedState $case
        Reset-CommandFixture
        if ($stage -eq 'restore') { $buildCompatibilityContext.RestoreExitCode = 77 }
        else { $buildCompatibilityContext.BuildExitCode = 88 }
        Assert-ScriptFailure { & $case.script -Install -Enable } ("Runtime " + $stage + " failed") "$stage nonzero exit code aborts the build."
        $expectedCalls = if ($stage -eq 'restore') { 1 } else { 2 }
        Assert-Compatibility ($buildCompatibilityContext.Calls.Count -eq $expectedCalls) "$stage failure prevents subsequent SDK stages."
        Assert-ProtectedState $case $before
        Assert-EnvironmentRestored $case.name
        Assert-LockReleased $case
    }

    # .NET Framework 将空串视为删除；较新的 .NET 可保留空串。先测宿主能力，不能伪造等价覆盖。
    $emptyProbeName = 'NODEGROUPS_BUILD_EMPTY_ENV_' + [guid]::NewGuid().ToString('N')
    try {
        [Environment]::SetEnvironmentVariable($emptyProbeName,[string]::Empty,'Process')
        $probeValue = [Environment]::GetEnvironmentVariable($emptyProbeName,'Process')
        $emptyStringEnvironmentSupported = $null -ne $probeValue -and $probeValue.Length -eq 0
    } finally {
        [Environment]::SetEnvironmentVariable($emptyProbeName,[NullString]::Value,'Process')
    }
    if ($emptyStringEnvironmentSupported) {
        $oldCliSentinel = $parentEnvironment.DOTNET_CLI_HOME
        try {
            $parentEnvironment.DOTNET_CLI_HOME = [string]::Empty
            # NUGET_PACKAGES 仍保持真正缺失，验证空串与缺失值能在同一次调用中分别恢复。
            $case = New-BuildFixture 'empty-string-environment'
            $before = Read-ProtectedState $case
            Reset-CommandFixture
            & $case.script
            Assert-EnvironmentRestored ($case.name + ' success')

            Reset-CommandFixture
            $buildCompatibilityContext.BuildExitCode = 88
            Assert-ScriptFailure { & $case.script -Install -Enable } 'Runtime build failed' 'An empty parent variable still propagates a nonzero build exit.'
            Assert-EnvironmentRestored ($case.name + ' failure')
            Assert-ProtectedState $case $before
            Assert-LockReleased $case
        } finally {
            $parentEnvironment.DOTNET_CLI_HOME = $oldCliSentinel
        }
    } else {
        $notApplicable.Add([pscustomobject]@{
            name='empty-string-environment'
            reason='This host treats an empty process environment value as deletion; distinct empty-string restoration is not representable.'
        })
        Write-Host 'NOT APPLICABLE: the host cannot retain an empty process environment string.'
    }
} catch {
    $failure = $_.Exception.ToString()
    throw
} finally {
    foreach ($variableName in $environmentNames) {
        $value = $incomingEnvironment[$variableName]
        if ($null -eq $value) { [Environment]::SetEnvironmentVariable($variableName,[NullString]::Value,'Process') }
        else { [Environment]::SetEnvironmentVariable($variableName,$value,'Process') }
    }
    if ($incomingExitCodeExists) { $global:LASTEXITCODE = $incomingExitCodeValue }
    else { Remove-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue }

    if ($null -eq $failure) {
        try {
            # 只删除本次创建、规范化后仍位于 TestResults/build-compatibility 下的假应用。
            $absoluteFixture = [IO.Path]::GetFullPath($fixtureRoot)
            $safePrefix = [IO.Path]::GetFullPath($resultsRoot) + [IO.Path]::DirectorySeparatorChar
            if (-not $absoluteFixture.StartsWith($safePrefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup path.' }
            if (Test-Path -LiteralPath $absoluteFixture) { Remove-Item -LiteralPath $absoluteFixture -Recurse -Force }
        } catch {
            $cleanupError = $_.Exception.ToString()
        }
    }
    [IO.Directory]::CreateDirectory($runDirectory) | Out-Null
    $report = [ordered]@{
        success=($null -eq $failure -and $null -eq $cleanupError)
        powerShellVersion=$PSVersionTable.PSVersion.ToString()
        edition=$PSVersionTable.PSEdition
        checks=$checks.ToArray()
        emptyStringEnvironmentSupported=$emptyStringEnvironmentSupported
        notApplicable=$notApplicable.ToArray()
        fixture=$fixtureRoot
        error=$failure
        cleanupError=$cleanupError
    }
    $target = if ($ReportPath) { [IO.Path]::GetFullPath($ReportPath) } else { Join-Path $runDirectory 'report.json' }
    [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
    [IO.File]::WriteAllText($target,($report | ConvertTo-Json -Depth 8),$utf8)
    Write-Host "Report: $target"
    if ($null -ne $cleanupError) { throw "Fixture cleanup failed: $cleanupError" }
}
