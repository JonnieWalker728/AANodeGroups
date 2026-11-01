#Requires -Version 7.0
<#
.SYNOPSIS
    Verifies that the shipping DLL excludes the opt-in Unity test harness.
.DESCRIPTION
    Reads PE/CLI metadata and method IL without Assembly.Load, so Unity, BepInEx,
    and game assemblies need not be present or loaded. This script never builds,
    installs, enables, or executes either assembly. A nonzero exit code means the
    build boundary is invalid. The optional JSON report retains all violations.
.PARAMETER ReleaseDll
    The product DLL produced by src/NodeGroups.Runtime.
.PARAMETER TestDll
    The independent DLL produced by tests/NodeGroups.Runtime.Tests.
.PARAMETER ExpectedVersion
    Assembly version to require for both DLLs. Defaults to manifest.json's
    version_number. Three-component package versions are normalized to .0.
.PARAMETER ReportPath
    Optional JSON evidence path. Its parent directory is created if necessary.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ReleaseDll,
    [Parameter(Mandatory)] [string] $TestDll,
    [string] $ExpectedVersion,
    [string] $ReportPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Explicit type names also reject old builds whose tests shared the product
# namespace. Namespace checks cover new suites and their generated helpers.
$requiredTestTypes = @(
    'RuntimeTestRunner', 'NativeCommandIntegrationTests', 'GroupCanvasTests',
    'GroupControlsTests', 'BorderHitTargetTests', 'DropTargetFeedbackTests',
    'PaletteDragTests', 'PersistenceLifecycleTests', 'NativeProjectFixture',
    'NativeStyleInspector', 'RuntimeStressTests', 'SaveStateIntegrationTests', 'QuitConfirmationTests', 'TrashMembershipTests'
)
$legacyTestTypes = @(
    'RuntimeTests', 'AdvancedRuntimeTests', 'Iteration2RuntimeTests',
    'Iteration3RuntimeTests', 'Iteration4RuntimeTests', 'Iteration5RuntimeTests',
    'PaletteRuntimeTests', 'StorageRuntimeTests', 'NativeFixture', 'NativeStyleProbe'
)
$testNamespace = 'AzureArchive.NodeGroups.Runtime.Tests'
$productNamespace = 'AzureArchive.NodeGroups.Runtime'
$environmentGates = @('NODEGROUPS_TEST_REPORT', 'NODEGROUPS_TEST_FOCUS')
$testOnlyReferences = @('UnityEngine.ScreenCaptureModule', 'UnityEngine.ImageConversionModule')

# Walk real IL instructions rather than searching arbitrary bytes for 0x72.
# Otherwise a branch offset or numeric constant can be mistaken for ldstr.
$opcodes = @{}
foreach ($field in [System.Reflection.Emit.OpCodes].GetFields(
    [System.Reflection.BindingFlags]'Public, Static')) {
    $opcode = $field.GetValue($null)
    $opcodes[[int]$opcode.Value -band 0xffff] = $opcode
}

# Resolve only the metadata declaration of the attribute constructor. Calling
# GetCustomAttributes on a loaded Assembly would instantiate attributes and can
# force game dependencies to load, which this verifier intentionally avoids.
function Get-AttributeTypeName {
    param($Reader, [System.Reflection.Metadata.EntityHandle] $Constructor)

    $row = [System.Reflection.Metadata.Ecma335.MetadataTokens]::GetToken($Constructor) -band 0x00ffffff
    if ($Constructor.Kind -eq [System.Reflection.Metadata.HandleKind]::MemberReference) {
        $member = $Reader.GetMemberReference(
            [System.Reflection.Metadata.Ecma335.MetadataTokens]::MemberReferenceHandle($row))
        $parent = $member.Parent
        $parentRow = [System.Reflection.Metadata.Ecma335.MetadataTokens]::GetToken($parent) -band 0x00ffffff
        if ($parent.Kind -eq [System.Reflection.Metadata.HandleKind]::TypeReference) {
            $type = $Reader.GetTypeReference(
                [System.Reflection.Metadata.Ecma335.MetadataTokens]::TypeReferenceHandle($parentRow))
        }
        elseif ($parent.Kind -eq [System.Reflection.Metadata.HandleKind]::TypeDefinition) {
            $type = $Reader.GetTypeDefinition(
                [System.Reflection.Metadata.Ecma335.MetadataTokens]::TypeDefinitionHandle($parentRow))
        }
        else { return $null }
    }
    elseif ($Constructor.Kind -eq [System.Reflection.Metadata.HandleKind]::MethodDefinition) {
        $method = $Reader.GetMethodDefinition(
            [System.Reflection.Metadata.Ecma335.MetadataTokens]::MethodDefinitionHandle($row))
        $type = $Reader.GetTypeDefinition($method.GetDeclaringType())
    }
    else { return $null }
    return $Reader.GetString($type.Namespace) + '.' + $Reader.GetString($type.Name)
}

