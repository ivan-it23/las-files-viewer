---
name: las-verify
description: Проверить «Просмотрщик LAS» запуском — собрать в отдельную папку (не мешая открытому приложению и Visual Studio), открыть файлы, нажать кнопки через UI Automation, снять только окно программы, задать вопрос агенту и прочитать ответ. Загружать, когда изменение нужно подтвердить в окне, а не только тестами.
---

# Проверка в окне

Успешная сборка — не проверка работы. После изменений интерфейса или
агента — запустить и посмотреть. Записать в `docs/STATUS.md`, что проверено
тестами, что запуском.

## 1. Сборка в отдельную папку

У пользователя часто открыто приложение — файлы `LasViewer\bin` заняты.
`-p:OutDir` не помогает (компилятор XAML всё равно копирует `.xbf` в `bin`),
нужен `BaseOutputPath`:

```bash
dotnet build LasViewer/LasViewer.csproj -c Debug -p:Platform=x64 -p:BaseOutputPath=<scratchpad>/bin/
```

Рядом с `LasViewer.exe` (`<scratchpad>/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64`)
скопировать `engine\` (без `tests`) — иначе движок не найдётся.

## 2. Запуск и действия — `ui.ps1`

Сценарий лежит рядом с этим файлом. Скопировать его в scratchpad и
запускать оттуда (Windows PowerShell 5.1, файл в UTF-8 с BOM):

```powershell
powershell -ExecutionPolicy Bypass -File ui.ps1 -Action start -Build <папка с LasViewer.exe> -Work <scratchpad> -Files "C:\...\51_ВИКИЗ.las"
powershell -ExecutionPolicy Bypass -File ui.ps1 -Action click -Work <scratchpad> -Name "Свернуть панель проекта"
powershell -ExecutionPolicy Bypass -File ui.ps1 -Action shot -Work <scratchpad> -Out shot1
powershell -ExecutionPolicy Bypass -File ui.ps1 -Action ask -Work <scratchpad> -Question "Проверь качество записи ВИКИЗ"
powershell -ExecutionPolicy Bypass -File ui.ps1 -Action read -Work <scratchpad>
powershell -ExecutionPolicy Bypass -File ui.ps1 -Action stop -Work <scratchpad>
```

- `start` — запускает с `LASVIEWER_PYTHON` (системный Python) и
  `LASVIEWER_AGENT_SETTINGS` в scratchpad: настройки пользователя не
  трогаются, модель — как при первом запуске.
- `click` — `InvokePattern` по имени (AutomationProperties.Name / подсказке).
- `shot` — **только окно программы** (`PrintWindow`), PNG в scratchpad.
- `ask` — вписывает вопрос в поле агента и нажимает «Отправить». Ответ
  модели — 30–120 с; ждать перед `read`.
- `read` — все тексты окна в `answer.txt` (UTF-8): вызовы инструментов
  («✓ vikiz_layers»), ответ, расход.

## Нельзя

- **Снимать экран** (`CopyFromScreen`): окно может не выйти на передний
  план, и в снимок попадут личные окна пользователя. Только `PrintWindow`.
- **SendKeys** и эмуляцию клавиатуры — нажатия уходят в окно Claude.
- Оставлять снимки и ответы после проверки — удалить из scratchpad.
- Трогать запущенный пользователем экземпляр: работать только с PID из
  `pid.txt`.

Масштаб экрана пользователя 125 %: координаты снимков ×1,25.
