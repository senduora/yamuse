[CmdletBinding(DefaultParameterSetName = 'Attach')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Attach')]
    [ValidateRange(1, 2147483647)]
    [int]$RootProcessId,

    [Parameter(Mandatory = $true, ParameterSetName = 'Launch')]
    [ValidateNotNullOrEmpty()]
    [string]$ExecutablePath,

    [ValidateRange(1, 86400)]
    [int]$WarmupSeconds = 10,

    [ValidateRange(1, 86400)]
    [int]$DurationSeconds = 30,

    [ValidateRange(1, 600)]
    [int]$MainWindowTimeoutSeconds = 30,

    [string]$ResultsDirectory = (Join-Path (Get-Location).Path 'results'),

    [switch]$TestMode
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Скрипт предназначен для Windows.'
}
if (-not $TestMode -and ($WarmupSeconds -lt 10 -or $DurationSeconds -lt 30)) {
    throw 'Для сравнения нужны WarmupSeconds >= 10 и DurationSeconds >= 30. TestMode допускает короткий технический тест, непригодный для сравнения.'
}

# CIM timestamps have microsecond precision; Process.StartTime has 100 ns precision.
function Get-CreationKey {
    param([int]$ProcessIdValue, [datetime]$CreationUtc)
    [long]$creationTicks = $CreationUtc.ToUniversalTime().Ticks
    $creationTicks -= $creationTicks % 10
    return ('{0}:{1}' -f $ProcessIdValue, $creationTicks)
}

function Add-TrackedProcess {
    param($State, [int]$ProcessIdValue, [datetime]$ExpectedCreationUtc, $KnownProcess = $null)
    $processObject = $KnownProcess
    $identity = Get-CreationKey $ProcessIdValue $ExpectedCreationUtc
    if ($State.Tracked.ContainsKey($identity)) { return $State.Tracked[$identity] }
    try {
        if ($null -eq $processObject) {
            $processObject = [Diagnostics.Process]::GetProcessById($ProcessIdValue)
        }
        # Keep an OS handle: after an observed process exits its final CPU time can
        # still be read, and a new process reusing its PID is a different identity.
        $null = $processObject.Handle
        $actualCreationUtc = $processObject.StartTime.ToUniversalTime()
        if ((Get-CreationKey $ProcessIdValue $actualCreationUtc) -ne $identity) {
            throw 'PID был переиспользован между снимком и открытием процесса.'
        }
        $entry = [pscustomobject]@{
            Identity = $identity
            ProcessId = $ProcessIdValue
            CreationUtc = $actualCreationUtc
            Process = $processObject
            ExitUtc = $null
            Exited = $false
            FinalCpuRead = $false
            CpuMs = [double]0
            CpuBaselineMs = $null
            SeenDuringSampling = $false
        }
        $State.Tracked[$identity] = $entry
        return $entry
    }
    catch {
        if (-not $State.FailedIdentities.ContainsKey($identity)) {
            $State.FailedIdentities[$identity] = $_.Exception.Message
        }
        if ($null -ne $processObject -and $null -eq $KnownProcess) { $processObject.Dispose() }
        return $null
    }
}

function Update-ExitedProcesses {
    param($State)
    foreach ($entry in @($State.Tracked.Values)) {
        if ($entry.FinalCpuRead) { continue }
        try {
            if ($entry.Process.HasExited) {
                $entry.Exited = $true
                $entry.ExitUtc = $entry.Process.ExitTime.ToUniversalTime()
                $entry.CpuMs = [Math]::Max($entry.CpuMs, $entry.Process.TotalProcessorTime.TotalMilliseconds)
                $entry.FinalCpuRead = $true
            }
        }
        catch { $State.ReadFailures++ }
    }
}

