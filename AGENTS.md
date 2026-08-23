# AGENTS.md

## Проект

VoxCore — голосовой чат для игр. Репозиторий: https://github.com/R3G1ST/VoxCore, рабочая ветка: `ui`.

- **VoxCore.Client** — WinUI 3, .NET 8 (`net8.0-windows10.0.19041.0`), Discord-подобный UI.
  Тема: тёмная `#1e1f22` / `#2b2d31` / `#313338`, акцент `#5865f2`, шрифт Cascadia Code (см. `UI_GUIDE.md`).
- **VoxCore.Server** — UDP 9987 (голос), TCP 9988 (newline-JSON API).
  ⚠️ НЕ ТРОГАТЬ: задеплоен на 194.31.204.5. Изменения в VoxCore.Server — только по явной просьбе.

## Сборка

Требуется .NET 8 SDK и Git (оба установлены через winget).

**Клиент** (единственный рабочий способ; см. нюанс ниже):

```
dotnet build VoxCore.Client/VoxCore.Client.csproj -p:Platform=x64 -c Release
```

Публикация portable-сборки (как в CI, `.github/workflows/build.yml`):

```
dotnet publish VoxCore.Client/VoxCore.Client.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -o publish
```

**Сервер** (локально, только для отладки):

```
dotnet build VoxCore.Server/VoxCore.Server.csproj -c Release
```

### Нюансы

- `dotnet build VoxCore.sln` падает с ошибкой `NETSDK1032`: в клиентском csproj жёстко задан `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`, а платформу надо передавать явно через `-p:Platform=x64`. Это известная особенность, не пытаться «чинить» без необходимости.
- В репозитории есть кастомный XAML-фикс (`build-tools/`, подключается через `Directory.Build.props` / `Directory.Build.targets`) для воспроизводимой сборки WinUI-компилятора — не удалять.
- Сборка клиента выдаёт ~18 warnings (nullable CS8602/CS8601/CS4014) — это норма, не ошибки.
