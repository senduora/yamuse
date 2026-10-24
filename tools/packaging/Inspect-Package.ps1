[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputFolder,
    [Parameter(Mandatory)][string]$ExpectedVersion,
    [string]$ExpectedPackId = 'YMusic.Native',
    [string]$ExpectedProductSha256,
    [string]$ExpectedProjectNoticesSha256)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$outputPath = [IO.Path]::GetFullPath($OutputFolder)
if (!$ExpectedProjectNoticesSha256) {
    $projectNoticesSource = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\THIRD-PARTY-NOTICES.txt'))
    $ExpectedProjectNoticesSha256 = (Get-FileHash -LiteralPath $projectNoticesSource -Algorithm SHA256).Hash
}
$setups = @(Get-ChildItem -LiteralPath $outputPath -File -Filter '*Setup.exe')
$packages = @(Get-ChildItem -LiteralPath $outputPath -File -Filter '*-full.nupkg')
if ($setups.Count -ne 1 -or $packages.Count -ne 1) { throw 'Expected one Setup.exe and one full package.' }
$setup = $setups[0]
$package = $packages[0]
# Velopack 1.2.161's unsigned Setup appends the complete nupkg. Verify the
# embedded payload too, so Legal notices/product DLL are proven in Setup.
$packageHash = (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash
$setupStream = [IO.File]::OpenRead($setup.FullName)
$setupPayloadAlgorithm = [Security.Cryptography.SHA256]::Create()
try {
    $payloadOffset = $setup.Length - $package.Length
    if ($payloadOffset -lt 0) { throw 'Setup is smaller than its full package.' }
    $null = $setupStream.Seek($payloadOffset, [IO.SeekOrigin]::Begin)
    $setupPayloadHash = [BitConverter]::ToString($setupPayloadAlgorithm.ComputeHash($setupStream)).Replace('-','')
    if ($setupPayloadHash -ne $packageHash) { throw 'Setup does not embed the verified full package.' }
} finally { $setupPayloadAlgorithm.Dispose(); $setupStream.Dispose() }
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Get-ZipEntrySha256($Entry) {
    $entryStream = $Entry.Open()
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($entryStream)).Replace('-','') }
    finally { $algorithm.Dispose(); $entryStream.Dispose() }
}
$archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
    $nuspec = $archive.Entries | Where-Object { $_.FullName -like '*.nuspec' } | Select-Object -First 1
    $reader = [IO.StreamReader]::new($nuspec.Open())
    try { [xml]$metadata = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $id = $metadata.package.metadata.id
    $version = $metadata.package.metadata.version
    if ($id -ne $ExpectedPackId -or $version -ne $ExpectedVersion) { throw 'Package id/version mismatch.' }
    if ($ExpectedPackId -eq 'YMusic.Native.Prototype') { throw 'Install root must not be the existing user-data folder.' }
    if (!($archive.Entries | Where-Object { $_.FullName -match 'YMusic\.Native\.exe$' })) { throw 'Main executable missing from full package.' }
    $productEntry = $archive.Entries | Where-Object { $_.FullName -match 'YMusic\.Native\.dll$' } | Select-Object -First 1
    if (!$productEntry) { throw 'Product DLL missing from full package.' }
    $noticeHashes = @{
        'Velopack-LICENSE.txt'='91845DB83551C877EBBB1118E0FB92E4E527290D23B995C55DCD438B3293943F'
        'Microsoft.NETCore-LICENSE.txt'='D7A68596AB69B06F51CA278A6545148E4269A9381C26D597C13DF5D88E08CF5B'
        'Microsoft.NETCore-THIRD-PARTY-NOTICES.txt'='B60B2912DA28EAA6518593C9E2EFB5334EE062D3C42E80D8FDFA806B3DC52977'
        'Microsoft.WindowsDesktop-LICENSE.txt'='A89886665765362EB77E0F8E26602C924520041D1711B2EEDC136434FE4D01AB'
        'Third-Party-Notices.txt'=$ExpectedProjectNoticesSha256
    }
    foreach ($notice in $noticeHashes.Keys) {
        $noticeEntry = $archive.Entries | Where-Object { $_.FullName -match ('Legal/' + [regex]::Escape($notice) + '$') } | Select-Object -First 1
        if (!$noticeEntry) { throw "Required third-party notice missing: $notice" }
        if ((Get-ZipEntrySha256 $noticeEntry) -ne $noticeHashes[$notice]) { throw "Third-party notice differs from the verified source: $notice" }
    }
    $productHash = Get-ZipEntrySha256 $productEntry
    if ($ExpectedProductSha256 -and $productHash -ne $ExpectedProductSha256) { throw 'Packaged product DLL differs from the checked publish SHA-256.' }
    $nuspecText = $metadata.OuterXml
    $inventory = @($archive.Entries | ForEach-Object { [pscustomobject]@{name=$_.FullName;bytes=$_.Length} })
} finally { $archive.Dispose() }

