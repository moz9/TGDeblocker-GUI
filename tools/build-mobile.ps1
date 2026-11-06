param(
    [ValidateSet('Android', 'iOSCompile')][string]$Target = 'Android',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateSet('android-arm64', 'android-x64')][string]$Runtime = 'android-arm64',
    [string]$Dotnet = 'dotnet',
    [string]$AndroidSdk = $env:ANDROID_HOME,
    [string]$JavaSdk = $env:JAVA_HOME
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $projectRoot 'src/TGDeblocker.Mobile'
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
        if ($AndroidSdk) { $arguments += "-p:AndroidSdkDirectory=$AndroidSdk" }
        if ($JavaSdk) { $arguments += "-p:JavaSdkDirectory=$JavaSdk" }
        & $Dotnet @arguments
    }
    if ($LASTEXITCODE -ne 0) { throw "Mobile $Target failed (exit $LASTEXITCODE)." }
} finally { Pop-Location }
