[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory)][string]$OutputFolder,
    [Parameter(Mandatory)][string]$WorkFolder,
    [string]$PublishedFolder,
    [string]$PackageCache,
    [ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedProductSha256
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$packId = 'YMusic.Native' # Deliberately different from the existing user-data folder.
$velopackVersion = '1.2.161'
$runtimeVersion = '8.0.32'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$outputPath = [IO.Path]::GetFullPath($OutputFolder)
$workingPath = [IO.Path]::GetFullPath($WorkFolder)
if ($outputPath -eq $workingPath) { throw 'OutputFolder and WorkFolder must be different.' }
if (Test-Path -LiteralPath $outputPath) {
    if (@(Get-ChildItem -LiteralPath $outputPath -Force).Count -ne 0) { throw 'OutputFolder must be empty; existing releases are never overwritten.' }
}
if (Test-Path -LiteralPath $workingPath) {
    if (@(Get-ChildItem -LiteralPath $workingPath -Force).Count -ne 0) { throw 'Use a new, empty WorkFolder for each build.' }
}
New-Item -ItemType Directory -Path $workingPath -Force | Out-Null
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
if ([string]::IsNullOrWhiteSpace($PackageCache)) { $PackageCache = Join-Path $workingPath 'packages' }
$cachePath = [IO.Path]::GetFullPath($PackageCache)
New-Item -ItemType Directory -Path $cachePath -Force | Out-Null

# Build caches stay in the explicitly selected work directory. This never runs
# Setup.exe, installs the application, opens a window, or reads user music/auth.
$env:DOTNET_CLI_HOME = Join-Path $workingPath 'dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:NUGET_PACKAGES = Join-Path $workingPath 'nuget'

function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE." }
}
function Get-PinnedPackage([string]$Id, [string]$PackageVersion, [string]$Sha256) {
    $name = "$Id.$PackageVersion.nupkg"
    $target = Join-Path $cachePath $name
    if (!(Test-Path -LiteralPath $target)) {
        $uri = "https://api.nuget.org/v3-flatcontainer/$Id/$PackageVersion/$name"
        Invoke-WebRequest -Uri $uri -OutFile $target -TimeoutSec 120
    }
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $Sha256) { throw "Pinned SHA-256 mismatch: $name" }
    return $target
}
$pinnedPackages = @(
    (Get-PinnedPackage 'velopack' $velopackVersion 'D57275692B299751A12EE3E18283B5F7FE3E55339F96B1623345DA095DC484ED'),
    (Get-PinnedPackage 'vpk' $velopackVersion 'AC9BE73846D9A7A76483F8684ABD1503CD50703B992727429DCDC045589C5E08'),
    (Get-PinnedPackage 'microsoft.netcore.app.runtime.win-x64' $runtimeVersion '4B7AE1688A886CDCE2EAE81822FDE63D15D8E5E8A8B03957D80858E4CFD894C1'),
    (Get-PinnedPackage 'microsoft.windowsdesktop.app.runtime.win-x64' $runtimeVersion '9DC00EC0A7FB950E794E118993DC322FF97869173185CCA356FE9D11E8D8D6C7'),
    (Get-PinnedPackage 'microsoft.netcore.app.host.win-x64' $runtimeVersion '32D48BDBCE29EC3C57917CA2AAE2AA5FC71302702780C099B9F122842BCCD789'),
    (Get-PinnedPackage 'microsoft.windows.sdk.net.ref' '10.0.19041.31' 'A061DAF752588596B99B231CFB49DDA93C0F11617EA63AA0E833DAE42A6C22A5'),
    (Get-PinnedPackage 'microsoft.aspnetcore.app.runtime.win-x64' $runtimeVersion '45C80DB007781F6A825FC9FE0C288216117187CA92C5064ABEF016F7EF8291A2')
)
Invoke-DotNet (@('nuget','verify','--all') + $pinnedPackages + @('--configfile',(Join-Path $PSScriptRoot 'NuGet.Config')))
$localConfig = Join-Path $workingPath 'LocalFeed.Config'
$escapedCachePath = [System.Security.SecurityElement]::Escape($cachePath)
"<configuration><packageSources><clear/><add key=`"verified-local-packages`" value=`"$escapedCachePath`"/></packageSources></configuration>" | Set-Content -LiteralPath $localConfig -Encoding utf8
$toolPath = Join-Path $workingPath 'vpk'
Invoke-DotNet @('tool','install','vpk','--version',$velopackVersion,'--tool-path',$toolPath,'--configfile',$localConfig)
$vpk = Join-Path $toolPath 'vpk.exe'

