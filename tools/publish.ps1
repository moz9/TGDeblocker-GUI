param([string]$Configuration = 'Release', [string]$OutputDirectory = '', [string]$Version = '',
    [string]$VcRuntimeDirectory = '',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9-]{0,23}$')][string]$QaId)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$outputPath = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $projectRoot 'dist\TGDeblocker-win-x64' }
if (-not $Version) { $Version = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'WindowsVersion.props') -Raw)).Project.PropertyGroup.Version.'#text' }
if ($Version -notmatch '^(0|[1-9]\d{0,3})\.(0|[1-9]\d{0,3})\.(0|[1-9]\d{0,3})$') { throw 'Use a canonical major.minor.patch version.' }
$arguments = @('publish', (Join-Path $projectRoot 'src\TGDeblocker.App\TGDeblocker.App.csproj'), '-c', $Configuration, '-p:Platform=x64', '-r', 'win-x64', '--self-contained', 'true', '-o', $outputPath, "-p:Version=$Version", "-p:InstallerQaId=$QaId")
dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
if (-not $VcRuntimeDirectory) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $vsRoot = & $vswhere -latest -products '*' -version '[17.0,18.0)' -property installationPath
        if ($vsRoot) {
            $VcRuntimeDirectory = Get-ChildItem -LiteralPath (Join-Path $vsRoot 'VC\Redist\MSVC') -Directory | Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } |
                Sort-Object { [version]$_.Name } -Descending | ForEach-Object { Join-Path $_.FullName 'x64\Microsoft.VC143.CRT' } |
                Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        }
    }
}
if (-not $VcRuntimeDirectory) { throw 'Pass -VcRuntimeDirectory pointing to official x64 Microsoft.VC143.CRT redistributables.' }
foreach ($dll in @('msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')) {
    $runtimeFile = Join-Path $VcRuntimeDirectory $dll
    $signature = Get-AuthenticodeSignature -LiteralPath $runtimeFile
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft') { throw "Untrusted Microsoft runtime: $dll" }
    Copy-Item -LiteralPath $runtimeFile -Destination $outputPath -Force
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $outputPath
if (Test-Path -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $outputPath
    Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses') -Destination $outputPath -Recurse -Force
}
$metadata = [ordered]@{ schema = 1; appId = 'TGDeblocker.Windows' + $(if ($QaId) { ".QA.$QaId" }); version = $Version; qaId = $(if ($QaId) { $QaId } else { $null }) }
$metadata | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $outputPath 'product.json') -Encoding utf8
Write-Output (Join-Path $outputPath 'TGDeblocker.App.exe')
