[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Installer,
    [Parameter(Mandatory)][ValidatePattern('^(0|[1-9]\d{0,3})\.(0|[1-9]\d{0,3})\.(0|[1-9]\d{0,3})$')][string]$Version,
    [string]$OutputFile,
    [string]$KeyFile = (Join-Path $env:LOCALAPPDATA 'TGDeblocker-Publisher\windows-update-key.dpapi')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$file = Get-Item -LiteralPath $Installer
$expectedName = "TGDeblocker-Setup-$Version-x64.exe"
if ($file.Name -cne $expectedName) { throw "Installer must be named $expectedName" }
$binaryVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($file.FullName)
if ("$($binaryVersion.FileMajorPart).$($binaryVersion.FileMinorPart).$($binaryVersion.FileBuildPart)" -ne $Version) { throw 'Installer version mismatch.' }
if ($file.Length -le 0 -or $file.Length -gt 512MB) { throw 'Invalid installer size.' }
if (-not $OutputFile) { $OutputFile = Join-Path $file.DirectoryName 'windows-update.json' }
if (-not (Test-Path -LiteralPath $KeyFile -PathType Leaf)) { throw 'Publisher signing key is missing; keys are never generated during release builds.' }
$publicKey = [IO.File]::ReadAllText((Join-Path $root 'release\windows-update-public-key.txt')).Trim()
$signer = [Security.Cryptography.ECDsa]::Create()
$privateBytes = $null
try {
    $privateBytes = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($KeyFile), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $consumed = 0
    $signer.ImportPkcs8PrivateKey($privateBytes, [ref]$consumed)
    if ([Convert]::ToBase64String($signer.ExportSubjectPublicKeyInfo()) -cne $publicKey) { throw 'Private key does not match the app trust key.' }
    $manifest = [ordered]@{
        schema = 1; product = 'TGDeblocker.Windows'; architecture = 'x64'; channel = 'stable'; version = $Version
        fileName = $expectedName; url = "https://github.com/moz9/TGDeblocker-GUI/releases/download/v$Version/$expectedName"
        size = $file.Length; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $payload = [Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Compress))
    $signature = $signer.SignData($payload, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
    $envelope = [ordered]@{ payload = [Convert]::ToBase64String($payload); signature = [Convert]::ToBase64String($signature) }
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputFile), ($envelope | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
    Get-Item -LiteralPath $OutputFile
} finally {
    if ($privateBytes) { [Array]::Clear($privateBytes, 0, $privateBytes.Length) }
    $signer.Dispose()
}