$stage = Join-Path $workingPath 'publish'
New-Item -ItemType Directory -Path $stage -Force | Out-Null
if (![string]::IsNullOrWhiteSpace($PublishedFolder)) {
    # Prefer this mode when the caller already reviewed and checked this publish.
    $publishedPath = [IO.Path]::GetFullPath($PublishedFolder)
    if (!(Test-Path -LiteralPath (Join-Path $publishedPath 'YMusic.Native.exe'))) { throw 'PublishedFolder has no YMusic.Native.exe.' }
    Get-ChildItem -LiteralPath $publishedPath -Force | Copy-Item -Destination $stage -Recurse
}
else {
    $source = Join-Path $projectRoot 'src\YMusic.Native'
    $sourceStage = Join-Path $workingPath 'source\src\YMusic.Native'
    New-Item -ItemType Directory -Path $sourceStage -Force | Out-Null
    $configStage = Join-Path $workingPath 'source\tools\packaging'
    New-Item -ItemType Directory -Path $configStage -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'update-feed.json') -Destination $configStage
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses') -Destination $configStage -Recurse
    Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.txt') -Destination (Join-Path $workingPath 'source\THIRD-PARTY-NOTICES.txt')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination (Join-Path $workingPath 'source\LICENSE')
    $assetsStage = Join-Path $sourceStage 'Assets'
    New-Item -ItemType Directory -Path $assetsStage -Force | Out-Null
    foreach ($assetName in @('Yamuse.ico','Yamuse.png','Yamuse.svg')) {
        Copy-Item -LiteralPath (Join-Path (Join-Path $source 'Assets') $assetName) -Destination $assetsStage
    }
    # Copy only source files and the explicit branding assets. Never copy bin/obj,
    # account data, a previous package, or the live portable app directory.
    Get-ChildItem -LiteralPath $source -File | Where-Object {
        $_.Extension -in @('.cs','.xaml','.csproj','.manifest','.ico','.resx') -or $_.Name -eq 'packages.lock.json'
    } | Copy-Item -Destination $sourceStage
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PinnedRuntime.targets') -Destination (Join-Path $sourceStage 'Directory.Build.targets')
    $project = Join-Path $sourceStage 'YMusic.Native.csproj'
    Invoke-DotNet @('restore',$project,'--source',$cachePath,'--runtime','win-x64',"-p:RuntimeFrameworkVersion=$runtimeVersion",'-p:SelfContained=true','--force-evaluate')
    # The RID-specific lock is generated only in staging. It is saved as build evidence.
    Invoke-DotNet @('publish',$project,'-c','Release','-r','win-x64','--self-contained','true','--no-restore','-o',$stage,
        "-p:Version=$Version","-p:RuntimeFrameworkVersion=$runtimeVersion",'-p:PublishSingleFile=false','-p:PublishTrimmed=false','-p:DebugType=embedded')
    Copy-Item -LiteralPath (Join-Path $sourceStage 'packages.lock.json') -Destination (Join-Path $workingPath 'publish.packages.lock.json')
}
$assembly = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $stage 'YMusic.Native.dll'))
if ($assembly.Version.ToString(3) -ne $Version) { throw 'Published assembly version does not match Version.' }
$productHash = (Get-FileHash -LiteralPath (Join-Path $stage 'YMusic.Native.dll') -Algorithm SHA256).Hash
if ($ExpectedProductSha256 -and $productHash -ne $ExpectedProductSha256) { throw 'Published product DLL does not match the caller-approved SHA-256.' }
if (!(Test-Path -LiteralPath (Join-Path $stage 'coreclr.dll')) -or !(Test-Path -LiteralPath (Join-Path $stage 'PresentationFramework.dll'))) {
    throw 'PublishedFolder must contain the complete self-contained Windows x64 WPF publish.'
}
$runtimeConfig = Get-Content -LiteralPath (Join-Path $stage 'YMusic.Native.runtimeconfig.json') -Raw | ConvertFrom-Json
if (!($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' -and $_.version -eq $runtimeVersion })) {
    throw "Expected the pinned .NET $runtimeVersion runtime."
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'update-feed.json') -Destination (Join-Path $stage 'update-feed.json')
$legalStage = Join-Path $stage 'Legal'
New-Item -ItemType Directory -Path $legalStage -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'licenses') -File -Filter '*.txt' | Copy-Item -Destination $legalStage
$projectNoticesSource = Join-Path $projectRoot 'THIRD-PARTY-NOTICES.txt'
$projectNoticesHash = (Get-FileHash -LiteralPath $projectNoticesSource -Algorithm SHA256).Hash
$projectNoticesStage = Join-Path $legalStage 'Third-Party-Notices.txt'
Copy-Item -LiteralPath $projectNoticesSource -Destination $projectNoticesStage
if ((Get-FileHash -LiteralPath $projectNoticesStage -Algorithm SHA256).Hash -ne $projectNoticesHash) { throw 'Third-party notices changed while staging.' }
$projectLicenseSource = Join-Path $projectRoot 'LICENSE'
$projectLicenseHash = (Get-FileHash -LiteralPath $projectLicenseSource -Algorithm SHA256).Hash
$projectLicenseStage = Join-Path $legalStage 'Yamuse-LICENSE.txt'
Copy-Item -LiteralPath $projectLicenseSource -Destination $projectLicenseStage
if ((Get-FileHash -LiteralPath $projectLicenseStage -Algorithm SHA256).Hash -ne $projectLicenseHash) { throw 'Yamuse license changed while staging.' }
$sourceIcon = Join-Path $projectRoot 'src\YMusic.Native\Assets\Yamuse.ico'
$iconHash = (Get-FileHash -LiteralPath $sourceIcon -Algorithm SHA256).Hash
$stageIcon = Join-Path $stage 'Assets\Yamuse.ico'
if (!(Test-Path -LiteralPath $stageIcon) -or (Get-FileHash -LiteralPath $stageIcon -Algorithm SHA256).Hash -ne $iconHash) { throw 'Published Yamuse icon is missing or differs from the approved source.' }

