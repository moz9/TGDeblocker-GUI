# TGDeblocker для Android

MAUI 10, общее ядро `TGDeblocker.Core` и официальный TDLib. Android 8+, ARM64. Русский и английский языки; тема и язык по умолчанию следуют системе.

На Android 13+ Liquid Glass преломляет содержимое приложения через RenderNode/AGSL. На Android 8–12 используется упрощённый материал. Есть настройки уменьшения движения и непрозрачных поверхностей.

## Сборка

Нужны .NET SDK 10.0.403, workload `maui-android`, Android SDK 36 и JDK 17. Сначала [соберите TDLib](../../tools/README-tdlib-mobile.md). На Windows используйте checkout с латинским путём — AAPT2 может не обрабатывать кириллицу.

```powershell
./tools/build-mobile.ps1 -Runtime android-arm64 -AndroidSdk <sdk> -JavaSdk <jdk>
```

Результат находится в `bin/Release/net10.0-android/android-arm64/publish/`. По умолчанию используется отладочная подпись. Для выпуска APK подпишите его своим постоянным ключом; скрипт `tools/sign-android-apk.ps1` использует отдельно сохранённое хранилище и пароль, защищённый Windows DPAPI. Ключи не включаются в Git.

На отдельном эмуляторе `TGDeblocker_API36` проверку офлайн-демо и возврата из фона запускает `tools/test-android-smoke.py --reset-demo-data`. Команда очищает только данные приложения на этом тестовом эмуляторе.

Реальный вход в Telegram и работа на физическом ARM64-телефоне пока не проверены. Исходники iOS предварительные; готового IPA нет.