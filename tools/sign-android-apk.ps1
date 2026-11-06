[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Apk,
    [Parameter(Mandatory)][string]$OutputFile,
    [Parameter(Mandatory)][string]$AndroidSdk,
    [Parameter(Mandatory)][string]$JavaSdk,
    [string]$KeyStore = (Join-Path $env:LOCALAPPDATA 'TGDeblocker-Publisher\android-release.keystore'),
    [string]$PasswordFile = (Join-Path $env:LOCALAPPDATA 'TGDeblocker-Publisher\android-release-password.dpapi')
)
$ErrorActionPreference = 'Stop'
$inputPath = (Resolve-Path -LiteralPath $Apk).Path
$outputPath = [IO.Path]::GetFullPath($OutputFile)
if ($inputPath -eq $outputPath) { throw 'Use a separate output APK.' }
if (!(Test-Path -LiteralPath $KeyStore) -or !(Test-Path -LiteralPath $PasswordFile)) {
    throw 'Publisher key is missing. Release signing never generates or replaces keys.'
}
$buildTools = Get-ChildItem -LiteralPath (Join-Path $AndroidSdk 'build-tools') -Directory |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$signer = Join-Path $buildTools.FullName 'lib\apksigner.jar'
$java = Join-Path $JavaSdk 'bin\java.exe'
$bytes = $null
$previousPassword = $env:TGDEBLOCKER_ANDROID_SIGNING_PASSWORD
try {
    $bytes = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($PasswordFile), $null,
        [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $env:TGDEBLOCKER_ANDROID_SIGNING_PASSWORD = [Text.Encoding]::UTF8.GetString($bytes)
    & $java -jar $signer sign --ks $KeyStore --ks-key-alias tgdeblocker --ks-pass env:TGDEBLOCKER_ANDROID_SIGNING_PASSWORD --key-pass env:TGDEBLOCKER_ANDROID_SIGNING_PASSWORD --v4-signing-enabled false --out $outputPath $inputPath
    if ($LASTEXITCODE -ne 0) { throw 'APK signing failed.' }
    & $java -jar $signer verify --verbose --print-certs $outputPath
    if ($LASTEXITCODE -ne 0) { throw 'APK signature verification failed.' }
    Get-FileHash -LiteralPath $outputPath -Algorithm SHA256
} finally {
    $env:TGDEBLOCKER_ANDROID_SIGNING_PASSWORD = $previousPassword
    if ($bytes) { [Array]::Clear($bytes, 0, $bytes.Length) }
}
