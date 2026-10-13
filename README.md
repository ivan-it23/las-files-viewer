# Просмотрщик LAS

Приложение для просмотра, обработки и анализа LAS-файлов геофизических
исследований скважин с ИИ-агентом. Разрабатывается для интерпретаторов
ООО НПФ «АМК ГОРИЗОНТ».

- пользовательское имя — **«Просмотрщик LAS»**;
- исполняемый файл — `LasViewer.exe`;
- версия — `0.4.1`: единственный источник — `Version` в `LasViewer/LasViewer.csproj`,
  `__version__` движка обязан с ней совпадать (сверяет `scripts/Build-Installer.ps1`).

План работ и список функций — [`docs/PLAN.md`](docs/PLAN.md), устройство —
[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md), состояние работ и что дальше —
[`docs/STATUS.md`](docs/STATUS.md), формат LAS компании и программа GORIZONT —
[`docs/DOMAIN.md`](docs/DOMAIN.md).

## Состав

```text
LasViewer.slnx
├── LasViewer/                      WinUI 3: окна, планшет, таблица, вкладки
├── src/AMK.LasViewer.Core/         модель открытого LAS-файла
├── src/AMK.LasViewer.Charting/     математика планшета (без WinUI)
├── src/AMK.LasViewer.Engine/       запуск движка, JSON-RPC, методы las.*
├── src/AMK.LasViewer.Agent/        ИИ-агент: клиент посредника, цикл с инструментами, настройки
├── server/LlmController.php        посредник к polza.ai для хостинга (ключ — только на хостинге)
├── engine/                         Python-движок (пакет lasviewer_engine)
│   ├── lasviewer_engine/
│   ├── tests/
│   └── requirements.txt            пакеты для встроенного Python
├── tests/AMK.LasViewer.Engine.Smoke/  проверка связи C# ↔ движок
├── tests/AMK.LasViewer.Checks/     проверки планшета, чтения LAS и агента
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

Файлы можно открыть сразу при запуске: `LasViewer.exe путь\к\файлу.las …` или
проект `LasViewer.exe путь\к\проекту.lvproj`.

## Данные пользователя

| Что | Где |
| --- | --- |
| Проект (файлы, шаблоны, маркеры, интервалы, раскладка) | `*.lvproj` там, куда сохранил пользователь |
| Библиотека шаблонов планшета | `%LOCALAPPDATA%\AMK\LasViewer\templates\*.lvtpl` |
| Выбранная модель агента | `%LOCALAPPDATA%\AMK\LasViewer\agent-settings.json` |
| Скрипты агента: код, графики, таблицы — папка на каждый запуск | `%LOCALAPPDATA%\AMK\LasViewer\scripts` |
| Журналы | `%LOCALAPPDATA%\AMK\LasViewer\logs` |

LAS-файлы программа не изменяет.

## ИИ-агент

Панель «Агент» справа. Вопрос уходит посреднику на сайте компании —
`https://amk-horizon.ru/las-viewer/LlmController.php` (исходник —
[`server/LlmController.php`](server/LlmController.php), на хостинге —
`amk-horizon.ru/public_html/las-viewer/`). Посредник подставляет ключ polza.ai
и модель `deepseek/deepseek-v3.2-exp` — те же, что у ИИ-агента horizon.ru, —
и передаёт ответ потоком. Инструменты агента выполняет движок на рабочей
станции (`engine/lasviewer_engine/agent_tools.py`), модель получает только их
результаты.

Для расчётов и графиков агент пишет скрипты Python (`run_python`). Каждый
скрипт пользователь видит и подтверждает; скрипт выполняется отдельным
процессом движка без сети и без права писать вне своей папки, графики
показываются в чате, отчёты Word открываются ссылкой. Пакеты скриптов —
numpy, pandas, matplotlib, python-docx (`engine/requirements.txt`); у Python разработчика они должны быть
установлены, иначе скрипт вернёт понятную ошибку.

Пользователю ничего настраивать не нужно: программа предъявляет посреднику
встроенный ключ клиента (`AgentSettings.BuiltInClientKey`). Ключ polza.ai
вписывается только в копию `LlmController.php` на хостинге — в репозитории
вместо него заглушка.

Проверка с учебным посредником без сайта:
`LASVIEWER_AGENT_SETTINGS` указывает на отдельный файл настроек с адресом
`http://127.0.0.1:…` (http допускается только для этого компьютера).

Журналы приложения и вывод движка — `%LOCALAPPDATA%\AMK\LasViewer\logs`
(меню «Движок → Открыть папку журналов»).

## Установщик и обновления

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Prepare-Python.ps1   # один раз и при смене requirements.txt
powershell -ExecutionPolicy Bypass -File scripts\Build-Installer.ps1
```

В `installer\Output` появятся `LasViewer-Setup-<версия>.exe` и
`las-viewer-version.txt`. Оба файла выкладываются на хостинге в
`ivanbohg.beget.tech/public_html/FilesForProgramVisualisation` — рядом с
обновлениями других приложений; автообновление берёт их по адресу
`http://ivanbohg.beget.tech/FilesForProgramVisualisation/`.