function Read-AssemblyEvidence {
    param([Parameter(Mandatory)] [string] $Path)

    $absolutePath = (Resolve-Path -LiteralPath $Path).ProviderPath
    $stream = [System.IO.File]::OpenRead($absolutePath)
    $pe = $null
    try {
        $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        if (-not $pe.HasMetadata) { throw "Not a managed PE assembly: $absolutePath" }
        $reader = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        $assembly = $reader.GetAssemblyDefinition()
        $buildFlavor = $null
        $informationalVersion = $null
        foreach ($attributeHandle in $assembly.GetCustomAttributes()) {
            $attribute = $reader.GetCustomAttribute($attributeHandle)
            $attributeName = Get-AttributeTypeName $reader $attribute.Constructor
            if ($attributeName -notin @('System.Reflection.AssemblyMetadataAttribute',
                'System.Reflection.AssemblyInformationalVersionAttribute')) { continue }
            $blob = $reader.GetBlobReader($attribute.Value)
            if ($blob.ReadUInt16() -ne 1) { throw "Invalid custom attribute prolog: $attributeName" }
            if ($attributeName -eq 'System.Reflection.AssemblyMetadataAttribute') {
                $key = $blob.ReadSerializedString()
                $value = $blob.ReadSerializedString()
                if ($key -eq 'NodeGroups.BuildFlavor') { $buildFlavor = $value }
            }
            else { $informationalVersion = $blob.ReadSerializedString() }
        }
        $sourceRevision = $null
        if ($informationalVersion -and $informationalVersion.Contains('+')) {
            $sourceRevision = $informationalVersion.Substring($informationalVersion.IndexOf('+') + 1)
        }
        $references = @($reader.AssemblyReferences | ForEach-Object {
            $reader.GetString($reader.GetAssemblyReference($_).Name)
        } | Sort-Object)
        $types = [System.Collections.Generic.List[object]]::new()
        $strings = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)

        foreach ($handle in $reader.TypeDefinitions) {
            $type = $reader.GetTypeDefinition($handle)
            $methods = [System.Collections.Generic.List[string]]::new()
            foreach ($methodHandle in $type.GetMethods()) {
                $method = $reader.GetMethodDefinition($methodHandle)
                $methods.Add($reader.GetString($method.Name))
                if ($method.RelativeVirtualAddress -eq 0) { continue }
                $body = [System.Reflection.Metadata.PEReaderExtensions]::GetMethodBody(
                    $pe, $method.RelativeVirtualAddress)
                $bytes = $body.GetILBytes()
                $offset = 0
                while ($offset -lt $bytes.Length) {
                    $code = [int]$bytes[$offset++]
                    if ($code -eq 0xfe) {
                        if ($offset -ge $bytes.Length) { throw 'Truncated two-byte IL opcode.' }
                        $code = 0xfe00 -bor [int]$bytes[$offset++]
                    }
                    if (-not $opcodes.ContainsKey($code)) { throw "Invalid IL opcode: $code" }
                    $opcode = $opcodes[$code]
                    $operandBytes = switch ($opcode.OperandType.ToString()) {
                        'InlineNone' { 0 }
                        'ShortInlineBrTarget' { 1 }
                        'ShortInlineI' { 1 }
                        'ShortInlineVar' { 1 }
                        'InlineVar' { 2 }
                        'InlineI8' { 8 }
                        'InlineR' { 8 }
                        'InlineSwitch' {
                            if ($offset + 4 -gt $bytes.Length) { throw 'Truncated IL switch.' }
                            $count = [BitConverter]::ToInt32($bytes, $offset)
                            if ($count -lt 0) { throw 'Invalid IL switch count.' }
                            4 + 4 * $count
                        }
                        default { 4 }
                    }
                    if ($offset + $operandBytes -gt $bytes.Length) { throw 'Truncated IL operand.' }
                    if ($opcode.Name -eq 'ldstr') {
                        $token = [BitConverter]::ToInt32($bytes, $offset)
                        if (($token -band 0xff000000) -ne 0x70000000) {
                            throw 'Invalid IL user-string token.'
                        }
                        $stringHandle = [System.Reflection.Metadata.Ecma335.MetadataTokens]::UserStringHandle(
                            $token -band 0x00ffffff)
                        [void]$strings.Add($reader.GetUserString($stringHandle))
                    }
                    $offset += $operandBytes
                }
            }
            $types.Add([pscustomobject]@{
                namespace = $reader.GetString($type.Namespace)
                name = $reader.GetString($type.Name)
                methods = $methods.ToArray()
            })
        }
        return [pscustomobject]@{
            path = $absolutePath
            sha256 = (Get-FileHash -LiteralPath $absolutePath -Algorithm SHA256).Hash
            assemblyName = $reader.GetString($assembly.Name)
            assemblyVersion = $assembly.Version.ToString()
            buildFlavor = $buildFlavor
            informationalVersion = $informationalVersion
            sourceRevision = $sourceRevision
            assemblyReferences = $references
            types = $types.ToArray()
            userStrings = @($strings | Sort-Object)
        }
    }
    finally {
        if ($null -ne $pe) { $pe.Dispose() }
        $stream.Dispose()
    }
}