# LoadLibraryEx with DATAFILE reads PE resources only; it cannot execute Setup.
if (!('PackagingManifestReader' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class PackagingManifestReader {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr LoadLibraryExW(string file, IntPtr unused, uint flags);
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr FindResourceW(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll", SetLastError=true)] static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
    public static string Read(string file) {
        var module = LoadLibraryExW(file, IntPtr.Zero, 2);
        if (module == IntPtr.Zero) throw new Win32Exception();
        try {
            var resource = FindResourceW(module, (IntPtr)1, (IntPtr)24);
            if (resource == IntPtr.Zero) throw new Win32Exception();
            var data = new byte[SizeofResource(module, resource)];
            Marshal.Copy(LockResource(LoadResource(module, resource)), data, 0, data.Length);
            return System.Text.Encoding.UTF8.GetString(data).Trim('\0','\uFEFF');
        } finally { FreeLibrary(module); }
    }
}
'@
}
$manifest = [PackagingManifestReader]::Read($setup.FullName)
if ($manifest -notmatch 'requestedExecutionLevel\s+level=["'']asInvoker["'']') { throw 'Setup must request asInvoker, not elevation.' }
$manifest | Set-Content -LiteralPath (Join-Path $outputPath 'setup.manifest.xml') -Encoding utf8
$nuspecText | Set-Content -LiteralPath (Join-Path $outputPath 'package.nuspec.xml') -Encoding utf8

$releasePath = Join-Path $outputPath 'releases.win.json'
$release = Get-Content -LiteralPath $releasePath -Raw | ConvertFrom-Json
foreach ($asset in $release.Assets) {
    if ($asset.FileName -ne [IO.Path]::GetFileName($asset.FileName)) { throw 'Unsafe release asset name.' }
    $assetFile = Join-Path $outputPath $asset.FileName
    if (!(Test-Path -LiteralPath $assetFile)) { throw 'Release asset missing.' }
    if ((Get-Item -LiteralPath $assetFile).Length -ne $asset.Size) { throw 'Release asset size mismatch.' }
    if ($asset.SHA256 -and (Get-FileHash -LiteralPath $assetFile -Algorithm SHA256).Hash -ne $asset.SHA256) { throw 'Release asset SHA-256 mismatch.' }
}
$evidence = [pscustomobject]@{
    inspectedAt=(Get-Date -Format o); packId=$id; version=$version; productDllSha256=$productHash; legalNoticesVerified=$true; projectNoticesSha256=$ExpectedProjectNoticesSha256;
    setupEmbedsVerifiedNupkg=$true; setupPackageOffset=$payloadOffset; setupEmbeddedNupkgSha256=$setupPayloadHash;
    setupFileVersion=$setup.VersionInfo.FileVersion; setupProduct=$setup.VersionInfo.ProductName;
    setupExecutionLevel='asInvoker'; setupExecuted=$false;
    setupSignature=(Get-AuthenticodeSignature -LiteralPath $setup.FullName).Status.ToString();
    installRoot='%LOCALAPPDATA%\YMusic.Native'; preservedData='%LOCALAPPDATA%\YMusic.Native.Prototype';
    preservedMusic='Documents\YMusic Native\Music';
    files=@(Get-ChildItem -LiteralPath $outputPath -File | Where-Object { $_.Name -ne 'package-evidence.json' } | ForEach-Object {
        [pscustomobject]@{name=$_.Name;bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
    }); packageEntries=$inventory
}
$evidence | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputPath 'package-evidence.json') -Encoding utf8
Write-Output "Package metadata, feed asset hashes and non-elevated Setup manifest: PASS ($version)"
