[CmdletBinding()]
param(
    [string] $AssetsPath = (Join-Path $PSScriptRoot '../src/TGDeblocker.Mobile/obj/project.assets.json'),
    [string] $DotnetRoot = $env:DOTNET_ROOT,
    [int] $AndroidApi = 36,
    [ValidateSet('android-arm64', 'android-x64', 'android-arm', 'android-x86')]
    [string[]] $AndroidRuntimeIdentifiers,
    [string] $NativeNoticesDirectory = (Join-Path $PSScriptRoot '../artifacts/mobile-native/android'),
    [string] $NoticesPath = (Join-Path $PSScriptRoot '../src/TGDeblocker.Mobile/Resources/Raw/third-party-notices.txt'),
    [string] $InventoryPath = (Join-Path $PSScriptRoot '../release/mobile-dependencies.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $DotnetRoot) {
    $DotnetRoot = Split-Path -Parent (Get-Command dotnet -CommandType Application).Source
}

$assets = Get-Content -LiteralPath $AssetsPath -Raw | ConvertFrom-Json -AsHashtable
$packageFolders = @($assets.packageFolders.Keys)
$components = [System.Collections.Generic.List[object]]::new()
$texts = [ordered]@{}
$gaps = [System.Collections.Generic.List[string]]::new()
$noticeName = '^(licen[cs]e|notice|copying|third[-_. ]?party[-_. ]?notices?)(\.(txt|md|html))?$'

function Get-NoticeReference($Directory, $RelativePath) {
    if ([IO.Path]::IsPathRooted($RelativePath) -or $RelativePath -match '(^|[/\\])\.\.($|[/\\])') { return }
    $path = Join-Path $Directory $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return }
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $texts.Contains($hash)) {
        $texts[$hash] = Get-Content -LiteralPath $path -Raw
    }
    [ordered]@{ file = ($RelativePath -replace '\\','/'); sha256 = $hash }
}

function Add-Package($Name, $Version, $Kind, $Directory, $Files) {
    $licenseType = $null
    $license = $null
    $licenseUrl = $null
    $references = @()
    if ($Directory) {
        $nuspec = Get-ChildItem -LiteralPath $Directory -File -Filter '*.nuspec' | Select-Object -First 1
        if ($nuspec) {
            [xml] $document = Get-Content -LiteralPath $nuspec.FullName -Raw
            $metadata = $document.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
            $licenseNode = $metadata.SelectSingleNode("*[local-name()='license']")
            if ($licenseNode) {
                $licenseType = $licenseNode.GetAttribute('type')
                $license = $licenseNode.InnerText.Trim()
            }
            $urlNode = $metadata.SelectSingleNode("*[local-name()='licenseUrl']")
            if ($urlNode -and $urlNode.InnerText -match '^https?://') { $licenseUrl = $urlNode.InnerText.Trim() }
        } else {
            $gaps.Add("Missing nuspec for $Name $Version.")
        }

        $noticeFiles = @($Files | Where-Object { (Split-Path $_ -Leaf) -match $noticeName })
        if ($licenseType -eq 'file' -and $license -and -not [IO.Path]::IsPathRooted($license)) {
            $noticeFiles += $license
        }
        $references = @($noticeFiles | Sort-Object -Unique | ForEach-Object { Get-NoticeReference $Directory $_ })
    } else {
        $gaps.Add("Local package files unavailable for $Name $Version.")
    }

    if ($licenseType -eq 'file' -and [IO.Path]::IsPathRooted($license)) { $license = $null }
    $components.Add([ordered]@{
        name = $Name; version = $Version; kind = $Kind; licenseType = $licenseType
        license = $license; licenseUrl = $licenseUrl; texts = $references
    })
}

foreach ($libraryName in ($assets.libraries.Keys | Sort-Object)) {
    $library = $assets.libraries[$libraryName]
    if ($library.type -ne 'package') { continue }
    $name, $version = $libraryName -split '/', 2
    $directory = $packageFolders | ForEach-Object { Join-Path $_ $library.path } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
    Add-Package $name $version 'nuget-restore' $directory $library.files
}

$targetNames = @($assets.targets.Keys | Sort-Object)
$androidRids = @($targetNames | ForEach-Object {
    if ($_ -match '/(android-(?:arm64|x64|arm|x86))$') { $Matches[1] }
} | Sort-Object -Unique)
if ($AndroidRuntimeIdentifiers) { $androidRids = @($AndroidRuntimeIdentifiers | Sort-Object -Unique) }
$runtimeBase = @($targetNames | ForEach-Object { if ($_ -match '^net(\d+\.\d+)-android') { $Matches[1] } } |
    Sort-Object -Unique | Select-Object -First 1)