# No --framework: .NET 8 is bundled. No --msi: the standard Setup is per-user,
# uses LocalAppData, and requires no administrator elevation.
& $vpk pack --packId $packId --packVersion $Version --packDir $stage --mainExe 'YMusic.Native.exe' --packTitle 'Yamuse' --packAuthors 'senduora' --icon $stageIcon --runtime win-x64 --channel win --shortcuts 'Desktop,StartMenuRoot' --outputDir $outputPath
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed with exit code $LASTEXITCODE." }
& (Join-Path $PSScriptRoot 'Rename-ReleaseAssets.ps1') -OutputFolder $outputPath -Version $Version | Out-Null
& (Join-Path $PSScriptRoot 'Inspect-Package.ps1') -OutputFolder $outputPath -ExpectedVersion $Version -ExpectedPackId $packId -ExpectedProductSha256 $productHash -ExpectedProjectNoticesSha256 $projectNoticesHash -ExpectedProjectLicenseSha256 $projectLicenseHash -ExpectedIconSha256 $iconHash -ReferencePublishedFolder $stage
if (!$?) { throw 'Package inspection failed.' }
[pscustomobject]@{
    version=$Version; packId=$packId; product='Yamuse'; authors='senduora'; velopack=$velopackVersion; runtime=$runtimeVersion; productDllSha256=$productHash; projectNoticesSha256=$projectNoticesHash; projectLicenseSha256=$projectLicenseHash; iconSha256=$iconHash;
    sdk=(& dotnet --version); configuration='Self-contained Windows x64, public GitHub feed, explicit apply';
    workFolder=$workingPath; packageSource='https://api.nuget.org/v3/index.json';
    packages=@($pinnedPackages | ForEach-Object { [pscustomobject]@{file=[IO.Path]::GetFileName($_);sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash} })
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputPath 'build-provenance.json') -Encoding utf8
Write-Output "Local package ready: $outputPath"
