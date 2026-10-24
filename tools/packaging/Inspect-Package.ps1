[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputFolder,
    [Parameter(Mandatory)][string]$ExpectedVersion,
    [Parameter(Mandatory)][string]$ReferencePublishedFolder,
    [string]$ExpectedPackId = 'YMusic.Native',
    [string]$ExpectedProductSha256,
    [string]$ExpectedProjectNoticesSha256,
    [string]$ExpectedProjectLicenseSha256,
    [string]$ExpectedIconSha256)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$outputPath = [IO.Path]::GetFullPath($OutputFolder)
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$referencePath = [IO.Path]::GetFullPath($ReferencePublishedFolder)
$sourceIcon = Join-Path $projectRoot 'src\YMusic.Native\Assets\Yamuse.ico'
if (!$ExpectedIconSha256) { $ExpectedIconSha256 = (Get-FileHash -LiteralPath $sourceIcon -Algorithm SHA256).Hash }
if ((Get-FileHash -LiteralPath $sourceIcon -Algorithm SHA256).Hash -ne $ExpectedIconSha256) { throw 'Expected icon hash differs from the approved source.' }
if (!$ExpectedProjectLicenseSha256) { $ExpectedProjectLicenseSha256 = (Get-FileHash -LiteralPath (Join-Path $projectRoot 'LICENSE') -Algorithm SHA256).Hash }
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
    if ($metadata.package.metadata.title -ne 'Yamuse' -or $metadata.package.metadata.authors -ne 'senduora') { throw 'Package display title/authors must be Yamuse/senduora.' }
    if ($ExpectedPackId -eq 'YMusic.Native.Prototype') { throw 'Install root must not be the existing user-data folder.' }
    $mainExeEntry = $archive.Entries | Where-Object { $_.FullName -match 'YMusic\.Native\.exe$' } | Select-Object -First 1
    if (!$mainExeEntry) { throw 'Main executable missing from full package.' }
    $mainExePath = Join-Path $referencePath 'YMusic.Native.exe'
    $mainExeHash = (Get-FileHash -LiteralPath $mainExePath -Algorithm SHA256).Hash
    if ((Get-ZipEntrySha256 $mainExeEntry) -ne $mainExeHash) { throw 'Packaged executable differs from the reference publish.' }
    $mainExeInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($mainExePath)
    if ($mainExeInfo.ProductName -ne 'Yamuse' -or $mainExeInfo.FileDescription -ne 'Yamuse') { throw 'Main executable ProductName/Title must be Yamuse.' }
    $iconEntry = $archive.Entries | Where-Object { $_.FullName -match 'Assets/Yamuse\.ico$' } | Select-Object -First 1
    if (!$iconEntry) { throw 'Required physical Yamuse icon is missing.' }
    if ((Get-ZipEntrySha256 $iconEntry) -ne $ExpectedIconSha256) { throw 'Packaged Yamuse icon differs from the approved source.' }
    $productEntry = $archive.Entries | Where-Object { $_.FullName -match 'YMusic\.Native\.dll$' } | Select-Object -First 1
    if (!$productEntry) { throw 'Product DLL missing from full package.' }
    $noticeHashes = @{
        'Velopack-LICENSE.txt'='91845DB83551C877EBBB1118E0FB92E4E527290D23B995C55DCD438B3293943F'
        'Microsoft.NETCore-LICENSE.txt'='D7A68596AB69B06F51CA278A6545148E4269A9381C26D597C13DF5D88E08CF5B'
        'Microsoft.NETCore-THIRD-PARTY-NOTICES.txt'='B60B2912DA28EAA6518593C9E2EFB5334EE062D3C42E80D8FDFA806B3DC52977'
        'Microsoft.WindowsDesktop-LICENSE.txt'='A89886665765362EB77E0F8E26602C924520041D1711B2EEDC136434FE4D01AB'
        'Third-Party-Notices.txt'=$ExpectedProjectNoticesSha256
        'Yamuse-LICENSE.txt'=$ExpectedProjectLicenseSha256
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
if (!('YamuseIconVerifier' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
public static class YamuseIconVerifier {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr LoadLibraryExW(string file, IntPtr unused, uint flags);
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr FindResourceW(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll", SetLastError=true)] static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
    delegate bool EnumName(IntPtr module, IntPtr type, IntPtr name, IntPtr parameter);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool EnumResourceNamesW(IntPtr module, IntPtr type, EnumName callback, IntPtr parameter);
    sealed class Image { public byte Width, Height; public ushort Planes, Bits; public byte[] Data; }
    static Image[] Images(string file) {
        var ico = File.ReadAllBytes(file);
        if (ico.Length < 6 || BitConverter.ToUInt16(ico,0) != 0 || BitConverter.ToUInt16(ico,2) != 1) throw new InvalidDataException("Invalid ICO header.");
        int count = BitConverter.ToUInt16(ico,4);
        if (count < 1 || count > 256 || ico.Length < 6 + count * 16) throw new InvalidDataException("Invalid ICO count.");
        var images = new Image[count];
        for (int i=0;i<count;i++) {
            int pos = 6 + i * 16;
            uint size=BitConverter.ToUInt32(ico,pos+8), offset=BitConverter.ToUInt32(ico,pos+12);
            if (size == 0 || (long)offset+size > ico.Length) throw new InvalidDataException("Invalid ICO image.");
            var data = new byte[size]; Array.Copy(ico,offset,data,0,size);
            images[i] = new Image { Width=ico[pos], Height=ico[pos+1], Planes=BitConverter.ToUInt16(ico,pos+4), Bits=BitConverter.ToUInt16(ico,pos+6), Data=data };
        }
        return images;
    }
    static byte[] Resource(IntPtr module, IntPtr name, int type) {
        var resource = FindResourceW(module,name,(IntPtr)type);
        if (resource == IntPtr.Zero) return null;
        uint size = SizeofResource(module,resource);
        if (size == 0 || size > 32*1024*1024) return null;
        var bytes = new byte[size];
        Marshal.Copy(LockResource(LoadResource(module,resource)),bytes,0,bytes.Length);
        return bytes;
    }
    static bool GroupMatches(IntPtr module, byte[] group, Image[] images) {
        if (group == null || group.Length < 6 || BitConverter.ToUInt16(group,4) != images.Length || group.Length < 6 + images.Length * 14) return false;
        foreach (var image in images) {
            bool found = false;
            for (int i=0;i<images.Length;i++) {
                int pos=6+i*14;
                if (group[pos] != image.Width || group[pos+1] != image.Height || BitConverter.ToUInt16(group,pos+4) != image.Planes || BitConverter.ToUInt16(group,pos+6) != image.Bits) continue;
                var data=Resource(module,(IntPtr)BitConverter.ToUInt16(group,pos+12),3);
                if (data != null && data.SequenceEqual(image.Data)) { found=true; break; }
            }
            if (!found) return false;
        }
        return true;
    }
    public static int[] FrameSizes(string icon) { return Images(icon).Select(image => image.Width == 0 ? 256 : (int)image.Width).ToArray(); }
    public static bool Matches(string executable, string icon) {
        var images=Images(icon);
        var module=LoadLibraryExW(executable,IntPtr.Zero,2); // DATAFILE: never executes the PE.
        if (module == IntPtr.Zero) return false;
        try {
            bool matches=false;
            EnumName callback=(handle,type,name,parameter) => { try { if (GroupMatches(handle,Resource(handle,name,14),images)) matches=true; } catch { } return true; };
            EnumResourceNamesW(module,(IntPtr)14,callback,IntPtr.Zero);
            GC.KeepAlive(callback);
            return matches;
        } finally { FreeLibrary(module); }
    }
}
'@
}
$executableIconMatches = [YamuseIconVerifier]::Matches($mainExePath, $sourceIcon)
if (!$executableIconMatches) { throw 'Main executable icon resources differ from the approved Yamuse ICO.' }
$setupIconMatches = [YamuseIconVerifier]::Matches($setup.FullName, $sourceIcon)
if (!$setupIconMatches) { throw 'Setup icon resources differ from the approved Yamuse ICO.' }
if ($setup.VersionInfo.ProductName -ne 'Yamuse' -or $setup.VersionInfo.CompanyName -ne 'senduora') { throw 'Setup ProductName/CompanyName must be Yamuse/senduora.' }
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
    product='Yamuse'; authors='senduora'; projectLicenseSha256=$ExpectedProjectLicenseSha256; iconSha256=$ExpectedIconSha256;
    mainExecutableSha256=$mainExeHash; mainExecutableProduct=$mainExeInfo.ProductName; executableIconMatches=$executableIconMatches; setupIconMatches=$setupIconMatches; iconFrameSizes=[YamuseIconVerifier]::FrameSizes($sourceIcon);
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
