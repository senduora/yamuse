[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [AllowEmptyCollection()][string[]]$Arguments = @(),
    [Parameter(Mandatory=$true)][string]$WorkingDirectory,
    [Parameter(Mandatory=$true)][string]$LogPath,
    [Parameter(Mandatory=$true)][string]$ReceiptPath,
    [ValidateRange(1,86400)][int]$TimeoutSeconds = 1800,
    [string]$DotnetPath = 'dotnet',
    [string]$RunnerWorkFolder,
    [Collections.IDictionary]$EnvironmentOverrides = @{}
)
$ErrorActionPreference = 'Stop'
$toolsPath = Join-Path $PSScriptRoot '..\..'
. (Join-Path $toolsPath 'ScriptPaths.ps1')
. (Join-Path $toolsPath 'packaging/PublishValidation.ps1')
$projectRoot = Resolve-YamuseFileSystemPath (Join-Path $toolsPath '..')
$dotnetExecutable = Resolve-YamuseDotnet $DotnetPath
$runnerProject = Join-Path $PSScriptRoot 'PrivateDesktopRunner.csproj'
$nugetConfig = Join-Path $projectRoot 'tools/packaging/NuGet.Config'
$configuration = [ordered]@{
    Executable = Resolve-YamuseFileSystemPath $Executable
    Arguments = @($Arguments)
    WorkingDirectory = Resolve-YamuseFileSystemPath $WorkingDirectory
    LogPath = Resolve-YamuseFileSystemPath $LogPath
    ReceiptPath = Resolve-YamuseFileSystemPath $ReceiptPath
    TimeoutSeconds = $TimeoutSeconds
}
$workPath = if ([string]::IsNullOrWhiteSpace($RunnerWorkFolder)) {
    Join-Path ([IO.Path]::GetDirectoryName($configuration.ReceiptPath)) ('launcher-' + [guid]::NewGuid().ToString('N'))
} else { Resolve-YamuseFileSystemPath $RunnerWorkFolder }
Assert-PackagingPath $workPath
Assert-PackagingPath $runnerProject -File -Required
Assert-PackagingPath $nugetConfig -File -Required
foreach ($target in $configuration.LogPath,$configuration.ReceiptPath) {
    Assert-PackagingPath $target -File
    if ([IO.File]::Exists($target)) { throw "Use a new launcher output file: $target" }
    if ($target.Equals($workPath,[StringComparison]::OrdinalIgnoreCase) -or
        $target.StartsWith($workPath.TrimEnd('\','/') + '\',[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Launcher log and receipt paths must be outside RunnerWorkFolder.'
    }
}
if ([IO.Directory]::Exists($workPath)) { throw 'Use a new RunnerWorkFolder for each launcher invocation.' }
$environment = New-YamuseDotnetEnvironment (Join-Path $workPath 'dotnet-home') (Join-Path $workPath 'nuget')
foreach ($name in $EnvironmentOverrides.Keys) { $environment[$name] = $EnvironmentOverrides[$name] }
foreach ($name in 'DOTNET_CLI_HOME','NUGET_PACKAGES') {
    if ([string]::IsNullOrWhiteSpace($environment[$name])) {
        $environment[$name] = Join-Path $workPath $(if ($name -eq 'DOTNET_CLI_HOME') { 'dotnet-home' } else { 'nuget' })
    } else { $environment[$name] = Resolve-YamuseFileSystemPath $environment[$name] }
    Assert-PackagingPath $environment[$name]
}
$environment['DOTNET_GENERATE_ASPNET_CERTIFICATE'] = 'false'
$environment['DOTNET_ADD_GLOBAL_TOOLS_TO_PATH'] = 'false'

$null = [IO.Directory]::CreateDirectory($workPath)
# CreateNew reserves the folder even when two callers chose the same name.
$reservation = [IO.File]::Open((Join-Path $workPath '.launcher-run'), [IO.FileMode]::CreateNew)
$reservation.Dispose()
$outputPath = Join-Path $workPath 'bin'
$runner = Join-Path $outputPath 'PrivateDesktopRunner.dll'
# These path values become MSBuild properties. Escape its list separators and
# special characters without changing the paths used for filesystem access.
$escapedWorkPath = ConvertTo-YamuseMSBuildPath $workPath
$artifactsProperty = "-p:PrototypeArtifactsPath=$escapedWorkPath"
$escapedOutputPath = Join-Path $escapedWorkPath 'bin'
$restore = @('restore',$runnerProject,'--configfile',(ConvertTo-YamuseMSBuildPath $nugetConfig),'-v','quiet','--disable-build-servers',$artifactsProperty)
if ($nugetConfig.Contains(';')) { $restore += '-p:RestoreUseStaticGraphEvaluation=true' }
Invoke-YamuseDotnet $dotnetExecutable $projectRoot $restore $environment
if ($LASTEXITCODE -ne 0) { throw "Private desktop runner restore failed: $LASTEXITCODE" }
# NuGet emits the local cache as a SourceRoot item. The helper is built for
# local use and does not need Source Link to interpret that directory as a list.
Invoke-YamuseDotnet $dotnetExecutable $projectRoot @('build',$runnerProject,'--no-restore','-o',$escapedOutputPath,'-v','quiet','--disable-build-servers','-p:EnableSourceLink=false',$artifactsProperty) $environment
if ($LASTEXITCODE -ne 0) { throw "Private desktop runner build failed: $LASTEXITCODE" }
Assert-PackagingPath $runner -File -Required
$configurationPath = Join-Path $workPath 'invocation.json'
$ownedConfiguration = $false
try {
    $bytes = [Text.Encoding]::UTF8.GetBytes(($configuration | ConvertTo-Json -Depth 5))
    $stream = [IO.File]::Open($configurationPath, [IO.FileMode]::CreateNew)
    $ownedConfiguration = $true
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
    Invoke-YamuseDotnet $dotnetExecutable $projectRoot @($runner,$configurationPath) $environment
    $runnerExit = $LASTEXITCODE
    Write-Output ('Private desktop receipt: ' + $configuration.ReceiptPath)
    exit $runnerExit
} finally {
    if ($ownedConfiguration) { [IO.File]::Delete($configurationPath) }
}
