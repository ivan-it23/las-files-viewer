# Просмотрщик LAS — указания для Claude

Перед работой прочитать [`docs/STATUS.md`](docs/STATUS.md): что сделано, что
не закончено, что дальше, где лежат данные и хостинг. Устройство —
[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md), данные и GORIZONT —
[`docs/DOMAIN.md`](docs/DOMAIN.md), план и функции — [`docs/PLAN.md`](docs/PLAN.md).

Навыки проекта (`.claude/skills/`): `las-domain` — данные компании,
методичка ВИКИЗ, база знаний агента; `las-verify` — проверка в окне
приложения (сборка в отдельную папку, UI Automation, снимок только окна).

Обязательно:
- Пользователь пишет по-русски — отвечать по-русски.
- Ключ polza.ai не коммитить и не выводить; в `server/LlmController.php`
  остаётся заглушка `'ВСТАВЬТЕ_КЛЮЧ_POLZA_AI'`.
- Данные заказчиков (LAS, планшеты) в git не класть.
- `git push`, выкладку на хостинг и сборку установщика — только по просьбе.
- В `C:\Users\Admin\Desktop\horizon.ru` не коммитить: там правки пользователя.
- Форматы данных и правила интерпретации не угадывать — спрашивать.
- Разбор данных и расчёты — только в движке (`engine/`), не в коде окон.
- После заметного шага обновлять `docs/STATUS.md`.

Проверки:

```bash
python -m unittest discover -s engine/tests
dotnet run --project tests/AMK.LasViewer.Checks
dotnet build LasViewer.slnx -c Debug -p:Platform=x64
```