$checks = [System.Collections.Generic.List[object]]::new()
function Assert-Boundary {
    param([bool] $Condition, [string] $Message)
    $checks.Add([pscustomobject]@{ passed = $Condition; message = $Message })
    if ($Condition) { Write-Host "PASS: $Message" }
    else { Write-Host "FAIL: $Message" -ForegroundColor Red }
}

$release = $null
$test = $null
$fatalError = $null
try {
    if ([string]::IsNullOrWhiteSpace($ExpectedVersion)) {
        $manifestPath = Join-Path $PSScriptRoot '../../manifest.json'
        $ExpectedVersion = (Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json).version_number
    }
    $version = [Version]$ExpectedVersion
    if ($version.Build -lt 0) { throw 'ExpectedVersion must have at least three components.' }
    $normalizedVersion = [Version]::new($version.Major, $version.Minor, $version.Build,
        [Math]::Max(0, $version.Revision)).ToString()
    $release = Read-AssemblyEvidence $ReleaseDll
    $test = Read-AssemblyEvidence $TestDll

    Assert-Boundary (-not [StringComparer]::OrdinalIgnoreCase.Equals($release.path, $test.path)) `
        'Shipping and test DLLs use different output paths.'
    Assert-Boundary ($release.buildFlavor -eq 'Release') `
        'Shipping metadata declares NodeGroups.BuildFlavor=Release.'
    Assert-Boundary ($test.buildFlavor -eq 'RuntimeTests') `
        'Test metadata declares NodeGroups.BuildFlavor=RuntimeTests.'
    Assert-Boundary (-not [string]::IsNullOrWhiteSpace($release.informationalVersion) -and
        $release.informationalVersion -eq $test.informationalVersion) `
        'Shipping and test informational versions identify the same source revision.'
    foreach ($entry in @($release, $test)) {
        $label = if ($entry -eq $release) { 'Shipping' } else { 'Test' }
        Assert-Boundary ($entry.assemblyName -eq 'NodeGroups') "$label assembly name is NodeGroups."
        Assert-Boundary ($entry.assemblyVersion -eq $normalizedVersion) `
            "$label assembly version is $normalizedVersion."
        foreach ($name in @('Plugin', 'EditorSession')) {
            $present = @($entry.types | Where-Object {
                $_.namespace -eq $productNamespace -and $_.name -eq $name
            }).Count -eq 1
            Assert-Boundary $present "$label contains product type $name."
        }
    }
    foreach ($reference in $testOnlyReferences) {
        Assert-Boundary ($reference -notin $release.assemblyReferences) `
            "Shipping metadata excludes test-only assembly reference $reference."
        # A configured but unused Reference is not emitted into assembly metadata.
        # Test screenshots currently call ImageConversion; they do not call the
        # ScreenCapture API, so only the actually used dependency is required.
        if ($reference -eq 'UnityEngine.ImageConversionModule') {
            Assert-Boundary ($reference -in $test.assemblyReferences) `
                "Test metadata includes test-only assembly reference $reference."
        }
    }

    $forbidden = @($release.types | Where-Object {
        $_.namespace -match '(^|\.)Tests($|\.)' -or
        $_.name -in $requiredTestTypes -or $_.name -in $legacyTestTypes -or
        $_.name -match '^Iteration\d+RuntimeTests$'
    })
    Assert-Boundary ($forbidden.Count -eq 0) `
        'Shipping metadata contains no test suites, runner, fixture, or style inspector.'
    foreach ($type in $forbidden) { Write-Host "  forbidden type: $($type.namespace).$($type.name)" }
    foreach ($gate in $environmentGates) {
        Assert-Boundary ($gate -notin $release.userStrings) "Shipping IL excludes environment gate $gate."
        Assert-Boundary ($gate -in $test.userStrings) "Test IL includes environment gate $gate."
    }
    foreach ($name in $requiredTestTypes) {
        $present = @($test.types | Where-Object {
            $_.namespace -eq $testNamespace -and $_.name -eq $name
        }).Count -eq 1
        Assert-Boundary $present "Test metadata contains $testNamespace.$name."
    }
    foreach ($entry in @($release, $test)) {
        $session = @($entry.types | Where-Object {
            $_.namespace -eq $productNamespace -and $_.name -eq 'EditorSession'
        })
        $hasFactory = $session.Count -eq 1 -and 'CreateMember' -in $session[0].methods
        if ($entry -eq $release) {
            Assert-Boundary (-not $hasFactory) 'Shipping EditorSession excludes the test-only CreateMember helper.'
        }
        else {
            Assert-Boundary $hasFactory 'Test EditorSession retains the test-only CreateMember helper.'
        }
    }
}
catch {
    $fatalError = $_.Exception.Message
    Assert-Boundary $false "Assembly metadata inspection failed: $fatalError"
}

$success = $null -eq $fatalError -and @($checks | Where-Object { -not $_.passed }).Count -eq 0
# Keep report evidence useful without copying potentially large arbitrary IL
# strings. Record the gate subset, concrete type names, and artifact hashes.
function Get-ReportAssembly {
    param($Entry)
    if ($null -eq $Entry) { return $null }
    return [pscustomobject]@{
        path = $Entry.path
        sha256 = $Entry.sha256
        assemblyName = $Entry.assemblyName
        assemblyVersion = $Entry.assemblyVersion
        buildFlavor = $Entry.buildFlavor
        informationalVersion = $Entry.informationalVersion
        sourceRevision = $Entry.sourceRevision
        assemblyReferences = $Entry.assemblyReferences
        testBuild = $Entry.buildFlavor -eq 'RuntimeTests'
        classesCompiled = @($Entry.types | ForEach-Object {
            if ($_.namespace) { "$($_.namespace).$($_.name)" } else { $_.name }
        } | Sort-Object)
        environmentGates = @($environmentGates | Where-Object { $_ -in $Entry.userStrings })
    }
}
$report = [pscustomobject]@{
    schemaVersion = 1
    success = $success
    expectedVersion = $ExpectedVersion
    release = Get-ReportAssembly $release
    test = Get-ReportAssembly $test
    checks = $checks.ToArray()
    error = $fatalError
}
if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
    $absoluteReport = [IO.Path]::GetFullPath($ReportPath)
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($absoluteReport))
    [IO.File]::WriteAllText($absoluteReport, ($report | ConvertTo-Json -Depth 8) + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
    Write-Host "Report: $absoluteReport"
}
if (-not $success) {
    # An unhandled throw gives `pwsh -File` exit code 1. When invoked with `&`
    # from test-runtime.ps1 it propagates normally through the caller's finally
    # block, allowing that caller to restore the installed release DLL/profile.
    throw 'Build boundary verification failed. Inspect the failed checks in the report.'
}
Write-Host "Build boundary verified: $($checks.Count) checks passed."
