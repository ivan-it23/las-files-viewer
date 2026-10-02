# Просмотрщик LAS

Приложение для просмотра, обработки и анализа LAS-файлов геофизических
исследований скважин с ИИ-агентом. Разрабатывается для интерпретаторов
ООО НПФ «АМК ГОРИЗОНТ».

- пользовательское имя — **«Просмотрщик LAS»**;
- исполняемый файл — `LasViewer.exe`;
- версия — `0.1.0`: единственный источник — `Version` в `LasViewer/LasViewer.csproj`,
  `__version__` движка обязан с ней совпадать (сверяет `scripts/Build-Installer.ps1`).

План работ и список функций — [`docs/PLAN.md`](docs/PLAN.md), устройство —
[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

## Состав

```text
LasViewer.slnx
├── LasViewer/                      WinUI 3: окна и сценарии
├── src/AMK.LasViewer.Engine/       запуск движка и клиент JSON-RPC
├── engine/                         Python-движок (пакет lasviewer_engine)
│   ├── lasviewer_engine/
│   ├── tests/
│   └── requirements.txt            пакеты для встроенного Python
├── tests/AMK.LasViewer.Engine.Smoke/  проверка связи C# ↔ движок
├── installer/setup.iss             установщик Inno Setup
└── scripts/
    ├── Prepare-Python.ps1          встроенный Python → artifacts\python
    └── Build-Installer.ps1         публикация, проверки, установщик
```

## Разработка

Нужны .NET SDK 10, Python 3.12 и (для установщика) Inno Setup 6.

При запуске из Visual Studio или `dotnet build` встроенного Python нет, и
движок запускается Python разработчика из исходников `engine/`. Интерпретатор
выбирается так: переменная `LASVIEWER_PYTHON`, затем `.venv` в корне
репозитория, затем `python.exe` из `PATH`.

```bash
dotnet build LasViewer.slnx -c Debug -p:Platform=x64
```

Тесты движка:

```bash
python -m unittest discover -s engine/tests
```

Проверка связи C# ↔ движок на настоящем процессе:

```bash
dotnet run --project tests/AMK.LasViewer.Engine.Smoke
```

Журналы приложения и вывод движка — `%LOCALAPPDATA%\AMK\LasViewer\logs`
(меню «Движок → Открыть папку журналов»).

## Установщик и обновления

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Prepare-Python.ps1   # один раз и при смене requirements.txt
powershell -ExecutionPolicy Bypass -File scripts\Build-Installer.ps1
```

В `installer\Output` появятся `LasViewer-Setup-<версия>.exe` и
`las-viewer-version.txt`. Оба файла выкладываются в
`https://amk-horizon.ru/downloads/las-viewer/` — оттуда их берёт
автообновление. Канал обновлений работает только по HTTPS.
