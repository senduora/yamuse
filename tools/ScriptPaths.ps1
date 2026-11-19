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

$yamuseToolProcessSource = Join-Path $PSScriptRoot 'YamuseToolProcess.cs'

function ConvertTo-YamuseMSBuildPath([string]$Path) {
    # Escape MSBuild property and item separators before the value reaches the SDK.
    return [regex]::Replace($Path, '[%$@''();,=]', { param($match) '%{0:X2}' -f [int][char]$match.Value })
}

function Set-YamuseLocalNuGetSource([string]$Path, [Collections.IDictionary]$ChildEnvironment) {
    if (!$Path.Contains('%')) { return $Path }
    # NuGet expands configuration variables before resolving an absolute file URI.
    $slashPath = $Path.Replace('\', '/')
    if ($slashPath.StartsWith('//', [StringComparison]::Ordinal)) {
        $authorityEnd = $slashPath.IndexOf('/', 2)
        if ($authorityEnd -lt 3) { throw 'Expected a UNC host and share.' }
        $prefix = 'file:' + $slashPath.Substring(0, $authorityEnd + 1)
        $relative = $slashPath.Substring($authorityEnd + 1)
    }
    else {
        if ($slashPath -notmatch '^[A-Za-z]:/') { throw 'Expected an absolute package cache path.' }
        $prefix = 'file:///' + $slashPath.Substring(0, 3)
        $relative = $slashPath.Substring(3)
    }
    # Encode literal Windows names before URI parsing can interpret sequences such as %2E.
    $segments = $relative.Split('/') | ForEach-Object { [Uri]::EscapeDataString($_) }
    $value = [Uri]::new($prefix + ($segments -join '/'), [UriKind]::Absolute).AbsoluteUri
    $uri = [Uri]::new($value, [UriKind]::Absolute)
    if (!$uri.IsFile -or ![string]::Equals($uri.LocalPath, $Path, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Unable to represent the verified package cache as a literal file URI.'
    }
    # Encoded percents can resemble variables. Disarm those exact names in the child.
    foreach ($candidate in [regex]::Matches($value, '(?=%([^%=]+)%)')) {
        $ChildEnvironment[$candidate.Groups[1].Value] = $null
    }
    return $value
}

function ConvertTo-YamuseWindowsArgument([string]$Argument) {
    $escaped = [regex]::Replace($Argument, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}

function Invoke-YamuseDotnet([string]$Executable, [string]$ProjectRoot, [string[]]$CommandArguments,
        [Collections.IDictionary]$EnvironmentOverrides = @{}) {
    if (!('Yamuse.Tools.ToolProcess' -as [type])) {
        try { Add-Type -Path $yamuseToolProcessSource -ErrorAction Stop }
        catch {
            # Another runspace may have loaded this same type in the meantime.
            if (!('Yamuse.Tools.ToolProcess' -as [type])) { throw }
        }
    }
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Executable
    $start.WorkingDirectory = $ProjectRoot
    $start.Arguments = @($CommandArguments | ForEach-Object { ConvertTo-YamuseWindowsArgument $_ }) -join ' '
    foreach ($name in $EnvironmentOverrides.Keys) {
        if ($null -eq $EnvironmentOverrides[$name]) { $start.EnvironmentVariables.Remove($name) }
        else { $start.EnvironmentVariables[$name] = $EnvironmentOverrides[$name] }
    }
    $start.EnvironmentVariables['DOTNET_ADD_GLOBAL_TOOLS_TO_PATH'] = 'false'

    $process = [Yamuse.Tools.ToolProcess]::new()
    try {
        $process.Start($start)
        $stdout = $process.StandardOutput.ReadLineAsync()
        $stderr = $process.StandardError.ReadLineAsync()
        $exited = $false
        while ($true) {
            if (!$exited -and $process.WaitForExit(0)) {
                $exited = $true
                $process.DrainJob()
            }
            if ($null -eq $stdout -and $null -eq $stderr) {
                if ($exited) { break }
                $null = $process.WaitForExit(100)
                continue
            }
            $pending = @($stdout,$stderr | Where-Object { $null -ne $_ })
            $null = [Threading.Tasks.Task]::WaitAny([Threading.Tasks.Task[]]$pending, 100)
            if ($null -ne $stdout -and $stdout.IsCompleted) {
                $line = $stdout.GetAwaiter().GetResult()
                $stdout = $null
                if ($null -ne $line) {
                    Write-Output $line
                    $stdout = $process.StandardOutput.ReadLineAsync()
                }
            }
            if ($null -ne $stderr -and $stderr.IsCompleted) {
                $line = $stderr.GetAwaiter().GetResult()
                $stderr = $null
                if ($null -ne $line) {
                    Write-Error -Message $line -ErrorAction Continue
                    $stderr = $process.StandardError.ReadLineAsync()
                }
            }
        }
        $global:LASTEXITCODE = $process.ReadExitCode()
    } finally { $process.Dispose() }
}

function New-YamuseDotnetEnvironment([string]$DotnetHome, [string]$NugetPath) {
    return [ordered]@{
        DOTNET_CLI_HOME = $DotnetHome
        DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
        DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
        DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
        NUGET_PACKAGES = $NugetPath
    }
}
