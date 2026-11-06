# TGDeblocker

Неофициальное приложение для Windows и Android на основе TDLib. Проект не связан с Telegram.

- Просмотр активных устройств и завершение сеансов.
- Управление двухэтапной защитой и почтой восстановления.
- Русский и английский языки, светлая и тёмная темы.
- Демонстрационный режим без входа в Telegram.

## Установка и обновление

**Windows 10 2004+, x64:** [установщик](https://github.com/moz9/TGDeblocker-GUI/releases/latest) с выбором папки. Обновления проверяются в разделе «Настройки» приложения.

**Android 8+, ARM64:** [APK](https://github.com/moz9/TGDeblocker-GUI/releases/tag/android-v1.0.2). Подключение к Telegram уже настроено — нужен только вход в свой аккаунт. Устанавливается вручную; обновление Android внутри приложения пока не реализовано.

В Windows настройки и локальная сессия хранятся в `%LOCALAPPDATA%\TGDeblocker` и сохраняются при обновлении. При удалении программы их можно удалить отдельно. Windows 1.1.0 пока требует API ID и API Hash с [my.telegram.org](https://my.telegram.org/apps). Реальный вход пока не тестировался.

## Сборка

Нужны Windows x64, SDK .NET с поддержкой `net8.0` и Windows SDK.

```powershell
dotnet build TGDeblocker.WinUI.sln -c Release -p:Platform=x64
dotnet test tests/TGDeblocker.Core.Tests/TGDeblocker.Core.Tests.csproj
./tools/publish.ps1
```

Для установщика используйте `./tools/build-installer.ps1 -PayloadDirectory <путь> -Version <версия>` и Inno Setup. [Сборка Android](src/TGDeblocker.Mobile/README.md).

Основано на [TGDeblocker](https://github.com/Astaro212/TGDeblocker) автора [Astaro212](https://github.com/Astaro212). Уведомления о зависимостях — в [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
