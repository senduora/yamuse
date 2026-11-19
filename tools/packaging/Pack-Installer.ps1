[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory)][string]$OutputFolder,
    [Parameter(Mandatory)][string]$WorkFolder,
    [string]$PublishedFolder,
    [string]$PackageCache,
    [string]$DotnetPath,
    [string]$VerificationReceipt,
    [ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedAppHostSha256,
    [ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedProductSha256
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\ScriptPaths.ps1')
. (Join-Path $PSScriptRoot 'PublishValidation.ps1')

$packId = 'YMusic.Native' # Deliberately different from the existing user-data folder.
$velopackVersion = '1.2.161'
$runtimeVersion = '10.0.15'
$sdkVersion = '10.0.404'
$projectRoot = Resolve-YamuseFileSystemPath (Join-Path $PSScriptRoot '..\..')
function Assert-PackageProductVersion([string]$ProductDllPath, [string]$ExpectedVersion) {
    $fullVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($ProductDllPath).ProductVersion
    if ([string]::IsNullOrWhiteSpace($fullVersion) -or $fullVersion.Split('+')[0] -cne $ExpectedVersion) {
        throw 'Published product informational version does not match Version; preview DLLs cannot be packaged as stable.'
    }
    $assembly = [Reflection.AssemblyName]::GetAssemblyName($ProductDllPath)
    if ($assembly.Version -ne [version]"$ExpectedVersion.0") { throw 'Published assembly version does not match Version.' }
}
function Assert-DisjointPackageFolders([Collections.IDictionary]$Folders) {
    $directories = @($Folders.GetEnumerator())
    for ($i = 0; $i -lt $directories.Count; $i++) {
        $left = $directories[$i].Value.TrimEnd('\','/') + '\'
        for ($j = $i + 1; $j -lt $directories.Count; $j++) {
            $right = $directories[$j].Value.TrimEnd('\','/') + '\'
            if ($left.StartsWith($right, [StringComparison]::OrdinalIgnoreCase) -or
                $right.StartsWith($left, [StringComparison]::OrdinalIgnoreCase)) {
                throw "$($directories[$i].Key) and $($directories[$j].Key) must be disjoint directories."
            }
        }
    }
}
function Copy-PackageStageAdditions([string]$Stage, $BaseFiles, [Collections.IDictionary]$Additions) {
    $expected = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach ($relative in $BaseFiles.Keys) { $expected.Add($relative, $BaseFiles[$relative]) }
    foreach ($relative in $Additions.Keys) {
        $source = $Additions[$relative]
        $target = Join-Path $Stage $relative
        Assert-PackagingPath $source -File -Required
        Assert-PackagingPath $target -File
        $stream = [IO.File]::OpenRead($source)
        try {
            $expected[$relative] = [pscustomobject]@{ bytes=$stream.Length; sha256=(Get-FileHash -InputStream $stream -Algorithm SHA256).Hash }
        } finally { $stream.Dispose() }
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
        [IO.File]::Copy($source, $target, $true)
    }
    return $expected
}
$outputPath = Resolve-YamuseFileSystemPath $OutputFolder
$workingPath = Resolve-YamuseFileSystemPath $WorkFolder
$publishedPath = if (![string]::IsNullOrWhiteSpace($PublishedFolder)) { Resolve-YamuseFileSystemPath $PublishedFolder }
$internalCache = Join-Path $workingPath 'packages'
$cachePath = if ([string]::IsNullOrWhiteSpace($PackageCache)) { $internalCache } else { Resolve-YamuseFileSystemPath $PackageCache }
$folders = [ordered]@{ OutputFolder=$outputPath; WorkFolder=$workingPath }
if ($publishedPath) { $folders.PublishedFolder = $publishedPath }
if ($cachePath -ne $internalCache) { $folders.PackageCache = $cachePath }
Assert-DisjointPackageFolders $folders
foreach ($folder in $folders.Values) { Assert-PackagingPath $folder }
Assert-PackagingPath $cachePath
$inputFiles = $null
$sourceInventorySha256 = $null
if ($VerificationReceipt) {
    if (!$publishedPath) { throw 'VerificationReceipt requires PublishedFolder.' }
    $verified = Read-PackagingVerification (Resolve-YamuseFileSystemPath $VerificationReceipt) $publishedPath $Version
    if (($ExpectedProductSha256 -and $ExpectedProductSha256 -ne $verified.productDllSha256) -or
        ($ExpectedAppHostSha256 -and $ExpectedAppHostSha256 -ne $verified.mainExecutableSha256)) {
        throw 'Explicit package hashes differ from the full verification receipt.'
    }
    $ExpectedProductSha256 = $verified.productDllSha256
    $ExpectedAppHostSha256 = $verified.mainExecutableSha256
    $sourceInventorySha256 = $verified.publishInventorySha256
}
if ($publishedPath) {
    # Reject preview/stable mismatches before creating folders or downloading tools.
    $inputFiles = Get-PublishedFileInventory $publishedPath
    if (!$inputFiles.ContainsKey('YMusic.Native.exe') -or !$inputFiles.ContainsKey('YMusic.Native.dll')) { throw 'PublishedFolder must contain the Yamuse executable and DLL.' }
    if ($ExpectedProductSha256 -and $inputFiles['YMusic.Native.dll'].sha256 -ne $ExpectedProductSha256) {
        throw 'Published product DLL does not match the caller-approved SHA-256.'
    }
    $sourceAppHost = Assert-YamuseAppHost (Join-Path $publishedPath 'YMusic.Native.exe') $inputFiles['YMusic.Native.exe'].sha256
    if ($ExpectedAppHostSha256 -and $sourceAppHost -ne $ExpectedAppHostSha256) { throw 'Published apphost does not match the caller-approved SHA-256.' }
    Assert-PackageProductVersion (Join-Path $publishedPath 'YMusic.Native.dll') $Version
    if ($sourceInventorySha256) { Assert-PublishedInventorySha256 $inputFiles $sourceInventorySha256 }
    else { $sourceInventorySha256 = Get-PublishedInventorySha256 $inputFiles }
}
else {
    $source = Join-Path $projectRoot 'src\YMusic.Native'
    Assert-PackagingPath $source -Required
    $sourceFiles = @(Get-ChildItem -LiteralPath $source -File | Where-Object {
        $_.Extension -in @('.cs','.xaml','.csproj','.manifest','.ico','.resx') -or $_.Name -eq 'packages.lock.json'
    })
    $assetNames = @('Yamuse.ico','Yamuse.png','Yamuse.svg')
    $sourceAssets = foreach ($name in $assetNames) { Join-Path $source "Assets\$name" }
    foreach ($file in @($sourceFiles.FullName) + @($sourceAssets) + @((Join-Path $PSScriptRoot 'PinnedRuntime.targets'),(Join-Path $projectRoot 'global.json'))) {
        Assert-PackagingPath $file -File -Required
    }
    [xml]$sourceProject = Get-Content -LiteralPath (Join-Path $source 'YMusic.Native.csproj') -Raw
    $sourceVersion = $sourceProject.SelectNodes('/Project/PropertyGroup/Version')
    $assemblyVersion = $sourceProject.SelectNodes('/Project/PropertyGroup/AssemblyVersion')
    if ($sourceVersion.Count -ne 1 -or $sourceVersion[0].InnerText -cne $Version -or
        $assemblyVersion.Count -ne 1 -or $assemblyVersion[0].InnerText -cne "$Version.0") {
        throw 'Source Version and AssemblyVersion must match the requested stable Version.'
    }
}
$pins = @(
    @{ id='velopack'; version=$velopackVersion; sha256='D57275692B299751A12EE3E18283B5F7FE3E55339F96B1623345DA095DC484ED' }
    @{ id='vpk'; version=$velopackVersion; sha256='AC9BE73846D9A7A76483F8684ABD1503CD50703B992727429DCDC045589C5E08' }
    @{ id='microsoft.netcore.app.runtime.win-x64'; version=$runtimeVersion; sha256='7423F58917342B9E4BF276FB164D5726D2C90FE2FE989C552B9E39056256A035' }
    @{ id='microsoft.windowsdesktop.app.runtime.win-x64'; version=$runtimeVersion; sha256='A036C174BAC57A716E21DEDCCF96459804E4D8D8C31A08E3A8DE649BF7684497' }
    @{ id='microsoft.netcore.app.host.win-x64'; version=$runtimeVersion; sha256='41D6989224D9E273880072041C0FA7A2D8C92F1CF11E989E8967D3D589A066AD' }
    @{ id='microsoft.windows.sdk.net.ref'; version='10.0.19041.57'; sha256='90B18F2CF7D7A23F5B0BFE2B86D8DDA45881481F677CE36ECCAF6E9AB50B6B4B' }
    @{ id='microsoft.aspnetcore.app.runtime.win-x64'; version=$runtimeVersion; sha256='D2A18C23269C91106C7D87695600F7FC25D56672DCDB9C59ED947F8F71611A72' }
)
foreach ($pin in $pins) {
    Assert-PackagingPath (Join-Path $cachePath "$($pin.id).$($pin.version).nupkg") -File
}
$licenses = Join-Path $PSScriptRoot 'licenses'
Assert-PackagingPath $licenses -Required
$licenseFiles = @(foreach ($name in 'Velopack-LICENSE.txt','Microsoft.NETCore-LICENSE.txt','Microsoft.NETCore-THIRD-PARTY-NOTICES.txt','Microsoft.WindowsDesktop-LICENSE.txt') {
    Get-Item -LiteralPath (Join-Path $licenses $name)
})
$sourceFeed = Join-Path $PSScriptRoot 'update-feed.json'
$projectNoticesSource = Join-Path $projectRoot 'THIRD-PARTY-NOTICES.txt'
$projectLicenseSource = Join-Path $projectRoot 'LICENSE'
$sourceIcon = Join-Path $projectRoot 'src\YMusic.Native\Assets\Yamuse.ico'
foreach ($file in @($sourceFeed,$projectNoticesSource,$projectLicenseSource,$sourceIcon) + @($licenseFiles.FullName)) {
    Assert-PackagingPath $file -File -Required
}
$dotnetExecutable = Resolve-YamuseDotnet $DotnetPath
$actualSdk = Invoke-YamuseDotnet $dotnetExecutable $projectRoot @('--version')
if ($LASTEXITCODE -ne 0 -or ([string]$actualSdk).Trim() -cne $sdkVersion) { throw "Packaging requires the pinned .NET SDK $sdkVersion." }
if (Test-Path -LiteralPath $outputPath) {
    if (@(Get-ChildItem -LiteralPath $outputPath -Force).Count -ne 0) { throw 'OutputFolder must be empty; existing releases are never overwritten.' }
}
$sdkWorkPath = $workingPath
if (!$publishedPath -and $workingPath -match '%[0-9a-f]{2}') {
    # MSBuild can decode percent escapes in implicit project and restored pack paths.
    # Build under an ordinary path and copy the verified output back by literal name.
    $sdkParent = [IO.Path]::GetDirectoryName($workingPath)
    while ($sdkParent -and $sdkParent -match '%[0-9a-f]{2}') { $sdkParent = [IO.Path]::GetDirectoryName($sdkParent) }
    if (!$sdkParent -or $sdkParent -eq [IO.Path]::GetPathRoot($sdkParent)) {
        $sdkParent = Join-Path $projectRoot 'work'
        if ($sdkParent -match '%[0-9a-f]{2}') { throw 'Use a WorkFolder with a non-root ancestor free of MSBuild percent escapes.' }
    }
    $sdkWorkPath = Join-Path $sdkParent ('.yamuse-sdk-' + [guid]::NewGuid().ToString('N'))
    $folders.SdkWorkFolder = $sdkWorkPath
    Assert-DisjointPackageFolders $folders
    Assert-PackagingPath $sdkWorkPath
}
$stage = New-PackagingWorkStage $workingPath
if ($sdkWorkPath -ne $workingPath) {
    New-Item -ItemType Directory -Path $sdkWorkPath | Out-Null
    Assert-PackagingPath $sdkWorkPath -Required
}
if ($publishedPath) {
    Copy-PackagingInventory $publishedPath $stage $inputFiles
    Assert-PublishedInventorySha256 (Get-PublishedFileInventory $stage) $sourceInventorySha256
}
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
New-Item -ItemType Directory -Path $cachePath -Force | Out-Null

# Build caches stay with the staged SDK project. There is no app installation or launch.
$buildEnvironment = New-YamuseDotnetEnvironment (Join-Path $sdkWorkPath 'dotnet-home') (Join-Path $sdkWorkPath 'nuget')

function Invoke-DotNet([string[]]$Arguments) {
    Invoke-YamuseDotnet $dotnetExecutable $projectRoot $Arguments $buildEnvironment
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE." }
}
function Get-PinnedPackage([string]$Id, [string]$PackageVersion, [string]$Sha256) {
    $name = "$Id.$PackageVersion.nupkg"
    $target = Join-Path $cachePath $name
    Assert-PackagingPath $target -File
    if (![IO.File]::Exists($target)) {
        $uri = "https://api.nuget.org/v3-flatcontainer/$Id/$PackageVersion/$name"
        $temporary = Join-Path $cachePath ('.' + [guid]::NewGuid().ToString('N') + '.download')
        $owned = $false
        try {
            $reservation = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            $owned = $true
            $reservation.Dispose()
            Invoke-WebRequest -Uri $uri -OutFile $temporary -TimeoutSec 120 -UseBasicParsing
            Assert-PackagingPath $temporary -File -Required
            if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -cne $Sha256) { throw "Pinned SHA-256 mismatch: $name" }
            try {
                [IO.File]::Move($temporary, $target)
                $owned = $false
            } catch [IO.IOException] {
                if (![IO.File]::Exists($target)) { throw }
            }
        } finally {
            if ($owned -and [IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
        }
    }
    # A concurrent producer may win the move. Never replace its file.
    Assert-PackagingPath $target -File -Required
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -cne $Sha256) { throw "Pinned SHA-256 mismatch: $name" }
    return $target
}
$pinnedPackages = @(foreach ($pin in $pins) { Get-PinnedPackage $pin.id $pin.version $pin.sha256 })
Invoke-DotNet (@('nuget','verify','--all') + $pinnedPackages + @('--configfile',(Join-Path $PSScriptRoot 'NuGet.Config')))
$localConfig = Join-Path $sdkWorkPath 'LocalFeed.Config'
$localSource = [System.Security.SecurityElement]::Escape((Set-YamuseLocalNuGetSource $cachePath $buildEnvironment))
"<configuration><packageSources><clear/><add key=`"verified-local-packages`" value=`"$localSource`"/></packageSources></configuration>" | Set-Content -LiteralPath $localConfig -Encoding utf8
$toolPath = Join-Path $sdkWorkPath 'vpk'
Invoke-DotNet @('tool','install','vpk','--version',$velopackVersion,'--tool-path',$toolPath,'--configfile',$localConfig)
$vpk = Join-Path $toolPath '.store/vpk/1.2.161/vpk/1.2.161/tools/net10.0/any/vpk.dll'
if (!(Test-Path -LiteralPath $vpk -PathType Leaf)) { throw 'The pinned vpk .NET 10 entry point is missing.' }

if (!$publishedPath) {
    $sdkPublish = Join-Path $sdkWorkPath 'publish'
    if ($sdkPublish -ne $stage) { New-Item -ItemType Directory -Path $sdkPublish | Out-Null }
    $sourceStage = Join-Path $sdkWorkPath 'source\src\YMusic.Native'
    New-Item -ItemType Directory -Path $sourceStage -Force | Out-Null
    $configStage = Join-Path $sdkWorkPath 'source\tools\packaging'
    New-Item -ItemType Directory -Path $configStage -Force | Out-Null
    Copy-Item -LiteralPath $sourceFeed -Destination $configStage
    $licenseStage = Join-Path $configStage 'licenses'
    New-Item -ItemType Directory -Path $licenseStage | Out-Null
    foreach ($file in $licenseFiles) { Copy-Item -LiteralPath $file.FullName -Destination $licenseStage }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.txt') -Destination (Join-Path $sdkWorkPath 'source\THIRD-PARTY-NOTICES.txt')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination (Join-Path $sdkWorkPath 'source\LICENSE')
    $assetsStage = Join-Path $sourceStage 'Assets'
    New-Item -ItemType Directory -Path $assetsStage -Force | Out-Null
    foreach ($file in $sourceAssets) {
        Copy-Item -LiteralPath $file -Destination $assetsStage
    }
    # Copy only source files and the explicit branding assets. Never copy bin/obj,
    # account data, a previous package, or the live portable app directory.
    foreach ($file in $sourceFiles) { Copy-Item -LiteralPath $file.FullName -Destination $sourceStage }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PinnedRuntime.targets') -Destination (Join-Path $sourceStage 'Directory.Build.targets')
    $project = Join-Path $sourceStage 'YMusic.Native.csproj'
    Copy-Item -LiteralPath (Join-Path $projectRoot 'global.json') -Destination (Join-Path $sdkWorkPath 'source\global.json')
    $restore = @('restore',$project,'--configfile',(ConvertTo-YamuseMSBuildPath $localConfig),'--runtime','win-x64','-p:SelfContained=true','--force-evaluate','--disable-build-servers')
    if ($localConfig.Contains(';') -or $cachePath.Contains(';') -or $cachePath.Contains('%')) {
        $restore += '-p:RestoreUseStaticGraphEvaluation=true'
    }
    Invoke-DotNet $restore
    # The RID-specific lock is generated only in staging. It is saved as build evidence.
    $publish = @('publish',$project,'-c','Release','-r','win-x64','--self-contained','true','--no-restore','--disable-build-servers','-o',(ConvertTo-YamuseMSBuildPath $sdkPublish),
        '-p:PublishSingleFile=false','-p:PublishTrimmed=false','-p:DebugType=embedded')
    if ($sdkWorkPath.Contains(';')) {
        # The SDK otherwise treats the local NuGet cache's SourceRoot as a list.
        $publish += '-p:EnableSourceLink=false'
    }
    Invoke-DotNet $publish
    if ($sdkPublish -ne $stage) {
        $sdkFiles = Get-PublishedFileInventory $sdkPublish
        Assert-PackagingPath $stage
        Copy-PackagingInventory $sdkPublish $stage $sdkFiles
        Assert-PublishedInventorySha256 (Get-PublishedFileInventory $stage) (Get-PublishedInventorySha256 $sdkFiles)
    }
    $sdkLock = Join-Path $sourceStage 'packages.lock.json'
    $publishLock = Join-Path $workingPath 'publish.packages.lock.json'
    Assert-PackagingPath $sdkLock -File -Required
    Assert-PackagingPath $publishLock -File
    [IO.File]::Copy($sdkLock,$publishLock)
}
$stageFiles = Get-PublishedFileInventory $stage
if ($publishedPath) {
    Assert-PublishedInventorySha256 $stageFiles $sourceInventorySha256
    $stageFiles = $inputFiles
} else { $sourceInventorySha256 = Get-PublishedInventorySha256 $stageFiles }
Assert-PackageProductVersion (Join-Path $stage 'YMusic.Native.dll') $Version
$productHash = (Get-FileHash -LiteralPath (Join-Path $stage 'YMusic.Native.dll') -Algorithm SHA256).Hash
if ($ExpectedProductSha256 -and $productHash -ne $ExpectedProductSha256) { throw 'Published product DLL does not match the caller-approved SHA-256.' }
$appHostHash = Assert-YamuseAppHost (Join-Path $stage 'YMusic.Native.exe') $stageFiles['YMusic.Native.exe'].sha256
if ($ExpectedAppHostSha256 -and $appHostHash -ne $ExpectedAppHostSha256) { throw 'Staged apphost does not match the caller-approved SHA-256.' }
$additions = [ordered]@{
    'update-feed.json'=$sourceFeed
    'Legal/Third-Party-Notices.txt'=$projectNoticesSource
    'Legal/Yamuse-LICENSE.txt'=$projectLicenseSource
}
foreach ($file in $licenseFiles) {
    $additions["Legal/$($file.Name)"] = $file.FullName
}
$expectedStageFiles = Copy-PackageStageAdditions $stage $stageFiles $additions
$expectedStageInventorySha256 = Get-PublishedInventorySha256 $expectedStageFiles
Assert-PublishedInventorySha256 (Get-PublishedFileInventory $stage) $expectedStageInventorySha256
$projectNoticesHash = $expectedStageFiles['Legal/Third-Party-Notices.txt'].sha256
$projectLicenseHash = $expectedStageFiles['Legal/Yamuse-LICENSE.txt'].sha256
$iconHash = (Get-FileHash -LiteralPath $sourceIcon -Algorithm SHA256).Hash
$stageIcon = Join-Path $stage 'Assets\Yamuse.ico'
if (!(Test-Path -LiteralPath $stageIcon) -or (Get-FileHash -LiteralPath $stageIcon -Algorithm SHA256).Hash -ne $iconHash) { throw 'Published Yamuse icon is missing or differs from the approved source.' }
$runtimeProof = Assert-PinnedPublishRuntime $stage $Version $expectedStageFiles

# No --framework: .NET 10 is bundled. No --msi: the standard Setup is per-user,
# uses LocalAppData, and requires no administrator elevation.
$outputLease = Open-PackagingOutput $outputPath
try {
    Invoke-DotNet @($vpk,'pack','--packId',$packId,'--packVersion',$Version,'--packDir',$stage,'--mainExe','YMusic.Native.exe','--packTitle','Yamuse','--packAuthors','senduora','--icon',$stageIcon,'--runtime','win-x64','--channel','win','--shortcuts','Desktop,StartMenuRoot','--outputDir',$outputPath)
    & (Join-Path $PSScriptRoot 'Rename-ReleaseAssets.ps1') -OutputFolder $outputPath -Version $Version | Out-Null
    & (Join-Path $PSScriptRoot 'Inspect-Package.ps1') -OutputFolder $outputPath -ExpectedVersion $Version -ExpectedPackId $packId -ExpectedProductSha256 $productHash -ExpectedAppHostSha256 $appHostHash -ExpectedProjectNoticesSha256 $projectNoticesHash -ExpectedProjectLicenseSha256 $projectLicenseHash -ExpectedIconSha256 $iconHash -ReferencePublishedFolder $stage -ExpectedStageInventorySha256 $expectedStageInventorySha256 -OutputLease $outputLease
    if (!$?) { throw 'Package inspection failed.' }
    [pscustomobject]@{
        version=$Version; packId=$packId; product='Yamuse'; authors='senduora'; velopack=$velopackVersion; runtime=$runtimeVersion; productDllSha256=$productHash; mainExecutableSha256=$appHostHash; projectNoticesSha256=$projectNoticesHash; projectLicenseSha256=$projectLicenseHash; iconSha256=$iconHash;
        sdk=$actualSdk; dotnetPath=$dotnetExecutable; configuration='Self-contained Windows x64, public GitHub feed, explicit apply'; runtimeValidation=$runtimeProof;
        workFolder=$workingPath; sdkWorkFolder=$sdkWorkPath; sourcePublishInventorySha256=$sourceInventorySha256; stagePublishInventorySha256=$expectedStageInventorySha256; packageSource='https://api.nuget.org/v3/index.json';
        packages=@($pinnedPackages | ForEach-Object { [pscustomobject]@{file=[IO.Path]::GetFileName($_);sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash} })
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputPath 'build-provenance.json') -Encoding utf8
}
finally { $outputLease.Dispose() }
Write-Output "Local package ready: $outputPath"
