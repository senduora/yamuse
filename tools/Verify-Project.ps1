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
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sdkVersion = (& $DotnetPath --version).Trim()
if ($LASTEXITCODE -ne 0 -or $sdkVersion -ne '10.0.403') { throw 'Use the pinned .NET SDK 10.0.403 (DotnetPath may point to a local SDK).' }
$runPath = Join-Path $projectRoot ('work/' + $RunName)
if (Test-Path -LiteralPath $runPath) { throw 'Use a new RunName; inspect an existing attempt before retrying.' }
if ($Full -and $PSBoundParameters.ContainsKey('Only')) { throw 'Choose Full or Only, not both.' }
if (!$Full -and [string]::IsNullOrWhiteSpace($Only)) { throw 'Only must select actual checks, or use Full.' }
if ($Only -match '(?i)--account|--online') { throw 'Live account/network test flags are not allowed here.' }
New-Item -ItemType Directory -Path $runPath | Out-Null
$env:DOTNET_CLI_HOME = Join-Path $runPath 'dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:NUGET_PACKAGES = Join-Path $projectRoot 'work/nuget-net10'
$pin = Join-Path $projectRoot 'tools/packaging/PinnedRuntime.targets'
$productProject = Join-Path $projectRoot 'src/YMusic.Native/YMusic.Native.csproj'
$checksProject = Join-Path $projectRoot 'tests/YMusic.Native.Checks/YMusic.Native.Checks.csproj'
$publishPath = Join-Path $runPath 'publish'
$checksPath = Join-Path $runPath 'checks-bin'
$common = @('-c','Release','-r','win-x64','--self-contained','true',('-p:DirectoryBuildTargetsPath=' + $pin),('-p:PrototypeArtifactsPath=' + (Join-Path $runPath 'build')))
$restore = @('restore',$checksProject,'--locked-mode','-r','win-x64','-p:SelfContained=true',('-p:DirectoryBuildTargetsPath=' + $pin),('-p:PrototypeArtifactsPath=' + (Join-Path $runPath 'build')))
if (![string]::IsNullOrWhiteSpace($PackageCache)) {
    $packagePath = [IO.Path]::GetFullPath($PackageCache)
    if (!(Test-Path -LiteralPath $packagePath -PathType Container)) { throw 'PackageCache directory is missing.' }
    $restore += @('--source',$packagePath,'-p:NuGetAudit=false')
} else {
    $restore += @('--configfile',(Join-Path $projectRoot 'tools/packaging/NuGet.Config'))
}
function Invoke-CheckedDotnet([string[]]$Arguments, [string]$LogName) {
    & $DotnetPath @Arguments *> (Join-Path $runPath $LogName)
    if ($LASTEXITCODE -ne 0) {
        Get-Content -LiteralPath (Join-Path $runPath $LogName) -Tail 18
        throw "dotnet failed; inspect $LogName before another attempt."
    }
}
Invoke-CheckedDotnet $restore 'restore.log'
Invoke-CheckedDotnet (@('publish',$productProject) + $common + @('--no-restore','-p:PublishSingleFile=false','-p:PublishTrimmed=false','-o',$publishPath)) 'publish.log'
Invoke-CheckedDotnet (@('build',$checksProject) + $common + @('--no-restore','-o',$checksPath)) 'checks-build.log'
Get-ChildItem -LiteralPath $publishPath -Force | Copy-Item -Destination $checksPath -Recurse -Force
$productDll = Join-Path $publishPath 'YMusic.Native.dll'
$productHash = (Get-FileHash -LiteralPath $productDll).Hash
if ($productHash -ne (Get-FileHash -LiteralPath (Join-Path $checksPath 'YMusic.Native.dll')).Hash) { throw 'Checks and publish must use the same product DLL.' }
$runtimeConfig = Get-Content -LiteralPath (Join-Path $publishPath 'YMusic.Native.runtimeconfig.json') -Raw | ConvertFrom-Json
$frameworks = @($runtimeConfig.runtimeOptions.includedFrameworks)
if ($frameworks.Count -lt 2 -or @($frameworks | Where-Object { $_.version -ne '10.0.14' }).Count -ne 0) { throw 'Publish must include the pinned .NET 10.0.14 Core/WPF runtime.' }
if ($BuildOnly) {
    [ordered]@{ completedUtc=[DateTimeOffset]::UtcNow.ToString('o'); status='BUILD_PASS_NATIVE_PENDING'; productDllSha256=$productHash;
        productVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo($productDll).ProductVersion; sdk=$sdkVersion; runtime='10.0.14 self-contained';
        nativeChecksRun=$false; liveAccountUsed=$false; setupExecuted=$false } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runPath 'build-verification.json') -Encoding utf8
    Write-Output ('Build verified; native checks pending: ' + $runPath)
    return
}
$resultsPath = Join-Path $runPath 'check-results'
$arguments = @($resultsPath)
if (!$Full) { $arguments += ('--only=' + $Only) }
$runner = Join-Path $projectRoot 'tools/testing/private-desktop-runner/Invoke-PrivateDesktopChecks.ps1'
& $runner -Executable (Join-Path $checksPath 'YMusic.Native.Checks.exe') -Arguments $arguments -WorkingDirectory $checksPath -LogPath (Join-Path $runPath 'checks.log') -ReceiptPath (Join-Path $runPath 'desktop-receipt.json') -DotnetPath $DotnetPath
$desktop = Get-Content -LiteralPath (Join-Path $runPath 'desktop-receipt.json') -Raw | ConvertFrom-Json
if ($desktop.status -ne 'PASS' -or $desktop.childExitCode -ne 0 -or !$desktop.childStopped -or !$desktop.desktopClosed -or !$desktop.inputDesktopNameUnchanged) { throw 'Private desktop check failed; inspect the receipt. A locked desktop is not bypassed.' }
$results = Get-Content -LiteralPath (Join-Path $resultsPath 'checks.json') -Raw | ConvertFrom-Json
if ($results.failed -ne 0 -or $results.checks.Count -eq 0) { throw 'Selected checks must be non-empty and all pass.' }
$receipt = [ordered]@{
    completedUtc = [DateTimeOffset]::UtcNow.ToString('o'); status = 'PASS'; projectRoot = $projectRoot
    productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($productDll).ProductVersion
    productDllSha256 = $productHash; checks = $results.checks.Count; full = [bool]$Full; selection = $(if ($Full) {'all'} else {$Only})
    sdk=$sdkVersion; runtime = '10.0.14 self-contained'; liveAccountUsed = $false; setupExecuted = $false
    isolatedInactiveDesktop = $true; packageCache = $PackageCache
}
$receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runPath 'verification.json') -Encoding utf8
Get-Content -LiteralPath (Join-Path $runPath 'checks.log') -Tail 4
Write-Output ('Verified project: ' + $runPath)
