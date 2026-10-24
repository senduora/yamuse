[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputFolder,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$folder = [IO.Path]::GetFullPath($OutputFolder)
$names = [ordered]@{
    'YMusic.Native-win-Setup.exe' = 'Yamuse-win-Setup.exe'
    'YMusic.Native-win-Portable.zip' = 'Yamuse-win-Portable.zip'
    "YMusic.Native-$Version-full.nupkg" = "Yamuse-$Version-full.nupkg"
}
$hashes = @{}
foreach ($old in $names.Keys) {
    $source = Join-Path $folder $old
    if (!(Test-Path -LiteralPath $source -PathType Leaf) -or (Test-Path -LiteralPath (Join-Path $folder $names[$old]))) { throw 'Expected fresh Velopack output with no branded-name collision.' }
    $hashes[$old] = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
}
$indexes = @{}
foreach ($name in @('releases.win.json','assets.win.json','RELEASES')) {
    $content = [IO.File]::ReadAllText((Join-Path $folder $name))
    if (!$content.Contains("YMusic.Native-$Version-full.nupkg")) { throw 'Release index does not reference the expected full package.' }
    foreach ($old in $names.Keys) { $content = $content.Replace($old, $names[$old]) }
    $indexes[$name] = $content
}
foreach ($old in $names.Keys) {
    Rename-Item -LiteralPath (Join-Path $folder $old) -NewName $names[$old]
    if ((Get-FileHash -LiteralPath (Join-Path $folder $names[$old]) -Algorithm SHA256).Hash -ne $hashes[$old]) { throw 'Release bytes changed while renaming.' }
}
foreach ($name in $indexes.Keys) { [IO.File]::WriteAllText((Join-Path $folder $name), $indexes[$name], [Text.UTF8Encoding]::new($false)) }
[pscustomobject]@{ version=$Version; packageId='YMusic.Native'; product='Yamuse'; binaryBytesUnchanged=$true;
    files=@($names.Keys | ForEach-Object { [pscustomobject]@{oldName=$_;name=$names[$_];sha256=$hashes[$_]} }) }
