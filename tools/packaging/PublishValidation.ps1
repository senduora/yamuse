function Assert-UniqueDependencyMembers([byte[]]$Bytes, [int]$Offset) {
    # Older PowerShell hosts have no strict JSON parser that reports duplicates.
    # The syntax check already ran. The JSON reader exposes every decoded key.
    $reader = [Runtime.Serialization.Json.JsonReaderWriterFactory]::CreateJsonReader(
        $Bytes, $Offset, $Bytes.Length - $Offset, [Xml.XmlDictionaryReaderQuotas]::Max)
    $objects = [Collections.Generic.Dictionary[int,object]]::new()
    try {
        while ($reader.Read()) {
            if ($reader.NodeType -eq [Xml.XmlNodeType]::EndElement) {
                $null = $objects.Remove($reader.Depth)
            } elseif ($reader.NodeType -eq [Xml.XmlNodeType]::Element) {
                $parent = $reader.Depth - 1
                if ($objects.ContainsKey($parent)) {
                    $key = if ($reader.NamespaceURI -ceq 'item') { $reader.GetAttribute('item') } else { $reader.LocalName }
                    if (!$objects[$parent].Add($key)) { throw 'Duplicate dependency metadata key.' }
                }
                if ($reader.GetAttribute('type') -ceq 'object') {
                    $objects.Add($reader.Depth, [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal))
                }
            }
        }
    } finally { $reader.Dispose() }
}

function Read-DependencyJsonText([string]$Path, [string]$ExpectedSha256) {
    Assert-PackagingPath $Path -File -Required
    $bytes = [IO.File]::ReadAllBytes($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $sha256 = [BitConverter]::ToString($algorithm.ComputeHash($bytes)).Replace('-','') }
    finally { $algorithm.Dispose() }
    if ($sha256 -cne $ExpectedSha256) { throw 'Dependency metadata differs from the inventoried file.' }

    $offset = 0
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { $offset = 3 }
    $utf8 = [Text.UTF8Encoding]::new($false,$true)
    try { $text = $utf8.GetString($bytes, $offset, $bytes.Length - $offset) }
    catch [Text.DecoderFallbackException] { throw 'Dependency metadata must be valid UTF-8.' }

    try {
        if ($PSVersionTable.PSEdition -eq 'Desktop') {
            Add-Type -AssemblyName System.Runtime.WindowsRuntime
            Add-Type -AssemblyName System.Runtime.Serialization
            $null = [Windows.Data.Json.JsonObject, Windows.Data.Json, ContentType=WindowsRuntime]::Parse($text)
            Assert-UniqueDependencyMembers $bytes $offset
        } else {
            $options = [System.Text.Json.JsonDocumentOptions]::new()
            if ($options.PSObject.Properties['AllowDuplicateProperties']) { $options.AllowDuplicateProperties = $false }
            else { Assert-UniqueDependencyMembers $bytes $offset }
            $document = [System.Text.Json.JsonDocument]::Parse($text, $options)
            $document.Dispose()
        }
    } catch {
        throw [IO.InvalidDataException]::new('Dependency metadata must contain strict, unambiguous JSON.', $_.Exception)
    }
    return $text
}

function Read-PackagingJson([string]$Path, [switch]$AsArray, [string]$ExpectedDependencySha256) {
    $text = if ($ExpectedDependencySha256) { Read-DependencyJsonText $Path $ExpectedDependencySha256 } else { Get-Content -LiteralPath $Path -Raw }
    $arguments = @{ InputObject = $text; ErrorAction = 'Stop' }
    # Windows PowerShell already preserves a root array. PowerShell 7
    # requires NoEnumerate to keep it distinct from its sole element.
    if ($PSVersionTable.PSVersion.Major -ge 7) { $arguments.NoEnumerate = $true }
    $root = ConvertFrom-Json @arguments
    if (($AsArray -and $root -isnot [array]) -or (!$AsArray -and $root -isnot [System.Management.Automation.PSCustomObject])) {
        throw "Unexpected JSON root in $([IO.Path]::GetFileName($Path))."
    }
    return ,$root
}

