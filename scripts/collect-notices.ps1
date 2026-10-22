# Gathers the license texts and third-party notices of everything a publish ships: every NuGet package in the
# restore graph and the runtime packs (.NET, ASP.NET Core, Windows Desktop) a self-contained build copies in.
# Packages are found from obj/project.assets.json, never from a list kept in this script, so a dependency or version
# change needs no edit here. A package's own LICENSE / NOTICE / THIRD-PARTY-NOTICES files are copied as they are;
# a package that carries none gets the standard text of the license its nuspec declares, with its copyright line.
param(
    [Parameter(Mandatory)] [string]$Assets,
    [Parameter(Mandatory)] [string]$Out,
    # A self-contained publish folder: its runtimeconfig names the runtimes it bundles, the authoritative list.
    [string]$Published = '',
    [string]$Rid = 'win-x64',
    [string]$Templates = (Join-Path $PSScriptRoot '..\licenses\templates')
)
$ErrorActionPreference = 'Stop'
$json = Get-Content -Raw $Assets | ConvertFrom-Json
$root = $json.project.restore.packagesPath
$packages = [ordered]@{}
foreach ($target in $json.targets.PSObject.Properties) {
    foreach ($entry in $target.Value.PSObject.Properties) {
        if ($entry.Value.type -eq 'package') { $packages[$entry.Name.ToLowerInvariant()] = $entry.Name }
    }
}
foreach ($framework in $json.project.frameworks.PSObject.Properties) {
    foreach ($dependency in @($framework.Value.downloadDependencies)) {
        if (-not $dependency) { continue }
        $version = ($dependency.version -replace '[\[\]\(\) ]', '' -split ',')[0]
        $packages["$($dependency.name)/$version".ToLowerInvariant()] = "$($dependency.name)/$version"
    }
}
if ($Published) {
    foreach ($config in Get-ChildItem $Published -Filter *.runtimeconfig.json) {
        $runtime = (Get-Content -Raw $config.FullName | ConvertFrom-Json).runtimeOptions
        foreach ($framework in @($runtime.includedFrameworks)) {
            if (-not $framework) { continue }
            $pack = "$($framework.name).Runtime.$Rid"
            $packages["$pack/$($framework.version)".ToLowerInvariant()] = "$pack/$($framework.version)"
        }
    }
}

New-Item -ItemType Directory -Force $Out | Out-Null
$index = [System.Collections.Generic.List[string]]::new()
$index.Add('Third-party components shipped in this CODEXish build, with where each notice below comes from.')
$index.Add('')
foreach ($key in $packages.Keys) {
    $name, $version = $packages[$key] -split '/', 2
    $dir = Join-Path $root "$($name.ToLowerInvariant())\$($version.ToLowerInvariant())"
    if (-not (Test-Path $dir)) { Write-Host "::warning::$name $version is not in $root; its notice is missing."; $index.Add("$name $version - not found in the package cache"); continue }
    $nuspec = Get-ChildItem $dir -Filter *.nuspec | Select-Object -First 1
    $expression = ''; $copyright = ''; $authors = ''
    if ($nuspec) {
        [xml]$xml = Get-Content -Raw $nuspec.FullName
        $metadata = $xml.package.metadata
        if ($metadata.license -and $metadata.license.type -eq 'expression') { $expression = $metadata.license.'#text' }
        $copyright = [string]$metadata.copyright
        $authors = [string]$metadata.authors
    }
    $target = Join-Path $Out $name
    $own = Get-ChildItem $dir -File | Where-Object { $_.Name -match '(?i)^(license|licence|notice|third[-_ ]?party[-_ ]?notices)' }
    if ($own) {
        New-Item -ItemType Directory -Force $target | Out-Null
        $own | Copy-Item -Destination $target
        $index.Add("$name $version - $(if ($expression) { $expression } else { 'see files' }) - the package's own $(($own.Name) -join ', ')")
        continue
    }
    $template = switch ($expression) { 'MIT' { 'MIT.txt' } 'Apache-2.0' { 'Apache-2.0.txt' } default { $null } }
    if ($template) {
        New-Item -ItemType Directory -Force $target | Out-Null
        $holder = if ($copyright) { $copyright } elseif ($authors) { "Copyright (c) $authors" } else { "Copyright (c) the $name authors" }
        $text = (Get-Content -Raw (Join-Path $Templates $template)).Replace('{copyright}', $holder)
        if ($template -eq 'Apache-2.0.txt') { $text = "$holder`r`n`r`n$text" }
        Set-Content -Path (Join-Path $target 'LICENSE.txt') -Value $text -NoNewline
        $index.Add("$name $version - $expression - standard text with the nuspec's copyright line ($holder)")
    } else {
        Write-Host "::warning::$name $version declares '$expression', which has no template; only the reference is recorded."
        $index.Add("$name $version - $(if ($expression) { "$expression, https://licenses.nuget.org/$expression" } else { 'no license metadata' })")
    }
}
$index.Add('')
$index.Add('SQLite itself (inside e_sqlite3) is in the public domain.')
Set-Content -Path (Join-Path $Out 'INDEX.txt') -Value ($index -join "`r`n")
"$($packages.Count) components; notices in $Out"
