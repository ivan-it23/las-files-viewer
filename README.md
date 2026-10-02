# Просмотрщик LAS

Приложение для просмотра, обработки и анализа LAS-файлов геофизических
исследований скважин с ИИ-агентом. Разрабатывается для интерпретаторов
ООО НПФ «АМК ГОРИЗОНТ».

- пользовательское имя — **«Просмотрщик LAS»**;
- исполняемый файл — `LasViewer.exe`;
- версия — `0.3.0`: единственный источник — `Version` в `LasViewer/LasViewer.csproj`,
  `__version__` движка обязан с ней совпадать (сверяет `scripts/Build-Installer.ps1`).

План работ и список функций — [`docs/PLAN.md`](docs/PLAN.md), устройство —
[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

## Состав

```text
LasViewer.slnx
├── LasViewer/                      WinUI 3: окна, планшет, таблица, вкладки
├── src/AMK.LasViewer.Core/         модель открытого LAS-файла
├── src/AMK.LasViewer.Charting/     математика планшета (без WinUI)
├── src/AMK.LasViewer.Engine/       запуск движка, JSON-RPC, методы las.*
├── engine/                         Python-движок (пакет lasviewer_engine)
│   ├── lasviewer_engine/
│   ├── tests/
│   └── requirements.txt            пакеты для встроенного Python
├── tests/AMK.LasViewer.Engine.Smoke/  проверка связи C# ↔ движок
├── tests/AMK.LasViewer.Checks/     проверки планшета и чтения LAS через движок
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

Проверка связи C# ↔ движок на настоящем процессе и проверки планшета:

```bash
dotnet run --project tests/AMK.LasViewer.Engine.Smoke
```

```bash
dotnet run --project tests/AMK.LasViewer.Checks
```

Тесты на реальных файлах скважины 3201 берут их из `LASVIEWER_SAMPLES_DIR`
или из папки загрузок; данные заказчика в репозиторий не кладутся.

Файлы можно открыть сразу при запуске: `LasViewer.exe путь\к\файлу.las …` или`nпроект `LasViewer.exe путь\к\проекту.lvproj`.`n`n## Данные пользователя`n`n| Что | Где |`n| --- | --- |`n| Проект (файлы, шаблоны, маркеры, интервалы, раскладка) | `*.lvproj` там, куда сохранил пользователь |`n| Библиотека шаблонов планшета | `%LOCALAPPDATA%\AMK\LasViewer\templates\*.lvtpl` |`n| Журналы | `%LOCALAPPDATA%\AMK\LasViewer\logs` |`n`nLAS-файлы программа не изменяет.

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