function Get-TreeReading {
    param($State)
    Update-ExitedProcesses $State
    # Explicit property list: command lines, environment variables and tokens are
    # neither requested nor exported. This enumerates process metadata, not files.
    $processRows = @(Get-CimInstance -ClassName Win32_Process -Property ProcessId, ParentProcessId, CreationDate -ErrorAction Stop)
    $candidates = @()
    foreach ($row in $processRows) {
        if ($null -eq $row.CreationDate) { continue }
        $createdUtc = $row.CreationDate.ToUniversalTime()
        $candidates += [pscustomobject]@{
            ProcessId = [int]$row.ProcessId
            ParentProcessId = [int]$row.ParentProcessId
            CreationUtc = $createdUtc
            Identity = (Get-CreationKey ([int]$row.ProcessId) $createdUtc)
        }
    }
    do {
        $added = $false
        foreach ($candidate in $candidates) {
            if ($State.Tracked.ContainsKey($candidate.Identity) -or $State.FailedIdentities.ContainsKey($candidate.Identity)) { continue }
            # Also retain observed descendants after their parent exits. A child
            # must have been born during the actual parent's lifetime; PID alone
            # is never sufficient to establish membership.
            $validParent = $false
            foreach ($parentEntry in @($State.Tracked.Values)) {
                if ($parentEntry.ProcessId -ne $candidate.ParentProcessId) { continue }
                if ($candidate.CreationUtc -lt $parentEntry.CreationUtc.AddTicks(-9)) { continue }
                if ($null -ne $parentEntry.ExitUtc -and $candidate.CreationUtc -gt $parentEntry.ExitUtc) { continue }
                $validParent = $true
                break
            }
            if ($validParent) {
                $newEntry = Add-TrackedProcess $State $candidate.ProcessId $candidate.CreationUtc
                if ($null -ne $newEntry) { $added = $true }
            }
        }
    } while ($added)

    [long]$workingSetBytes = 0
    [long]$privateBytes = 0
    [int]$activeCount = 0
    [double]$sampledCpuMs = 0
    $windowProcessIds = New-Object 'System.Collections.Generic.List[int]'
    foreach ($entry in @($State.Tracked.Values)) {
        if (-not $entry.Exited) {
            try {
                $entry.Process.Refresh()
                if ($entry.Process.HasExited) {
                    $entry.Exited = $true
                    $entry.ExitUtc = $entry.Process.ExitTime.ToUniversalTime()
                    $entry.CpuMs = [Math]::Max($entry.CpuMs, $entry.Process.TotalProcessorTime.TotalMilliseconds)
                    $entry.FinalCpuRead = $true
                }
                else {
                    [long]$currentWs = $entry.Process.WorkingSet64
                    [long]$currentPrivate = $entry.Process.PrivateMemorySize64
                    [double]$currentCpu = $entry.Process.TotalProcessorTime.TotalMilliseconds
                    $hasMainWindow = $entry.Process.MainWindowHandle -ne [IntPtr]::Zero
                    if (-not $entry.Process.HasExited) {
                        $workingSetBytes += $currentWs
                        $privateBytes += $currentPrivate
                        $activeCount++
                        if ($hasMainWindow) { $windowProcessIds.Add($entry.ProcessId) }
                        if ($null -ne $State.SamplingStartedUtc) { $entry.SeenDuringSampling = $true }
                    }
                    else {
                        $entry.Exited = $true
                        $entry.ExitUtc = $entry.Process.ExitTime.ToUniversalTime()
                        $currentCpu = $entry.Process.TotalProcessorTime.TotalMilliseconds
                        $entry.FinalCpuRead = $true
                    }
                    $entry.CpuMs = [Math]::Max($entry.CpuMs, $currentCpu)
                }
            }
            catch { $State.ReadFailures++ }
        }
        if ($null -ne $State.SamplingStartedUtc) {
            if ($null -eq $entry.CpuBaselineMs) {
                if ($entry.CreationUtc -ge $State.SamplingStartedUtc) {
                    $entry.CpuBaselineMs = [double]0
                    $entry.SeenDuringSampling = $true
                }
                else { $entry.CpuBaselineMs = $entry.CpuMs }
            }
            $sampledCpuMs += [Math]::Max(0, $entry.CpuMs - $entry.CpuBaselineMs)
        }
    }
    return [pscustomobject]@{
        TimestampUtc = [datetime]::UtcNow
        WorkingSetBytes = $workingSetBytes
        PrivateBytes = $privateBytes
        ProcessCount = $activeCount
        SampledCpuMs = $sampledCpuMs
        MainWindowProcessIds = @($windowProcessIds.ToArray())
    }
}

