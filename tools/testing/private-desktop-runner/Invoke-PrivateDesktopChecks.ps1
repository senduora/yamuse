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
$runner = Join-Path $PSScriptRoot 'bin/PrivateDesktopRunner.dll'
# Always perform an incremental build so a cached launcher from an older target
# cannot survive an SDK migration. Use the selected host instead of global runtime.
& $DotnetPath build (Join-Path $PSScriptRoot 'PrivateDesktopRunner.csproj') -o (Join-Path $PSScriptRoot 'bin') -v quiet
if ($LASTEXITCODE -ne 0) { throw "Private desktop runner build failed: $LASTEXITCODE" }
$configuration = [ordered]@{
    Executable = [IO.Path]::GetFullPath($Executable)
    Arguments = @($Arguments)
    WorkingDirectory = [IO.Path]::GetFullPath($WorkingDirectory)
    LogPath = [IO.Path]::GetFullPath($LogPath)
    ReceiptPath = [IO.Path]::GetFullPath($ReceiptPath)
    TimeoutSeconds = $TimeoutSeconds
}
$configurationPath = Join-Path $PSScriptRoot ('invocation-' + [Guid]::NewGuid().ToString('N') + '.json')
try {
    [IO.File]::WriteAllText($configurationPath,($configuration | ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
    & $DotnetPath $runner $configurationPath
    $runnerExit = $LASTEXITCODE
    Write-Output ('Private desktop receipt: ' + $configuration.ReceiptPath)
    exit $runnerExit
} finally {
    if (Test-Path -LiteralPath $configurationPath) { Remove-Item -LiteralPath $configurationPath -Force }
}
