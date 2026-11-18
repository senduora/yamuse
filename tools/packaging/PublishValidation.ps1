function Get-PublishedFileInventory([string]$Path) {
    $root = (Get-Item -LiteralPath $Path).FullName.TrimEnd('\') + '\'
    $files = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    $entries = @(Get-Item -LiteralPath $Path) + @(Get-ChildItem -LiteralPath $Path -Recurse -Force)
    foreach ($entry in $entries) {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Publish contains a reparse point: $($entry.FullName)" }
        if ($entry.PSIsContainer) { continue }
        $relative = $entry.FullName.Substring($root.Length).Replace('\','/')
        $files.Add($relative, [pscustomobject]@{ bytes=$entry.Length; sha256=(Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256).Hash })
    }
    return $files
}

function Assert-PinnedPublishRuntime([string]$Path, [string]$Version, $Files, [switch]$FixtureRunner) {
    $runtime = '10.0.15'
    $entryPoint = if ($FixtureRunner) { 'YMusic.Native.Checks' } else { 'YMusic.Native' }
    $options = (Get-Content -LiteralPath (Join-Path $Path "$entryPoint.runtimeconfig.json") -Raw | ConvertFrom-Json).runtimeOptions
    if ($options.tfm -cne 'net10.0' -or $options.PSObject.Properties['framework'] -or $options.PSObject.Properties['frameworks']) { throw 'Expected self-contained .NET 10 runtime options.' }
    $frameworks = @($options.includedFrameworks)
    if ($frameworks.Count -ne 2 -or 'Microsoft.NETCore.App' -cnotin $frameworks.name -or
        'Microsoft.WindowsDesktop.App' -cnotin $frameworks.name -or @($frameworks | Where-Object version -CNE $runtime).Count) {
        throw "Expected the pinned .NET Core and Windows Desktop $runtime frameworks."
    }

    $deps = Get-Content -LiteralPath (Join-Path $Path "$entryPoint.deps.json") -Raw | ConvertFrom-Json
    $targetName = '.NETCoreApp,Version=v10.0/win-x64'
    if ($deps.runtimeTarget.name -cne $targetName) { throw 'Expected win-x64 self-contained dependencies.' }
    $targets = $deps.targets.PSObject.Properties[$targetName].Value
    $product = "YMusic.Native/$Version"
    $dependencies = [ordered]@{
        'Velopack' = '1.2.161'
        'runtimepack.Microsoft.NETCore.App.Runtime.win-x64' = $runtime
        'runtimepack.Microsoft.WindowsDesktop.App.Runtime.win-x64' = $runtime
        'runtimepack.Microsoft.Windows.SDK.NET.Ref' = '10.0.19041.57'
    }
    $libraryCount = $dependencies.Count + 1 + [int]$FixtureRunner.IsPresent
    if ($targets.PSObject.Properties.Name.Count -ne $libraryCount -or
        $deps.libraries.PSObject.Properties.Name.Count -ne $libraryCount -or
        !$deps.libraries.PSObject.Properties[$product]) { throw 'Unexpected self-contained library set.' }
    $productTarget = $targets.PSObject.Properties[$product].Value
    $productDependencyCount = if ($FixtureRunner) { 1 } else { $dependencies.Count }
    if (@($productTarget.runtime.PSObject.Properties.Name).Count -ne 1 -or !$productTarget.runtime.PSObject.Properties['YMusic.Native.dll'] -or
        @($productTarget.dependencies.PSObject.Properties.Name).Count -ne $productDependencyCount -or
        $deps.libraries.PSObject.Properties[$product].Value.type -cne 'project') { throw 'Unexpected Yamuse dependencies.' }
    $frameworkTarget = $productTarget
    if ($FixtureRunner) {
        $fixtureKeys = @($targets.PSObject.Properties.Name | Where-Object { $_.StartsWith('YMusic.Native.Checks/', [StringComparison]::Ordinal) })
        if ($fixtureKeys.Count -ne 1 -or !$Files.ContainsKey('YMusic.Native.Checks.dll')) { throw 'Expected the fixture runner and its dependency entry.' }
        $fixtureKey = $fixtureKeys[0]
        $frameworkTarget = $targets.PSObject.Properties[$fixtureKey].Value
        if (@($frameworkTarget.runtime.PSObject.Properties.Name).Count -ne 1 -or !$frameworkTarget.runtime.PSObject.Properties['YMusic.Native.Checks.dll'] -or
            @($frameworkTarget.dependencies.PSObject.Properties.Name).Count -ne $dependencies.Count -or
            $frameworkTarget.dependencies.PSObject.Properties['YMusic.Native'].Value -cne $Version -or
            $deps.libraries.PSObject.Properties[$fixtureKey].Value.type -cne 'project') { throw 'Unexpected fixture runner dependencies.' }
    }

    $runtimeFiles = [Collections.Generic.SortedDictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach ($name in $dependencies.Keys) {
        $pin = $dependencies[$name]
        $library = "$name/$pin"
        $type = if ($name -ceq 'Velopack') { 'package' } else { 'runtimepack' }
        $owner = if ($name -ceq 'Velopack') { $productTarget } else { $frameworkTarget }
        if ($owner.dependencies.PSObject.Properties[$name].Value -cne $pin -or
            $deps.libraries.PSObject.Properties[$library].Value.type -cne $type) { throw "Expected pinned dependency: $library" }
        $assets = $targets.PSObject.Properties[$library].Value
        if (@($assets.PSObject.Properties.Name | Where-Object { $_ -cnotin 'runtime','native' }).Count) { throw "Unexpected dependency assets: $library" }
        foreach ($kind in @('runtime','native')) {
            if (!$assets.PSObject.Properties[$kind]) { continue }
            foreach ($asset in $assets.PSObject.Properties[$kind].Value.PSObject.Properties.Name) {
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

    $feed = Get-Content -LiteralPath (Join-Path $Path 'update-feed.json') -Raw | ConvertFrom-Json
    if (@($feed.PSObject.Properties.Name).Count -ne 1 -or $feed.githubRepository -cne 'https://github.com/senduora/yamuse') { throw 'Publish must use the official Yamuse update source.' }
    return [pscustomobject]@{ version=$runtime; digest=$hash; fileCount=$runtimeFiles.Count }
}