function Wait-UntilElapsed {
    param([Diagnostics.Stopwatch]$Timer, [double]$TargetSeconds)
    $remainingMs = ($TargetSeconds - $Timer.Elapsed.TotalSeconds) * 1000
    if ($remainingMs -gt 0) { Start-Sleep -Milliseconds ([int][Math]::Ceiling($remainingMs)) }
}

if ($PSCmdlet.ParameterSetName -eq 'Launch') {
    $targetItem = Get-Item -LiteralPath $ExecutablePath -ErrorAction Stop
    if ($targetItem.PSIsContainer -or $targetItem.Extension -ine '.exe') { throw 'Нужен точный путь к существующему файлу .exe.' }
    $resolvedExe = $targetItem.FullName
    $sameNameProcesses = @(Get-CimInstance -ClassName Win32_Process -Property ProcessId, Name, ExecutablePath | Where-Object { $_.Name -ieq $targetItem.Name })
    foreach ($candidate in $sameNameProcesses) {
        if ([string]::IsNullOrWhiteSpace($candidate.ExecutablePath)) {
            throw ('У процесса с тем же именем PID {0} недоступен путь. Повторный запуск отменён; выберите нужный PID и режим RootProcessId.' -f $candidate.ProcessId)
        }
        if ([string]::Equals([IO.Path]::GetFullPath($candidate.ExecutablePath), $resolvedExe, [StringComparison]::OrdinalIgnoreCase)) {
            throw ('Цель уже запущена, PID {0}. Используйте -RootProcessId {0}; второй экземпляр не запущен.' -f $candidate.ProcessId)
        }
    }
    $rootProcess = $null
}
else {
    $rootProcess = [Diagnostics.Process]::GetProcessById($RootProcessId)
    try {
        $null = $rootProcess.Handle
        $resolvedExe = $rootProcess.MainModule.FileName
        if ([string]::IsNullOrWhiteSpace($resolvedExe)) { throw 'Не удалось прочитать путь исполняемого файла цели.' }
        $targetItem = Get-Item -LiteralPath $resolvedExe -ErrorAction Stop
    }
    catch { $rootProcess.Dispose(); throw }
}