foreach ($rid in $androidRids) {
    foreach ($pack in @("Microsoft.Android.Runtime.Mono.$AndroidApi.$rid", "Microsoft.NETCore.App.Runtime.Mono.$rid")) {
        $packRoot = Join-Path $DotnetRoot "packs/$pack"
        $packVersion = if (Test-Path -LiteralPath $packRoot -PathType Container) {
            Get-ChildItem -LiteralPath $packRoot -Directory | Where-Object {
                $pack -notlike 'Microsoft.NETCore.App.*' -or $_.Name.StartsWith("$runtimeBase.")
            } | Sort-Object { [version]($_.Name -split '-')[0] } -Descending | Select-Object -First 1
        }
        if ($packVersion) {
            $files = Get-ChildItem -LiteralPath $packVersion.FullName -File | Select-Object -ExpandProperty Name
            Add-Package $pack $packVersion.Name 'installed-runtime-pack' $packVersion.FullName $files
        } else {
            $gaps.Add("No matching local SDK runtime pack for $pack.")
        }
    }
}
if (-not $androidRids.Count) { $gaps.Add('Restore assets do not identify a concrete Android runtime identifier.') }

$sdkDirectory = Join-Path $DotnetRoot 'sdk'
if (Test-Path -LiteralPath $sdkDirectory -PathType Container) {
    $sdk = Get-ChildItem -LiteralPath $sdkDirectory -Directory |
        Sort-Object { [version]($_.Name -split '-')[0] } -Descending | Select-Object -First 1
    if ($sdk) {
        $components.Add([ordered]@{
            name = '.NET SDK'; version = $sdk.Name; kind = 'installed-build-sdk'
            texts = @(Get-NoticeReference $DotnetRoot 'LICENSE.txt'; Get-NoticeReference $DotnetRoot 'ThirdPartyNotices.txt')
        })
    }
}

foreach ($native in @(
    @('TDLib', '1.8.67', 'BSL-1.0', 'TDLib-LICENSE.txt', 'https://github.com/tdlib/td/tree/42e6a5259551178d1dab54a22ad96d14bd906e20'),
    @('OpenSSL', '3.6.3', 'Apache-2.0', 'OpenSSL-LICENSE.txt', 'https://github.com/openssl/openssl/releases/tag/openssl-3.6.3')
)) {
    $references = @(Get-NoticeReference $NativeNoticesDirectory $native[3])
    if (-not $references.Count) { $gaps.Add("Build output is missing the upstream $($native[0]) license text.") }
    $components.Add([ordered]@{
        name = $native[0]; version = $native[1]; kind = 'native-source-build'; licenseType = 'expression'
        license = $native[2]; source = $native[4]; texts = $references
    })
}

$gaps.Add('The NuGet restore graph includes build-time components. It is not an audit of final binary inclusion.')
$gaps.Add('Nested AAR/JAR archives are not unpacked. Some components have package license metadata only.')
$gaps.Add('Installed Android Mono pack versions are selected for the specified or restored RID and target .NET version.')
$inventory = [ordered]@{
    restoreTargets = $targetNames; androidRuntimeIdentifiers = $androidRids
    components = @($components.ToArray()); gaps = @($gaps.ToArray())
}

$notices = [System.Collections.Generic.List[string]]::new()
$notices.Add('TGDeblocker mobile third-party notices')
$notices.Add("Restore targets: $($targetNames -join ', ')")
$notices.Add("Android runtime identifiers: $($androidRids -join ', ')")
$notices.Add('')
foreach ($entry in $components) {
    $metadata = @($entry['license'], $entry['licenseUrl'], $entry['source'] | Where-Object { $_ })
    $notices.Add("$($entry.name) $($entry.version) [$($entry.kind)]")
    foreach ($value in $metadata) { $notices.Add("  $value") }
    foreach ($reference in $entry.texts) { $notices.Add("  $($reference.file) [SHA-256 $($reference.sha256)]") }
}
$notices.Add('')
$notices.Add('Scope and gaps')
foreach ($gap in $gaps) { $notices.Add("  $gap") }
foreach ($hash in $texts.Keys) {
    $notices.Add('')
    $notices.Add("================ SHA-256 $hash ================")
    $notices.Add($texts[$hash].TrimEnd())
}

foreach ($destination in @($NoticesPath, $InventoryPath)) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent ([IO.Path]::GetFullPath($destination))) | Out-Null
}
Set-Content -LiteralPath $NoticesPath -Value $notices -Encoding utf8
$inventory | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $InventoryPath -Encoding utf8
Write-Output "Recorded $($components.Count) components and $($texts.Count) distinct local texts."
