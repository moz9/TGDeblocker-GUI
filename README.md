# TGDeblocker

Неофициальное приложение для Windows на WinUI 3 и TDLib. Проект не связан с Telegram.

- Просмотр активных устройств и завершение сеансов.
- Управление двухэтапной защитой и почтой восстановления.
- Русский и английский языки, светлая и тёмная темы.
- Демонстрационный режим без входа в Telegram.

## Установка и обновление

Windows 10 версии 2004 или новее, x64. Скачайте установщик из [Releases](https://github.com/moz9/TGDeblocker-GUI/releases/latest) и выберите папку установки. Обновления проверяются в разделе «Настройки» установленного приложения.

Настройки, ключ базы и локальная сессия хранятся в `%LOCALAPPDATA%\TGDeblocker` и сохраняются при обновлении. При удалении программы их можно удалить отдельно; по умолчанию они сохраняются. Для подключения нужны API ID и API Hash с [my.telegram.org](https://my.telegram.org/apps). Реальный вход пока не тестировался.

## Сборка

Нужны Windows x64, SDK .NET с поддержкой `net8.0` и Windows SDK.

```powershell
dotnet build TGDeblocker.WinUI.sln -c Release -p:Platform=x64
dotnet test tests/TGDeblocker.Core.Tests/TGDeblocker.Core.Tests.csproj
./tools/publish.ps1
```

Для установщика используйте `./tools/build-installer.ps1 -PayloadDirectory <путь> -Version <версия>` и Inno Setup.

Основано на [TGDeblocker](https://github.com/Astaro212/TGDeblocker) автора [Astaro212](https://github.com/Astaro212). Уведомления о зависимостях — в [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