$machine = Get-CimInstance -ClassName Win32_ComputerSystem -Property NumberOfLogicalProcessors, TotalPhysicalMemory
$operatingSystem = Get-CimInstance -ClassName Win32_OperatingSystem -Property Caption, Version, BuildNumber, OSArchitecture
$processorNames = @(Get-CimInstance -ClassName Win32_Processor -Property Name | ForEach-Object { $_.Name.Trim() })
[int]$logicalProcessors = $machine.NumberOfLogicalProcessors
if ($logicalProcessors -lt 1) { throw 'Не удалось определить число логических процессоров.' }
$hashBefore = (Get-FileHash -LiteralPath $resolvedExe -Algorithm SHA256).Hash
$fileVersion = $targetItem.VersionInfo.FileVersion
$resultsFullPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ResultsDirectory)
$null = [IO.Directory]::CreateDirectory($resultsFullPath)
$runId = ([datetime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')) + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$csvPath = Join-Path $resultsFullPath ($runId + '.samples.csv')
$jsonPath = Join-Path $resultsFullPath ($runId + '.summary.json')
$utf8 = New-Object Text.UTF8Encoding($false)
$samples = New-Object 'System.Collections.Generic.List[object]'
$state = [pscustomobject]@{
    Tracked = @{}
    FailedIdentities = @{}
    ReadFailures = 0
    SamplingStartedUtc = $null
}
$mode = $PSCmdlet.ParameterSetName
$startupMs = $null
$startupStatus = 'not_measured_attach'
$startupWindowIds = @()
$samplingTimer = New-Object Diagnostics.Stopwatch
$warmupTimer = New-Object Diagnostics.Stopwatch
$measurementError = $null
$completed = $false
$samplingEndedUtc = $null
$rootCreationUtc = $null
$hashAfter = $null

try {
    if ($mode -eq 'Launch') {
        $startupTimer = [Diagnostics.Stopwatch]::StartNew()
        $startInfo = New-Object Diagnostics.ProcessStartInfo
        $startInfo.FileName = $resolvedExe
        $startInfo.WorkingDirectory = $targetItem.DirectoryName
        $startInfo.UseShellExecute = $false
        $rootProcess = [Diagnostics.Process]::Start($startInfo)
        $RootProcessId = $rootProcess.Id
    }
    $rootCreationUtc = $rootProcess.StartTime.ToUniversalTime()
    $rootEntry = Add-TrackedProcess $state $RootProcessId $rootCreationUtc $rootProcess
    if ($null -eq $rootEntry) { throw 'Не удалось закрепить PID и время создания корневого процесса; замер отменён.' }

    if ($mode -eq 'Launch') {
        $startupStatus = 'timeout_no_main_window_observed'
        do {
            $reading = Get-TreeReading $state
            if ($reading.MainWindowProcessIds.Count -gt 0) {
                $startupMs = $startupTimer.Elapsed.TotalMilliseconds
                $startupWindowIds = $reading.MainWindowProcessIds
                $startupStatus = 'first_main_window_observed_proxy'
                break
            }
            if ($reading.ProcessCount -eq 0) { throw 'Запущенное дерево завершилось до появления наблюдаемого главного окна.' }
            Start-Sleep -Milliseconds 100
        } while ($startupTimer.Elapsed.TotalSeconds -lt $MainWindowTimeoutSeconds)
        $startupTimer.Stop()
    }

    Write-Host ('Прогрев {0} с; затем замер {1} с, интервал 1 с. Процессы останутся открытыми.' -f $WarmupSeconds, $DurationSeconds)
    $warmupTimer.Start()
    do {
        Wait-UntilElapsed $warmupTimer ([Math]::Min($WarmupSeconds, $warmupTimer.Elapsed.TotalSeconds + 1))
        $baseline = Get-TreeReading $state
        if ($baseline.ProcessCount -eq 0) { throw 'Дерево процессов завершилось во время прогрева.' }
    } while ($warmupTimer.Elapsed.TotalSeconds -lt $WarmupSeconds)
    $warmupTimer.Stop()

    $state.SamplingStartedUtc = $baseline.TimestampUtc
    foreach ($entry in @($state.Tracked.Values)) {
        $entry.CpuBaselineMs = $entry.CpuMs
        $entry.SeenDuringSampling = -not $entry.Exited
    }
    $samplingTimer.Start()
    [double]$previousElapsed = 0
    [double]$previousCpuMs = 0
    [int]$sampleNumber = 1
    do {
        Wait-UntilElapsed $samplingTimer ([Math]::Min($DurationSeconds, $sampleNumber))
        $reading = Get-TreeReading $state
        $elapsed = $samplingTimer.Elapsed.TotalSeconds
        $interval = $elapsed - $previousElapsed
        $cpuDelta = [Math]::Max(0, $reading.SampledCpuMs - $previousCpuMs)
        $samples.Add([pscustomobject]@{
            Sample = $sampleNumber
            TimestampUtc = $reading.TimestampUtc.ToString('o')
            ElapsedSeconds = $elapsed
            IntervalSeconds = $interval
            ProcessCount = $reading.ProcessCount
            WorkingSetBytesSum = $reading.WorkingSetBytes
            PrivateBytesSum = $reading.PrivateBytes
            CpuMillisecondsDelta = $cpuDelta
            CpuPercentNormalized = 100 * $cpuDelta / (1000 * $interval * $logicalProcessors)
            CpuMillisecondsCumulative = $reading.SampledCpuMs
        })
        $previousElapsed = $elapsed
        $previousCpuMs = $reading.SampledCpuMs
        $sampleNumber++
        if ($reading.ProcessCount -eq 0) { throw 'Дерево завершилось до конца окна замера; сохранён частичный результат.' }
    } while ($samplingTimer.Elapsed.TotalSeconds -lt $DurationSeconds)
    $completed = $true
}
catch { $measurementError = $_.Exception.Message }
finally {
    $samplingTimer.Stop()
    $warmupTimer.Stop()
    if ($null -ne $state.SamplingStartedUtc) { $samplingEndedUtc = [datetime]::UtcNow }
    try { $hashAfter = (Get-FileHash -LiteralPath $resolvedExe -Algorithm SHA256).Hash }
    catch { if ($null -eq $measurementError) { $measurementError = 'После замера не удалось повторно прочитать SHA256 исполняемого файла.' } }
    if ($hashBefore -ne $hashAfter) { $completed = $false }

    $sampleArray = @($samples.ToArray())
    [double]$totalCpuMs = 0
    if ($sampleArray.Count -gt 0) { $totalCpuMs = $sampleArray[-1].CpuMillisecondsCumulative }
    $processIdentities = @($state.Tracked.Values | Sort-Object CreationUtc, ProcessId | ForEach-Object {
        [pscustomobject]@{
            ProcessId = $_.ProcessId
            CreationUtc = $_.CreationUtc.ToString('o')
            ExitUtc = $(if ($null -ne $_.ExitUtc) { $_.ExitUtc.ToString('o') } else { $null })
            SeenDuringSampling = $_.SeenDuringSampling
        }
    })
    try { $runtimeDescription = [System.Runtime.InteropServices.RuntimeInformation]::FrameworkDescription }
    catch { $runtimeDescription = [Environment]::Version.ToString() }
    $summary = [ordered]@{
        SchemaVersion = 1
        RunId = $runId
        CompletedRequestedWindow = $completed
        TestOnly = [bool]$TestMode
        ObservationComplete = ($state.ReadFailures -eq 0 -and $state.FailedIdentities.Count -eq 0)
        Error = $measurementError
        Mode = $mode
        ExecutablePath = $resolvedExe
        ExecutableFileVersion = $fileVersion
        ExecutableSha256Before = $hashBefore
        ExecutableSha256After = $hashAfter
        ExecutableUnchanged = ($hashBefore -eq $hashAfter)
        RootProcessId = $RootProcessId
        RootCreationUtc = $(if ($null -ne $rootCreationUtc) { $rootCreationUtc.ToString('o') } else { $null })
        Startup = [ordered]@{
            Status = $startupStatus
            FirstObservedMainWindowMilliseconds = $startupMs
            TimeoutSeconds = $MainWindowTimeoutSeconds
            WindowProcessIds = $startupWindowIds
            Meaning = 'Proxy: launch call to first observed nonzero MainWindowHandle; may include a splash window; does not measure login, library loading or playback readiness.'
        }
        Measurement = [ordered]@{
            WarmupRequestedSeconds = $WarmupSeconds
            WarmupActualSeconds = $warmupTimer.Elapsed.TotalSeconds
            SamplingRequestedSeconds = $DurationSeconds
            SamplingActualSeconds = $samplingTimer.Elapsed.TotalSeconds
            IntervalRequestedSeconds = 1
            SamplingStartedUtc = $(if ($null -ne $state.SamplingStartedUtc) { $state.SamplingStartedUtc.ToString('o') } else { $null })
            SamplingEndedUtc = $(if ($null -ne $samplingEndedUtc) { $samplingEndedUtc.ToString('o') } else { $null })
            SampleCount = $sampleArray.Count
            CpuMilliseconds = $totalCpuMs
            CpuPercentNormalizedAverage = $(if ($samplingTimer.Elapsed.TotalSeconds -gt 0) { 100 * $totalCpuMs / (1000 * $samplingTimer.Elapsed.TotalSeconds * $logicalProcessors) } else { $null })
            CpuPercentNormalizedPeakSample = $(if ($sampleArray.Count -gt 0) { ($sampleArray | Measure-Object CpuPercentNormalized -Maximum).Maximum } else { $null })
            WorkingSetBytesSumAverage = $(if ($sampleArray.Count -gt 0) { ($sampleArray | Measure-Object WorkingSetBytesSum -Average).Average } else { $null })
            WorkingSetBytesSumPeakSample = $(if ($sampleArray.Count -gt 0) { ($sampleArray | Measure-Object WorkingSetBytesSum -Maximum).Maximum } else { $null })
            PrivateBytesSumAverage = $(if ($sampleArray.Count -gt 0) { ($sampleArray | Measure-Object PrivateBytesSum -Average).Average } else { $null })
            PrivateBytesSumPeakSample = $(if ($sampleArray.Count -gt 0) { ($sampleArray | Measure-Object PrivateBytesSum -Maximum).Maximum } else { $null })
            ProcessCountAverage = $(if ($sampleArray.Count -gt 0) { ($sampleArray | Measure-Object ProcessCount -Average).Average } else { $null })
            ProcessCountPeakSample = $(if ($sampleArray.Count -gt 0) { ($sampleArray | Measure-Object ProcessCount -Maximum).Maximum } else { $null })
            UniqueObservedProcessesDuringSampling = @($processIdentities | Where-Object SeenDuringSampling).Count
            ProcessReadFailures = $state.ReadFailures
            UnopenedObservedProcessIdentities = $state.FailedIdentities.Count
        }
        Machine = [ordered]@{
            OsCaption = $operatingSystem.Caption
            OsVersion = $operatingSystem.Version
            OsBuild = $operatingSystem.BuildNumber
            OsArchitecture = $operatingSystem.OSArchitecture
            CpuModels = $processorNames
            LogicalProcessorsForNormalization = $logicalProcessors
            PhysicalMemoryBytes = [long]$machine.TotalPhysicalMemory
        }
        Sampler = [ordered]@{
            PowerShellVersion = $PSVersionTable.PSVersion.ToString()
            Runtime = $runtimeDescription
            Is64BitProcess = [Environment]::Is64BitProcess
            TargetApplicationRuntime = 'Not inferred from the executable hash or sampler runtime.'
        }
        ProcessIdentities = $processIdentities
        Limitations = @(
            'Working sets are summed per process: shared physical pages may be counted more than once. Private bytes are private committed virtual memory, not resident RAM.'
            'Polling is not atomic. A process and its entire ancestry born and exited between observations may be missed. Final CPU of opened exited processes is included when readable; sub-second memory peaks are not measured.'
            'Tree scope includes only the root and descendants whose parent identity/lifetime can be established. Independent services, external players and separately started instances are outside this scope.'
            'Memory averages are sample averages; CPU average is total observed CPU divided by actual sample duration and machine logical processor count. Target processes remain open.'
            'Executable hashing before launch may warm the filesystem cache. Startup detection includes polling overhead and is not a cold-start or application-readiness measurement.'
            'No command lines, window titles, profiles, cookies or tokens are collected. Executable path and process IDs are included.'
        )
    }
    try {
        if ($sampleArray.Count -gt 0) {
            $csvRows = foreach ($sample in $sampleArray) {
                $csvRow = [ordered]@{}
                foreach ($property in $sample.PSObject.Properties) {
                    if ($property.Value -is [double]) {
                        $csvRow[$property.Name] = $property.Value.ToString('R', [Globalization.CultureInfo]::InvariantCulture)
                    }
                    elseif ($property.Value -is [IFormattable]) {
                        $csvRow[$property.Name] = $property.Value.ToString($null, [Globalization.CultureInfo]::InvariantCulture)
                    }
                    else { $csvRow[$property.Name] = $property.Value }
                }
                [pscustomobject]$csvRow
            }
            $csvLines = @($csvRows | ConvertTo-Csv -NoTypeInformation)
        }
        else { $csvLines = @('"Sample","TimestampUtc","ElapsedSeconds","IntervalSeconds","ProcessCount","WorkingSetBytesSum","PrivateBytesSum","CpuMillisecondsDelta","CpuPercentNormalized","CpuMillisecondsCumulative"') }
        [IO.File]::WriteAllLines($csvPath, [string[]]$csvLines, $utf8)
        [IO.File]::WriteAllText($jsonPath, ($summary | ConvertTo-Json -Depth 10), $utf8)
    }
    finally {
        foreach ($entry in @($state.Tracked.Values)) { $entry.Process.Dispose() }
        if ($null -ne $rootProcess -and $state.Tracked.Count -eq 0) { $rootProcess.Dispose() }
    }
}

Write-Host ('JSON: {0}' -f $jsonPath)
Write-Host ('CSV:  {0}' -f $csvPath)
if ($null -ne $measurementError) { throw $measurementError }
if ($hashBefore -ne $hashAfter) { throw 'Исполняемый файл изменился во время замера. Результат не подходит для сравнения.' }
if ($state.ReadFailures -gt 0 -or $state.FailedIdentities.Count -gt 0) {
    Write-Warning 'Были пропуски чтения процессов; см. ObservationComplete и счётчики в JSON. Это неполное наблюдение.'
}
[pscustomobject]@{ SummaryPath = $jsonPath; SamplesPath = $csvPath; Completed = $completed; TestOnly = [bool]$TestMode }