function Assert-PackagingPath([string]$Path, [switch]$File, [switch]$Required) {
    $root = [IO.Path]::GetPathRoot($Path)
    if (!$root -or $Path.StartsWith('\\?\') -or $Path.StartsWith('\\.\')) { throw "Expected an ordinary absolute path: $Path" }
    $parts = $Path.Substring($root.Length).Split([char[]]'\/', [StringSplitOptions]::RemoveEmptyEntries)
    foreach ($part in $parts) {
        if ($part.EndsWith(' ') -or $part.EndsWith('.') -or $part -match '[:*?"<>|]' -or
            $part -match '^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)') { throw "Ambiguous Windows path component: $Path" }
    }
    if ($File -and $parts.Length -eq 0) { throw "Expected a file path: $Path" }
    $current = $root
    for ($i = -1; $i -lt $parts.Length; $i++) {
        if ($i -ge 0) { $current = [IO.Path]::Combine($current, $parts[$i]) }
        try { $attributes = [IO.File]::GetAttributes($current) }
        catch [IO.FileNotFoundException], [IO.DirectoryNotFoundException] {
            if ($Required -or $i -eq -1) { throw }
            return
        }
        if ($attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Packaging path contains a reparse point: $current" }
        $expectDirectory = !$File -or $i -lt $parts.Length - 1
        if ([bool]($attributes -band [IO.FileAttributes]::Directory) -ne $expectDirectory) { throw "Unexpected file or directory at $current" }
    }
}

$yamusePackageOutputLockName = '.yamuse-package.lock'

function Open-PackagingOutput([string]$Path, [switch]$AllowExisting,
        [string]$NonemptyMessage = 'OutputFolder must be empty; existing releases are never overwritten.') {
    Assert-PackagingPath $Path -Required
    $lockPath = Join-Path $Path $yamusePackageOutputLockName
    $lease = [IO.FileStream]::new($lockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None, 4096, [IO.FileOptions]::DeleteOnClose)
    try {
        if (!$AllowExisting) {
            $otherEntries = @(Get-ChildItem -LiteralPath $Path -Force |
                Where-Object { $_.Name -cne $yamusePackageOutputLockName })
            if ($otherEntries.Count -ne 0) { throw $NonemptyMessage }
        }
        return $lease
    }
    catch { $lease.Dispose(); throw }
}

function New-PackagingWorkStage([string]$Path) {
    Assert-PackagingPath $Path
    $null = [IO.Directory]::CreateDirectory($Path)
    $lease = Open-PackagingOutput $Path -NonemptyMessage 'Use a new, empty WorkFolder for each build.'
    try {
        # Creating the stage claims this WorkFolder before any shared config is written.
        $stage = Join-Path $Path 'publish'
        $null = New-Item -ItemType Directory -Path $stage
        return $stage
    }
    finally { $lease.Dispose() }
}

function Get-PackagingTreeFiles([string]$Path) {
    Assert-PackagingPath $Path -Required
    $directories = [Collections.Generic.Stack[string]]::new()
    $directories.Push($Path)
    while ($directories.Count -gt 0) {
        $directory = $directories.Pop()
        Assert-PackagingPath $directory -Required
        foreach ($entry in Get-ChildItem -LiteralPath $directory -Force) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Publish contains a reparse point: $($entry.FullName)" }
            if ($entry.PSIsContainer) {
                $directories.Push($entry.FullName)
                continue
            }
            $entry
        }
    }
}

function Get-PublishedFileInventory([string]$Path) {
    $root = $Path.TrimEnd('\','/') + '\'
    $files = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach ($entry in Get-PackagingTreeFiles $Path) {
        $relative = $entry.FullName.Substring($root.Length).Replace('\','/')
        $files.Add($relative, [pscustomobject]@{ bytes=$entry.Length; sha256=(Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256).Hash })
    }
    return $files
}

function Get-PublishedInventorySha256($Files) {
    $paths = [string[]]@($Files.Keys)
    [Array]::Sort($paths, [StringComparer]::Ordinal)
    $manifest = [IO.MemoryStream]::new()
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        # Length-prefixed UTF-8 paths and hashes, little-endian byte lengths.
        # Relative paths preserve their exact casing and always use '/'.
        $writer = [IO.BinaryWriter]::new($manifest, [Text.UTF8Encoding]::new($false, $true), $true)
        try {
            $writer.Write('Yamuse publish inventory v1')
            $writer.Write($paths.Length)
            foreach ($relative in $paths) {
                $writer.Write($relative)
                $writer.Write([long]$Files[$relative].bytes)
                $writer.Write([string]$Files[$relative].sha256)
            }
        } finally { $writer.Dispose() }
        $manifest.Position = 0
        return [BitConverter]::ToString($algorithm.ComputeHash($manifest)).Replace('-','')
    } finally { $algorithm.Dispose(); $manifest.Dispose() }
}

function Assert-PublishedInventorySha256($Files, [string]$ExpectedSha256) {
    if ((Get-PublishedInventorySha256 $Files) -ne $ExpectedSha256) {
        throw 'Publish inventory differs from the approved file paths and SHA-256 hashes.'
    }
}

function Copy-PackagingInventory([string]$Source, [string]$Destination, $Files, [switch]$ReplaceExisting) {
    foreach ($relative in $Files.Keys) {
        $from = Join-Path $Source $relative
        $to = Join-Path $Destination $relative
        $expectedHash = $Files[$relative].sha256
        Assert-PackagingPath $from -File -Required
        Assert-PackagingPath $to -File
        $parent = [IO.Path]::GetDirectoryName($to)
        $null = [IO.Directory]::CreateDirectory($parent)
        if ($ReplaceExisting -and [IO.File]::Exists($to)) {
            if ((Get-FileHash -LiteralPath $to -Algorithm SHA256).Hash -ceq $expectedHash) {
                if ((Get-FileHash -LiteralPath $from -Algorithm SHA256).Hash -cne $expectedHash) {
                    throw "PublishedFolder changed during staging: $relative"
                }
                continue
            }
            $temporary = Join-Path $parent ('.' + [guid]::NewGuid().ToString('N') + '.tmp')
            [IO.File]::Copy($from, $temporary)
            try {
                if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -cne $expectedHash) {
                    throw "PublishedFolder changed during staging: $relative"
                }
                try { [IO.File]::Replace($temporary, $to, [NullString]::Value) }
                catch { throw [IO.IOException]::new("Failed to replace staged build output: $relative", $_.Exception) }
            }
            finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
        }
        else { [IO.File]::Copy($from,$to) }
        if ((Get-FileHash -LiteralPath $to -Algorithm SHA256).Hash -cne $expectedHash) {
            throw "PublishedFolder changed during staging: $relative"
        }
    }
}

function Assert-YamuseAppHost([string]$Path, [string]$ExpectedSha256) {
    Assert-PackagingPath $Path -File -Required
    $bytes = [IO.File]::ReadAllBytes($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $sha256 = [BitConverter]::ToString($algorithm.ComputeHash($bytes)).Replace('-','')
        if ($ExpectedSha256 -and $sha256 -ne $ExpectedSha256) {
            throw 'Yamuse apphost does not match the approved SHA-256.'
        }
        # The pinned win-x64 apphost has a stable native image before .rsrc.
        # The SDK binds YMusic.Native.dll there and appends the PE resources.
        $resourceStart = 160768
        if ($bytes.Length -lt $resourceStart -or [BitConverter]::ToUInt16($bytes,0) -ne 0x5A4D -or
            [BitConverter]::ToInt32($bytes,60) -ne 224 -or [BitConverter]::ToUInt32($bytes,224) -ne 0x4550 -or
            [BitConverter]::ToUInt16($bytes,228) -ne 0x8664) { throw 'Expected the pinned Windows x64 apphost.' }
        $resourceSize = [BitConverter]::ToUInt32($bytes,696)
        $resourceRaw = [BitConverter]::ToUInt32($bytes,704)
        if ($resourceSize -eq 0 -or $resourceRaw -lt $resourceSize -or [long]$resourceStart + $resourceRaw -gt $bytes.Length -or
            [BitConverter]::ToUInt32($bytes,380) -ne $resourceSize -or
            [BitConverter]::ToUInt32($bytes,256) -ne (64512 + [long]$resourceRaw) -or
            [BitConverter]::ToUInt32($bytes,304) -ne (180224 + 4096 * [Math]::Ceiling($resourceSize / 4096))) {
            throw 'Unexpected Yamuse apphost resource layout.'
        }
        # Normalize resource-dependent sizing fields and the PE checksum. The
        # remaining bytes, including the entry point and DLL binding, are pinned.
        foreach ($offset in 256,304,312,380,696,704) { [Array]::Clear($bytes,$offset,4) }
        $native = [BitConverter]::ToString($algorithm.ComputeHash($bytes,0,$resourceStart)).Replace('-','')
        if ($native -cne '25879604FB8223A77E1E087BBA2DD1DD8DB4A0F41FF2D51DCB1AF70603D32853') {
            throw 'Expected the pinned x64 .NET apphost bound to YMusic.Native.dll.'
        }
        return $sha256
    } finally { $algorithm.Dispose() }
}

function Read-PackagingVerification([string]$ReceiptPath, [string]$PublishedPath, [string]$Version) {
    Assert-PackagingPath $ReceiptPath -File -Required
    $receipt = Read-PackagingJson $ReceiptPath
    $full = $receipt.PSObject.Properties['full']
    if (!(Test-PackagingJsonString $receipt 'status' 'PASS') -or !$full -or $full.Value -isnot [bool] -or !$full.Value -or
        !(Test-PackagingJsonString $receipt 'productVersion' $Version)) {
        throw 'Packaging requires a completed full verification receipt for this version.'
    }
    $source = $receipt.PSObject.Properties['publishedFolder']
    if (!$source -or $source.Value -isnot [string] -or $source.Value -ine $PublishedPath) {
        throw 'PublishedFolder differs from the source recorded by verification.'
    }
    foreach ($name in 'productDllSha256','mainExecutableSha256','publishInventorySha256') {
        $field = $receipt.PSObject.Properties[$name]
        if (!$field -or $field.Value -isnot [string] -or $field.Value -notmatch '\A[A-Fa-f0-9]{64}\z') {
            throw "Verification receipt lacks a valid $name."
        }
    }
    return $receipt
}

function Get-PackagingJsonObject($Object, [string]$Name) {
    $property = $Object.PSObject.Properties[$Name]
    if (!$property -or $property.Name -cne $Name -or $property.Value -isnot [System.Management.Automation.PSCustomObject]) {
        throw "Expected a JSON object for $Name."
    }
    return $property.Value
}

function Test-PackagingJsonString($Object, [string]$Name, [string]$Expected) {
    if ($Object -isnot [System.Management.Automation.PSCustomObject]) { return $false }
    $property = $Object.PSObject.Properties[$Name]
    return $null -ne $property -and $property.Name -ceq $Name -and $property.Value -is [string] -and $property.Value -ceq $Expected
}

function Assert-DependencyAssetMetadata($Group, [string]$Name) {
    $descriptor = Get-PackagingJsonObject $Group $Name
    foreach ($property in $descriptor.PSObject.Properties) {
        # The checked SDK emits no localPath overrides. Hostpolicy prefers an
        # override to the path in the asset key, which the inventory verifies.
        $version = $null
        if ($property.Name -cnotin 'assemblyVersion','fileVersion' -or
            $property.Value -isnot [string] -or $property.Value -cnotmatch '^[0-9]+(?:\.[0-9]+){3}$' -or
            ![version]::TryParse($property.Value, [ref]$version)) {
            throw "Unexpected dependency asset metadata for ${Name}: $($property.Name)"
        }
    }
}

function Assert-DependencyLibraryMetadata($Library, [string]$Name, [string]$Type) {
    $isPackage = $Type -ceq 'package'
    $expectedCount = if ($isPackage) { 5 } else { 3 }
    $hash = if ($isPackage) { 'sha512-OpHNacIumnkhaaX3ZlQ/TCR4YPOJvud2/PAOoWb6o+mIrLwIVA275I5wAgt4q/1O2VrJeDwR1qFQyqzG4lUArg==' } else { '' }
    $serviceable = $Library.PSObject.Properties['serviceable']
    if (@($Library.PSObject.Properties.Name).Count -ne $expectedCount -or !$serviceable -or
        $serviceable.Name -cne 'serviceable' -or $serviceable.Value -isnot [bool] -or $serviceable.Value -ne $isPackage -or
        !(Test-PackagingJsonString $Library 'sha512' $hash) -or
        ($isPackage -and (!(Test-PackagingJsonString $Library 'path' 'velopack/1.2.161') -or
            !(Test-PackagingJsonString $Library 'hashPath' 'velopack.1.2.161.nupkg.sha512')))) {
        throw "Unexpected dependency library metadata for $Name."
    }
}

function Assert-PinnedPublishRuntime([string]$Path, [string]$Version, $Files, [switch]$FixtureRunner) {
    $runtime = '10.0.15'
    $entryPoint = if ($FixtureRunner) { 'YMusic.Native.Checks' } else { 'YMusic.Native' }
    # The SDK generates identical host configuration bytes for the product and
    # fixture runner. Checking them also binds host options and duplicate keys.
    $configName = "$entryPoint.runtimeconfig.json"
    $configPath = Join-Path $Path $configName
    $configHash = '90AD7933E57167DCA2B7EB7AAC4988C540FEC9D53FC1D31A0B2F451D0E866E9F'
    Assert-PackagingPath $configPath -File -Required
    if (!$Files.ContainsKey($configName) -or $Files[$configName].sha256 -cne $configHash -or
        (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash -cne $configHash) {
        throw "Expected the pinned .NET Core and Windows Desktop $runtime runtimeconfig bytes: $configName"
    }

    $depsName = "$entryPoint.deps.json"
    if (!$Files.ContainsKey($depsName)) { throw "Missing inventoried dependency metadata: $depsName" }
    $depsHash = $Files[$depsName].sha256
    $deps = Read-PackagingJson (Join-Path $Path $depsName) -ExpectedDependencySha256 $depsHash
    $targetName = '.NETCoreApp,Version=v10.0/win-x64'
    $runtimeTarget = Get-PackagingJsonObject $deps 'runtimeTarget'
    if (!(Test-PackagingJsonString $runtimeTarget 'name' $targetName)) { throw 'Expected win-x64 self-contained dependencies.' }
    $targets = Get-PackagingJsonObject (Get-PackagingJsonObject $deps 'targets') $targetName
    $libraries = Get-PackagingJsonObject $deps 'libraries'
    if ($deps.PSObject.Properties['runtimes']) {
        $runtimes = Get-PackagingJsonObject $deps 'runtimes'
        foreach ($rid in $runtimes.PSObject.Properties) {
            if ($rid.Value -isnot [array] -or @($rid.Value | Where-Object { $_ -isnot [string] }).Count) {
                throw "Invalid runtime fallback list for $($rid.Name)."
            }
        }
    }
    $product = "YMusic.Native/$Version"
    $dependencies = [ordered]@{
        'Velopack' = '1.2.161'
        'runtimepack.Microsoft.NETCore.App.Runtime.win-x64' = $runtime
        'runtimepack.Microsoft.WindowsDesktop.App.Runtime.win-x64' = $runtime
        'runtimepack.Microsoft.Windows.SDK.NET.Ref' = '10.0.19041.57'
    }
    $libraryCount = $dependencies.Count + 1 + [int]$FixtureRunner.IsPresent
    if ($targets.PSObject.Properties.Name.Count -ne $libraryCount -or
        $libraries.PSObject.Properties.Name.Count -ne $libraryCount) { throw 'Unexpected self-contained library set.' }
    $productTarget = Get-PackagingJsonObject $targets $product
    $productLibrary = Get-PackagingJsonObject $libraries $product
    $productRuntime = Get-PackagingJsonObject $productTarget 'runtime'
    $productDependencies = Get-PackagingJsonObject $productTarget 'dependencies'
    Assert-DependencyAssetMetadata $productRuntime 'YMusic.Native.dll'
    $productDependencyCount = if ($FixtureRunner) { 1 } else { $dependencies.Count }
    if (@($productTarget.PSObject.Properties.Name).Count -ne 2 -or @($productRuntime.PSObject.Properties.Name).Count -ne 1 -or
        @($productDependencies.PSObject.Properties.Name).Count -ne $productDependencyCount -or
        !(Test-PackagingJsonString $productLibrary 'type' 'project')) { throw 'Unexpected Yamuse dependencies.' }
    Assert-DependencyLibraryMetadata $productLibrary $product 'project'
    $frameworkDependencies = $productDependencies
    if ($FixtureRunner) {
        $fixtureKeys = @($targets.PSObject.Properties.Name | Where-Object { $_.StartsWith('YMusic.Native.Checks/', [StringComparison]::Ordinal) })
        if ($fixtureKeys.Count -ne 1 -or !$Files.ContainsKey('YMusic.Native.Checks.dll')) { throw 'Expected the fixture runner and its dependency entry.' }
        $fixtureKey = $fixtureKeys[0]
        $fixtureTarget = Get-PackagingJsonObject $targets $fixtureKey
        $fixtureLibrary = Get-PackagingJsonObject $libraries $fixtureKey
        $fixtureRuntime = Get-PackagingJsonObject $fixtureTarget 'runtime'
        $frameworkDependencies = Get-PackagingJsonObject $fixtureTarget 'dependencies'
        Assert-DependencyAssetMetadata $fixtureRuntime 'YMusic.Native.Checks.dll'
        if (@($fixtureTarget.PSObject.Properties.Name).Count -ne 2 -or @($fixtureRuntime.PSObject.Properties.Name).Count -ne 1 -or
            @($frameworkDependencies.PSObject.Properties.Name).Count -ne $dependencies.Count -or
            !(Test-PackagingJsonString $frameworkDependencies 'YMusic.Native' $Version) -or
            !(Test-PackagingJsonString $fixtureLibrary 'type' 'project')) { throw 'Unexpected fixture runner dependencies.' }
        Assert-DependencyLibraryMetadata $fixtureLibrary $fixtureKey 'project'
    }

    $runtimeFiles = [Collections.Generic.SortedDictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach ($name in $dependencies.Keys) {
        $pin = $dependencies[$name]
        $library = "$name/$pin"
        $type = if ($name -ceq 'Velopack') { 'package' } else { 'runtimepack' }
        $owner = if ($name -ceq 'Velopack') { $productDependencies } else { $frameworkDependencies }
        $metadata = Get-PackagingJsonObject $libraries $library
        if (!(Test-PackagingJsonString $owner $name $pin) -or
            !(Test-PackagingJsonString $metadata 'type' $type)) { throw "Expected pinned dependency: $library" }
        Assert-DependencyLibraryMetadata $metadata $library $type
        $assets = Get-PackagingJsonObject $targets $library
        if (@($assets.PSObject.Properties.Name | Where-Object { $_ -cnotin 'runtime','native' }).Count) { throw "Unexpected dependency assets: $library" }
        foreach ($kind in @('runtime','native')) {
            if (!$assets.PSObject.Properties[$kind]) { continue }
            $group = Get-PackagingJsonObject $assets $kind
            foreach ($asset in $group.PSObject.Properties.Name) {
                Assert-DependencyAssetMetadata $group $asset
                $relative = if ($asset -ceq 'lib/net10.0/Velopack.dll' -and $name -ceq 'Velopack') { 'Velopack.dll' } else { $asset }
                if (!$Files.ContainsKey($relative)) { throw "Missing pinned runtime file: $relative" }
                $runtimeFiles.Add($relative, "$library $kind $asset $($Files[$relative].sha256)")
            }
        }
    }
    foreach ($relative in $Files.Keys | Where-Object { $_ -clike '*.resources.dll' }) {
        $runtimeFiles.Add($relative, "satellite $relative $($Files[$relative].sha256)")
    }
    # From the SDK 10.0.404 publish layout, with every runtime, projection and
    # Velopack file checked against its signed nupkg. Hash path-sorted UTF-8
    # lines of the .deps asset layout and binary hashes, including WPF satellites.
    $data = [Text.Encoding]::UTF8.GetBytes((@($runtimeFiles.GetEnumerator() | ForEach-Object { "$($_.Key): $($_.Value)" }) -join "`n"))
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try { $hash = [BitConverter]::ToString($sha256.ComputeHash($data)).Replace('-','') }
    finally { $sha256.Dispose() }
    if ($hash -cne '3CB3ABA7B0E5E28E2C30C6BE2A3C6599397CFE8A05371019DA0DE60FF626E807') { throw 'Publish differs from the pinned .NET, Windows projection or Velopack runtime payload.' }

    $feed = Read-PackagingJson (Join-Path $Path 'update-feed.json')
    $repository = $feed.PSObject.Properties['githubRepository']
    if (@($feed.PSObject.Properties.Name).Count -ne 1 -or !$repository -or
        $repository.Value -isnot [string] -or $repository.Value -cne 'https://github.com/senduora/yamuse') {
        throw 'Publish must use the official Yamuse update source.'
    }
    return [pscustomobject]@{ version=$runtime; digest=$hash; fileCount=$runtimeFiles.Count; runtimeConfigSha256=$configHash; depsSha256=$depsHash }
}
