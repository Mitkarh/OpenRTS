# 035. Манифест игры и рекурсивные YAML-источники

Дата: 15 ноября 2026 года. [Решение](../decisions/035-game-manifest.md) · [План параметров](../research/game-configuration/README.md).

## Пройдено

- Сборка решения: 0 предупреждений и ошибок.
- `tools/dev.sh test`: 20 проверок DeclarativeContent и 332 проверки OpenRTS, 0 ошибок. Пакеты брались из локального `.packages` через `OPENRTS_NUGET_SOURCE`; restore выполнялся вне ограниченного sandbox.
- 5 новых Configuration checks: физические/виртуальные директории, рекурсивность и ordinal order, смешанные file/directory sources, дедупликация путей, межфайловое наследование, duplicate IDs, source diagnostics, строгие настройки и применение к миру.
- Реальные каталоги через манифест совпадают с прежними списками: diagnostic — 12 определений, полный — 40, fingerprints сохранены. Models/Effects загружаются.
- Временный самостоятельный корень: units перенесён в `content/rules/nested`, buildings — в `content/rules`, манифест подключает директорию. При TickRate=40/SpeedPerTick=64 headless и Godot на fixture-карте за 4 тика дали одинаковый state hash `AFB22957E69C4E0BDEDB7C6F4157D0744BF359BBE69618FAC266F4D126C067C2`.
- После переименования в `game.yaml`/`diagnostic-game.yaml`: повторная сборка, Configuration checks, default headless, явный `--mod=sandbox/game.yaml` с картой и Godot `--mod=res://game.yaml --verify-match-setup` прошли.
- Сквозные проверки до остановки: fog и настроенный hidden match; static/direct movement на 1/10000/10001 объектах, FPS15/120, realtime pacing; navigation1/10000 sequential/parallel/reverse/Godot; управление, представление, RTS controls, aviation и здания.
- Оставшиеся блоки запускались отдельно: production journal, skirmish UI и 1800-tick worker parity, match setup, все четыре файловые карты с альтернативными стартами, ошибки контента/аргументов — прошли.

## Полный verify не зелёный

`tools/verify.py` останавливается на `--verify-actions`: `An engineer did not complete the selected APC variant`. При отдельном продолжении `--verify-ai-match --ai-profile=infantry-skirmish` завершается с `The human and AI did not each place one project on the single step.`

Оба отказа воспроизведены в изолированном sandbox с `Sandbox.cs`, `Controls/Sandbox.MatchSetup.cs`, `Controls/MatchSetupScreen.cs`, `Scenarios/MapScenario.cs` из HEAD до этой доработки, без нового `Sandbox.Content.cs`, с прежней загрузкой каталогов. Использовались те же Gameplay/Core и ресурсы; реализация их механик этой задачей не менялась. Эти ошибки не исправлялись в рамках конфигурации. До mechanized AI-профиля общий прогон не дошёл.

Экспортированный Godot-пакет отдельно не проверялся. Прототипные лаборатории сохраняют фиксированные параметры, настройки Simulation применяются к картам. Новых замеров скорости симуляции не делалось: загрузчик работает до создания мира.
