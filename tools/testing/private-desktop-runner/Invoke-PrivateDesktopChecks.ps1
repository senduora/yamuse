[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [AllowEmptyCollection()][string[]]$Arguments = @(),
    [Parameter(Mandatory=$true)][string]$WorkingDirectory,
    [Parameter(Mandatory=$true)][string]$LogPath,
    [Parameter(Mandatory=$true)][string]$ReceiptPath,
    [ValidateRange(1,86400)][int]$TimeoutSeconds = 1800,
    [string]$DotnetPath = 'dotnet'
)
$ErrorActionPreference = 'Stop'
$toolsPath = Join-Path $PSScriptRoot '..\..'
. (Join-Path $toolsPath 'ScriptPaths.ps1')
$projectRoot = Resolve-YamuseFileSystemPath (Join-Path $toolsPath '..')
$dotnetExecutable = Resolve-YamuseDotnet $DotnetPath
$runner = Join-Path $PSScriptRoot 'bin/PrivateDesktopRunner.dll'
$configuration = [ordered]@{
    Executable = Resolve-YamuseFileSystemPath $Executable
    Arguments = @($Arguments)
    WorkingDirectory = Resolve-YamuseFileSystemPath $WorkingDirectory
    LogPath = Resolve-YamuseFileSystemPath $LogPath
    ReceiptPath = Resolve-YamuseFileSystemPath $ReceiptPath
    TimeoutSeconds = $TimeoutSeconds
}
# Always perform an incremental build so a cached launcher from an older target
# cannot survive an SDK migration. Use the selected host instead of global runtime.
Invoke-YamuseDotnet $dotnetExecutable $projectRoot @('build',(Join-Path $PSScriptRoot 'PrivateDesktopRunner.csproj'),'-o',(Join-Path $PSScriptRoot 'bin'),'-v','quiet')
if ($LASTEXITCODE -ne 0) { throw "Private desktop runner build failed: $LASTEXITCODE" }
$configurationPath = Join-Path $PSScriptRoot ('invocation-' + [Guid]::NewGuid().ToString('N') + '.json')
try {
    [IO.File]::WriteAllText($configurationPath,($configuration | ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
    Invoke-YamuseDotnet $dotnetExecutable $projectRoot @($runner,$configurationPath)
    $runnerExit = $LASTEXITCODE
    Write-Output ('Private desktop receipt: ' + $configuration.ReceiptPath)
    exit $runnerExit
} finally {
    if (Test-Path -LiteralPath $configurationPath) { Remove-Item -LiteralPath $configurationPath -Force }
}
