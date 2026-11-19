[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$RunName = ('verify-' + (Get-Date -Format 'yyyyMMdd-HHmmss')),
    [string]$PackageCache,
    [string]$DotnetPath = 'dotnet',
    [string]$Only = 'SelectorLabels040:,Branding040:,Theme040:,ThemeIntegration040:,ThemeSettings040:,Versioning040:,PlaybackKeys040:',
    [switch]$Full,
    [switch]$BuildOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'ScriptPaths.ps1')
. (Join-Path $PSScriptRoot 'packaging/PublishValidation.ps1')
if ($Full -and $PSBoundParameters.ContainsKey('Only')) { throw 'Choose Full or Only, not both.' }
if (!$Full) {
    if ($Only -match '(?i)--account|--online') { throw 'Live account/network test flags are not allowed here.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $prefixes = @(foreach ($part in $Only.Split(',')) {
        $prefix = $part.Trim()
        if (!$prefix -or $prefix.StartsWith('--') -or $prefix -match '\p{Cc}') { throw 'Only requires a comma-separated list of non-empty check prefixes.' }
        if ($seen.Add($prefix)) { $prefix }
    })
    $Only = $prefixes -join ','
}
$projectRoot = Resolve-YamuseFileSystemPath (Join-Path $PSScriptRoot '..')
$runPath = Join-Path $projectRoot ('work/' + $RunName)
$nugetPath = Join-Path $projectRoot 'work/nuget-net10'
Assert-PackagingPath $runPath
if (Test-Path -LiteralPath $runPath) { throw 'Use a new RunName; inspect an existing attempt before retrying.' }
Assert-PackagingPath $nugetPath
if (Test-Path -LiteralPath $nugetPath) { Get-PackagingTreeFiles $nugetPath | Out-Null }
$packagePath = $null
if (![string]::IsNullOrWhiteSpace($PackageCache)) {
    $packagePath = Resolve-YamuseFileSystemPath $PackageCache
    Get-PackagingTreeFiles $packagePath | Out-Null
}
$dotnetExecutable = Resolve-YamuseDotnet $DotnetPath
$pin = Join-Path $projectRoot 'tools/packaging/PinnedRuntime.targets'
$nugetConfig = Join-Path $projectRoot 'tools/packaging/NuGet.Config'
$productProject = Join-Path $projectRoot 'src/YMusic.Native/YMusic.Native.csproj'
$checksProject = Join-Path $projectRoot 'tests/YMusic.Native.Checks/YMusic.Native.Checks.csproj'
foreach ($file in $pin,$nugetConfig,$productProject,$checksProject) { Assert-PackagingPath $file -File -Required }
$sdkOutput = Invoke-YamuseDotnet $dotnetExecutable $projectRoot @('--version')
$sdkVersion = ([string]$sdkOutput).Trim()
if ($LASTEXITCODE -ne 0 -or $sdkVersion -ne '10.0.404') { throw 'Use the pinned .NET SDK 10.0.404 (DotnetPath may point to a local SDK).' }
$null = New-Item -ItemType Directory -Path $runPath
$dotnetHome = Join-Path $runPath 'dotnet-home'
$buildPath = Join-Path $runPath 'build'
$buildEnvironment = New-YamuseDotnetEnvironment $dotnetHome $nugetPath
$publishPath = Join-Path $runPath 'publish'
$checksPath = Join-Path $runPath 'checks-bin'
if ($packagePath) {
    # An explicit local cache is one literal source, including list punctuation.
    [xml]$settings = Get-Content -LiteralPath $nugetConfig -Raw
    $sources = $settings.SelectSingleNode('/configuration/packageSources')
    $sources.RemoveAll()
    $null = $sources.AppendChild($settings.CreateElement('clear'))
    $source = $settings.CreateElement('add')
    $source.SetAttribute('key','verified-local-packages')
    $source.SetAttribute('value',(Set-YamuseLocalNuGetSource $packagePath $buildEnvironment))
    $null = $sources.AppendChild($source)
    $nugetConfig = Join-Path $runPath 'LocalFeed.Config'
    $configStream = [IO.File]::Open($nugetConfig, [IO.FileMode]::CreateNew)
    try { $settings.Save($configStream) } finally { $configStream.Dispose() }
}
$pathProperties = @(
    '-p:DirectoryBuildTargetsPath=' + (ConvertTo-YamuseMSBuildPath $pin)
    '-p:PrototypeArtifactsPath=' + (ConvertTo-YamuseMSBuildPath $buildPath)
)
$common = @('-c','Release','-r','win-x64','--self-contained','true','--disable-build-servers') + $pathProperties
if ($nugetPath.Contains(';')) {
    # NuGet emits a SourceRoot item with the literal package path.
    $common += '-p:EnableSourceLink=false'
}
$restore = @('restore',$checksProject,'--locked-mode','--disable-build-servers','--configfile',(ConvertTo-YamuseMSBuildPath $nugetConfig),'-r','win-x64','-p:SelfContained=true') + $pathProperties
if ($nugetConfig.Contains(';') -or $packagePath) {
    # Avoid the classic graph's semicolon-separated config and source metadata.
    $restore += '-p:RestoreUseStaticGraphEvaluation=true'
}
if ($packagePath) {
    $restore += '-p:NuGetAudit=false'
}
function Invoke-CheckedDotnet([string[]]$Arguments, [string]$LogName) {
    foreach ($folder in $dotnetHome,$buildPath,$publishPath,$checksPath,$nugetPath) {
        Assert-PackagingPath $folder
        if (Test-Path -LiteralPath $folder) { Get-PackagingTreeFiles $folder | Out-Null }
    }
    $logPath = Join-Path $runPath $LogName
    Assert-PackagingPath $logPath -File
    $stream = [IO.File]::Open($logPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    $writer = [IO.StreamWriter]::new($stream)
    try {
        Invoke-YamuseDotnet $dotnetExecutable $projectRoot $Arguments $buildEnvironment *>&1 |
            ForEach-Object { $writer.WriteLine([string]$_) }
    }
    finally { $writer.Dispose() }
    if ($LASTEXITCODE -ne 0) {
        Get-Content -LiteralPath $logPath -Tail 18
        throw "dotnet failed; inspect $LogName before another attempt."
    }
}
function Write-NewVerification([string]$Name, $Proof) {
    $path = Join-Path $runPath $Name
    Assert-PackagingPath $path -File
    $bytes = [Text.Encoding]::UTF8.GetBytes(($Proof | ConvertTo-Json -Depth 5) + [Environment]::NewLine)
    $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
}
function Assert-PrivateDesktopReceipt($Desktop) {
    if (!(Test-PackagingJsonString $Desktop 'status' 'PASS')) { throw 'Private desktop check failed; inspect the receipt.' }
    foreach ($name in 'runnerExitCode','childExitCode','ownJobActiveProcessesAfterCleanup') {
        $property = $Desktop.PSObject.Properties[$name]
        if (!$property -or ($property.Value -isnot [int] -and $property.Value -isnot [long]) -or $property.Value -ne 0) {
            throw "Private desktop receipt does not confirm $name is zero."
        }
    }
    foreach ($name in 'childStopped','desktopClosed','inputDesktopNameUnchanged','launcherDesktopNameUnchanged','jobKillOnClose','childJobAssignedAtCreation') {
        $property = $Desktop.PSObject.Properties[$name]
        if (!$property -or $property.Value -isnot [bool] -or !$property.Value) { throw "Private desktop receipt does not confirm $name." }
    }
}
Invoke-CheckedDotnet $restore 'restore.log'
Invoke-CheckedDotnet (@('publish',$productProject) + $common + @('--no-restore','-p:PublishSingleFile=false','-p:PublishTrimmed=false','-o',(ConvertTo-YamuseMSBuildPath $publishPath))) 'publish.log'
Invoke-CheckedDotnet (@('build',$checksProject) + $common + @('--no-restore','-o',(ConvertTo-YamuseMSBuildPath $checksPath))) 'checks-build.log'
$publishFiles = Get-PublishedFileInventory $publishPath
$publishInventorySha256 = Get-PublishedInventorySha256 $publishFiles
Get-PackagingTreeFiles $checksPath | Out-Null
Copy-PackagingInventory $publishPath $checksPath $publishFiles -ReplaceExisting
$productDll = Join-Path $publishPath 'YMusic.Native.dll'
$productHash = (Get-FileHash -LiteralPath $productDll).Hash
if ($productHash -ne (Get-FileHash -LiteralPath (Join-Path $checksPath 'YMusic.Native.dll')).Hash) { throw 'Checks and publish must use the same product DLL.' }
$appHostHash = Assert-YamuseAppHost (Join-Path $publishPath 'YMusic.Native.exe') $publishFiles['YMusic.Native.exe'].sha256
$null = Assert-YamuseAppHost (Join-Path $checksPath 'YMusic.Native.exe') $appHostHash
$productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($productDll).ProductVersion
$dependencyVersion = $productVersion.Split('+')[0]
$publishRuntime = Assert-PinnedPublishRuntime $publishPath $dependencyVersion $publishFiles
$checksRuntime = Assert-PinnedPublishRuntime $checksPath $dependencyVersion (Get-PublishedFileInventory $checksPath) -FixtureRunner
if ($publishRuntime.digest -cne $checksRuntime.digest) { throw 'Checks and publish must use the same pinned runtime payload.' }
if ($BuildOnly) {
    Write-NewVerification 'build-verification.json' ([ordered]@{ completedUtc=[DateTimeOffset]::UtcNow.ToString('o'); status='BUILD_PASS_NATIVE_PENDING'; productDllSha256=$productHash;
        productVersion=$productVersion; publishedFolder=$publishPath; mainExecutableSha256=$appHostHash; publishInventorySha256=$publishInventorySha256; sdk=$sdkVersion; runtime="$($publishRuntime.version) self-contained"; publishRuntime=$publishRuntime; checksRuntime=$checksRuntime;
        nativeChecksRun=$false; liveAccountUsed=$false; setupExecuted=$false })
    Write-Output ('Build verified; native checks pending: ' + $runPath)
    return
}
$resultsPath = Join-Path $runPath 'check-results'
$desktopPath = Join-Path $runPath 'desktop-receipt.json'
$checksLogPath = Join-Path $runPath 'checks.log'
Assert-PackagingPath $resultsPath
Assert-PackagingPath $desktopPath -File
Assert-PackagingPath $checksLogPath -File
$arguments = @($resultsPath)
if (!$Full) { $arguments += ('--only=' + $Only) }
$runner = Join-Path $projectRoot 'tools/testing/private-desktop-runner/Invoke-PrivateDesktopChecks.ps1'
$runnerWorkPath = Join-Path $runPath 'launcher'
Assert-PackagingPath $runner -File -Required
$fixtureExecutable = Join-Path $checksPath 'YMusic.Native.Checks.exe'
Assert-PackagingPath $fixtureExecutable -File -Required
& $runner -Executable $fixtureExecutable -Arguments $arguments -WorkingDirectory $checksPath -LogPath $checksLogPath -ReceiptPath $desktopPath -DotnetPath $dotnetExecutable -RunnerWorkFolder $runnerWorkPath -EnvironmentOverrides $buildEnvironment
if ($LASTEXITCODE -ne 0) { throw 'Private desktop runner failed; inspect its output and any receipt.' }
Assert-PackagingPath $desktopPath -File -Required
$desktop = Read-PackagingJson $desktopPath
Assert-PrivateDesktopReceipt $desktop
$launcherAssembly = Join-Path $runnerWorkPath 'bin/PrivateDesktopRunner.dll'
Assert-PackagingPath $launcherAssembly -File -Required
$launcherHash = (Get-FileHash -LiteralPath $launcherAssembly -Algorithm SHA256).Hash
if (!(Test-PackagingJsonString $desktop 'runnerAssemblySha256' $launcherHash)) { throw 'Isolated launcher differs from the desktop receipt.' }
Assert-PackagingPath $checksLogPath -File -Required
$checksLogHash = (Get-FileHash -LiteralPath $checksLogPath -Algorithm SHA256).Hash
if (!(Test-PackagingJsonString $desktop 'logSha256' $checksLogHash)) { throw 'Checks log differs from the launcher receipt.' }
$resultsFile = Join-Path $resultsPath 'checks.json'
Assert-PackagingPath $resultsFile -File -Required
$results = Read-PackagingJson $resultsFile
if ($results.failed -ne 0 -or $results.checks.Count -eq 0) { throw 'Selected checks must be non-empty and all pass.' }
if (!$Full) {
    foreach ($prefix in $prefixes) {
        if (!($results.checks | Where-Object { $_.name.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })) { throw "No checks matched prefix: $prefix" }
    }
    foreach ($check in $results.checks) {
        if (!($prefixes | Where-Object { $check.name.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) })) { throw "Unexpected check outside Only: $($check.name)" }
    }
}
Assert-PublishedInventorySha256 (Get-PublishedFileInventory $publishPath) $publishInventorySha256
$receipt = [ordered]@{
    completedUtc = [DateTimeOffset]::UtcNow.ToString('o'); status = 'PASS'; projectRoot = $projectRoot
    productVersion = $productVersion
    productDllSha256 = $productHash; publishedFolder = $publishPath; mainExecutableSha256 = $appHostHash; publishInventorySha256 = $publishInventorySha256
    checks = $results.checks.Count; full = [bool]$Full; selection = $(if ($Full) {'all'} else {$Only})
    sdk=$sdkVersion; runtime = "$($publishRuntime.version) self-contained"; publishRuntime=$publishRuntime; checksRuntime=$checksRuntime; liveAccountUsed = $false; setupExecuted = $false
    isolatedInactiveDesktop = $true; launcherBuildPath = $runnerWorkPath; launcherAssemblySha256 = $launcherHash; packageCache = $packagePath
}
Write-NewVerification 'verification.json' $receipt
Get-Content -LiteralPath $checksLogPath -Tail 4
Write-Output ('Verified project: ' + $runPath)
