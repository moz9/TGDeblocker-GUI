# Нативная TDLib для мобильной сборки

Оба скрипта собирают JSON ABI из исходников TDLib 1.8.67 (`42e6a5259551178d1dab54a22ad96d14bd906e20`) и OpenSSL 3.6.3. Архивы проверяются по SHA-256. Скрипты также копируют тексты лицензий TDLib и OpenSSL рядом с результатом. [TDLib](https://github.com/tdlib/td/tree/42e6a5259551178d1dab54a22ad96d14bd906e20) · [OpenSSL](https://github.com/openssl/openssl/releases/tag/openssl-3.6.3)

## Android

Используйте Linux x86-64 или WSL 2. Нужны CMake, Ninja, C/C++-компилятор для хоста, `gperf`, Perl, Make, `curl`, `tar` и `unzip`. Для промежуточных файлов скрипт по умолчанию использует Linux-каталог `~/.cache/tgdeblocker-tdlib-mobile`. Он скачивает NDK r28c для Linux и проверяет опубликованную SHA-1 сумму. При уже установленном NDK той же ревизии задайте `ANDROID_NDK_HOME` абсолютным путём к нему. [NDK r28c](https://github.com/android/ndk/wiki/Unsupported-Downloads#r28c) · [OpenSSL для Android](https://github.com/openssl/openssl/blob/openssl-3.6.3/NOTES-ANDROID.md)

```bash
bash tools/build-tdlib-mobile-android.sh arm64-v8a
install -D artifacts/mobile-native/android/arm64-v8a/libtdjson.so \
  src/TGDeblocker.Mobile/Native/Android/arm64-v8a/libtdjson.so
```

Для x86-64 эмулятора используйте аргумент `x86_64` и тот же каталог с именем этой ABI. `TGDEBLOCKER_BUILD_JOBS` задаёт число параллельных задач сборки. Для машины с ограниченной памятью начните с `2` или `4`. `TGDEBLOCKER_BUILD_DIR` и `TGDEBLOCKER_NATIVE_OUTPUT` переопределяют рабочую папку и папку результата.

Android-библиотека рассчитана на API 26 и выше. OpenSSL и стандартная библиотека C++ связаны статически. `libz.so` предоставляется Android. Скрипт проверяет выравнивание сегментов ELF на 16 КиБ. Отдельно проверьте выравнивание несжатых библиотек в итоговом APK через `zipalign -c -P 16 -v 4 app.apk`, а в AAB проверьте `bundletool dump config --bundle=app.aab`. [Руководство Android по страницам 16 КиБ](https://developer.android.com/guide/practices/page-sizes)

В приложении укажите только те `RuntimeIdentifiers`, для которых собран `libtdjson.so`. `android-arm64` соответствует `arm64-v8a`, `android-x64` соответствует `x86_64`. Тестовый сценарий с вымышленными данными работает без TDLib. Для подключения к Telegram нужна нативная библиотека соответствующей архитектуры.

Для проверки загрузки `tools/build-tdlib-mobile-smoke.c` собирается компилятором NDK для нужной архитектуры, например `x86_64-linux-android26-clang -O2 tools/build-tdlib-mobile-smoke.c -ldl -o tdlib-smoke`. Запустите `tdlib-smoke` на Android, передав абсолютный путь к `libtdjson.so` той же ABI. Тест проверяет открытие библиотеки, получение начального состояния и закрытие клиента без параметров подключения.

## iOS

Используйте Mac с Xcode и iOS SDK. Нужны CMake, Ninja, `gperf`, Perl, Make и `curl`. Скрипт собирает статический XCFramework для устройства arm64 и симулятора arm64. `TGDEBLOCKER_IOS_SIMULATOR_X64=1` добавляет x86-64 к срезу симулятора. Сборку на macOS ещё предстоит проверить. [XCFramework в документации Apple](https://developer.apple.com/documentation/xcode/creating-a-multi-platform-binary-framework-bundle)

```bash
bash tools/build-tdlib-mobile-ios.sh
mkdir -p src/TGDeblocker.Mobile/Native/iOS
cp -R artifacts/mobile-native/ios/TDLib.xcframework src/TGDeblocker.Mobile/Native/iOS/
```

Для каждого среза скрипт использует zlib из выбранного iOS SDK, проверяет пять символов JSON ABI и выполняет пробную линковку с `ForceLoad`, `libc++`, zlib, Foundation и Security. MAUI связывает XCFramework с теми же системными библиотеками. Символ `TDLIB_IOS_NATIVE` разрешает вызовы `__Internal` только при наличии XCFramework. [NativeReference в документации .NET для iOS](https://learn.microsoft.com/en-us/dotnet/ios/building-apps/build-items#nativereference)

## Уведомления о компонентах

После восстановления проекта и сборки TDLib запустите `tools/collect-mobile-notices.ps1` из PowerShell 7. Параметр `-DotnetRoot` указывает каталог используемого .NET SDK. Скрипт читает `project.assets.json`, локальные пакеты и тексты лицензий из папки нативной сборки. Для инвентаря двух Android ABI передайте `-AndroidRuntimeIdentifiers android-arm64,android-x64`. Текст для MAUI появляется в `Resources/Raw/third-party-notices.txt`, машиночитаемый список в `release/mobile-dependencies.json`.
