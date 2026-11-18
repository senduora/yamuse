function Resolve-YamuseFileSystemPath([string]$Path) {
    $provider = $null
    $drive = $null
    $absolute = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path, [ref]$provider, [ref]$drive)
    if ($provider.Name -cne 'FileSystem') { throw "Expected a FileSystem path: $Path" }
    $root = [IO.Path]::GetPathRoot($absolute)
    if ($absolute.Length -gt $root.Length) { return $absolute.TrimEnd('\','/') }
    return $absolute
}

function Resolve-YamuseDotnet([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { $Path = 'dotnet' }
    if ($Path -notmatch '[:\\/]') { return (Get-Command -Name $Path -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source }
    $executable = Resolve-YamuseFileSystemPath $Path
    if (!(Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'DotnetPath must identify an existing dotnet executable.' }
    return $executable
}

function Invoke-YamuseDotnet([string]$Executable, [string]$ProjectRoot, [string[]]$CommandArguments) {
    Push-Location -LiteralPath $ProjectRoot
    try { & $Executable @CommandArguments }
    finally { Pop-Location }
}
