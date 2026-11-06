[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PayloadDirectory,
    [Parameter(Mandatory)] [ValidatePattern('^\d{1,5}\.\d{1,5}\.\d{1,5}$')] [string] $Version,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9-]{0,23}$')] [string] $QaId,
    [string] $OutputDirectory,
    [string] $CompilerPath
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$parts = $Version.Split('.')
if ($parts.Where({ [int]$_ -gt 65535 -or [string][int]$_ -cne $_ }).Count -ne 0) {
    throw 'Version components must be canonical integers between 0 and 65535.'
}

$payload = (Resolve-Path -LiteralPath $PayloadDirectory).ProviderPath
$required = @('TGDeblocker.App.exe', 'TGDeblocker.App.dll', 'TGDeblocker.App.pri', 'App.xbf', 'MainWindow.xbf', 'Microsoft.UI.Xaml.dll', 'hostfxr.dll', 'tdjson.dll', 'THIRD-PARTY-NOTICES.md', 'product.json')
foreach ($file in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $file) -PathType Leaf)) {
        throw "Missing Windows publish file $file in $payload. Use tools/publish.ps1 first."
    }
}
$fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $payload 'TGDeblocker.App.dll'))
$publishedVersion = "{0}.{1}.{2}" -f $fileVersion.FileMajorPart, $fileVersion.FileMinorPart, $fileVersion.FileBuildPart
if ($publishedVersion -cne $Version) {
    throw "The published app version $publishedVersion does not match installer version $Version. Publish the matching build first."
}
$product = Get-Content -LiteralPath (Join-Path $payload 'product.json') -Raw | ConvertFrom-Json
$expectedQa = if ($QaId) { $QaId } else { $null }
$expectedAppId = if ($QaId) { "TGDeblocker.Windows.QA.$QaId" } else { 'TGDeblocker.Windows' }
if ($product.schema -ne 1 -or $product.version -cne $Version -or $product.appId -cne $expectedAppId -or
    -not $product.PSObject.Properties['qaId'] -or $product.qaId -cne $expectedQa) {
    throw "The published app metadata does not match $expectedAppId $Version. Publish again with the corresponding version and QA id."
}

if (-not $CompilerPath) {
    $installedCompiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
        $installedCompiler.Source
    )
    $CompilerPath = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
    if (-not $CompilerPath) { throw 'Set -CompilerPath to ISCC.exe from Inno Setup 6.7.4.' }
}
$compiler = (Resolve-Path -LiteralPath $CompilerPath).ProviderPath
$outDir = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $projectRoot 'dist\Installer' }
$payloadPrefix = $payload.TrimEnd('\') + '\'
if (($outDir.TrimEnd('\') + '\').StartsWith($payloadPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Use an installer output folder outside the application payload.'
}

$leaf = 'TGDeblocker'
$setupPath = Join-Path $outDir "$leaf-Setup-$Version-x64.exe"
$arguments = @('/Q', "/DPayloadDirectory=$payload", "/DProductVersion=$Version", "/O$outDir")
if ($QaId) { $arguments += "/DQaId=$QaId" }
& $compiler @arguments (Join-Path $projectRoot 'installer\TGDeblocker.iss')
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
    throw "Inno Setup did not create $setupPath (exit code $LASTEXITCODE)."
}
Get-Item -LiteralPath $setupPath
