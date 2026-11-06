param(
    [ValidateSet('Android', 'iOSCompile')][string]$Target = 'Android',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateSet('android-arm64', 'android-x64')][string]$Runtime = 'android-arm64',
    [string]$Dotnet = 'dotnet',
    [string]$AndroidSdk = $env:ANDROID_HOME,
    [string]$JavaSdk = $env:JAVA_HOME,
    [string]$TelegramApiCredentialsFile,
    [string]$TelegramApiCredentialsStore = $(if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'TGDeblocker-Publisher/telegram-api.dpapi' })
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $projectRoot 'src/TGDeblocker.Mobile'
if ($TelegramApiCredentialsFile) { $TelegramApiCredentialsFile = (Resolve-Path -LiteralPath $TelegramApiCredentialsFile).Path }
if ($TelegramApiCredentialsStore -and (Test-Path -LiteralPath $TelegramApiCredentialsStore)) {
    $TelegramApiCredentialsStore = (Resolve-Path -LiteralPath $TelegramApiCredentialsStore).Path
}
$temporaryApiDirectory = $null
Push-Location $project
try {
    if ($Target -eq 'iOSCompile') {
        # A managed-code check only. This does not link TDLib, sign or create an IPA.
        & $Dotnet build -t:Compile -f net10.0-ios -p:TGDeblockerTargetFrameworks=net10.0-ios -p:EnableCodeSigning=false -c Debug
    } else {
        $abi = if ($Runtime -eq 'android-arm64') { 'arm64-v8a' } else { 'x86_64' }
        if (!(Test-Path -LiteralPath "Native/Android/$abi/libtdjson.so")) {
            throw "Missing TDLib for $abi. Follow tools/README-tdlib-mobile.md first."
        }
        $arguments = @('publish', '-f', 'net10.0-android', '-r', $Runtime, "-p:RuntimeIdentifiers=$Runtime", '-c', $Configuration)
        if ($Configuration -eq 'Release' -or $TelegramApiCredentialsFile) {
            $apiBytes = $null
            try {
                if ($TelegramApiCredentialsFile) {
                    $apiBytes = [IO.File]::ReadAllBytes($TelegramApiCredentialsFile)
                } elseif ($TelegramApiCredentialsStore -and (Test-Path -LiteralPath $TelegramApiCredentialsStore)) {
                    $apiBytes = [Security.Cryptography.ProtectedData]::Unprotect(
                        [IO.File]::ReadAllBytes($TelegramApiCredentialsStore), $null,
                        [Security.Cryptography.DataProtectionScope]::CurrentUser)
                } else {
                    throw 'No publisher configuration.'
                }
                $api = [Text.Encoding]::UTF8.GetString($apiBytes) | ConvertFrom-Json
                $apiIdentifier = 0
                if (![int]::TryParse([string]$api.ApiId, [ref]$apiIdentifier) -or $apiIdentifier -le 0 -or
                    [string]$api.ApiHash -notmatch '^[a-fA-F0-9]{32}$') {
                    throw 'Invalid publisher configuration.'
                }
                $temporaryApiDirectory = Join-Path ([IO.Path]::GetTempPath()) ('TGDeblocker-build-' + [guid]::NewGuid().ToString('N'))
                [IO.Directory]::CreateDirectory($temporaryApiDirectory) | Out-Null
                $apiResourcePath = Join-Path $temporaryApiDirectory 'telegram-api.json'
                $apiResource = @{ ApiId = $apiIdentifier; ApiHash = [string]$api.ApiHash } | ConvertTo-Json -Compress
                [IO.File]::WriteAllText($apiResourcePath, $apiResource, [Text.UTF8Encoding]::new($false))
                $arguments += "-p:TelegramApiCredentialsFile=$apiResourcePath"
            } catch {
                throw 'Provide a valid publisher Telegram API configuration; see the mobile README. Credential values are not logged.'
            } finally {
                if ($apiBytes) { [Array]::Clear($apiBytes, 0, $apiBytes.Length) }
                $api = $apiResource = $null
            }
        }
        if ($AndroidSdk) { $arguments += "-p:AndroidSdkDirectory=$AndroidSdk" }
        if ($JavaSdk) { $arguments += "-p:JavaSdkDirectory=$JavaSdk" }
        & $Dotnet @arguments
    }
    if ($LASTEXITCODE -ne 0) { throw "Mobile $Target failed (exit $LASTEXITCODE)." }
} finally {
    Pop-Location
    if ($temporaryApiDirectory) {
        $apiResourcePath = Join-Path $temporaryApiDirectory 'telegram-api.json'
        if (Test-Path -LiteralPath $apiResourcePath) { Remove-Item -LiteralPath $apiResourcePath }
        if (Test-Path -LiteralPath $temporaryApiDirectory) { Remove-Item -LiteralPath $temporaryApiDirectory }
    }
}
