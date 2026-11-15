# Конфигурация игры

**Статус:** первый этап реализован; дальнейшие параметры ниже — предложения.
**Контракт:** [решение 035](../../decisions/035-game-manifest.md).

## Задача и текущий результат

Каталоги уже принимали наборы ContentSource и разрешали наследование между файлами. До этого изменения sandbox и headless независимо составляли списки units/buildings/terrain/weapons/models/effects в C#. Теперь общий загрузчик раскрывает манифест перед существующим pipeline. Симуляция получает типизированные данные без YAML/I/O.

Референс — локальный [OpenRA mod.yaml](../../../../OpenRA/mods/cnc/mod.yaml): отдельные категории источников правил, оружия, графики и других данных. Взята идея корневого списка; реализация самостоятельная, GPL-код и MiniYaml не переносились. Рекурсивные директории — контракт OpenRTS, а не утверждение о поведении OpenRA.

Рабочие файлы: [game.yaml](../../../sandbox/game.yaml), [diagnostic-game.yaml](../../../sandbox/diagnostic-game.yaml), [simulation.yaml](../../../sandbox/content/settings/simulation.yaml), [match.yaml](../../../sandbox/content/settings/match.yaml). Первый задаёт полный игровой каталог, второй сохраняет состав лабораторий.

Примеры запуска из корня репозитория:

```sh
./tools/dev.sh run --mod=res://game.yaml
./tools/dev.sh headless --mod=sandbox/game.yaml --map=content/maps/skirmish.yaml --ticks=10
```

Пример раскладки без дополнительных изменений C#:

```text
game.yaml                    # Rules: [content/rules], Weapons: [content/weapons], ...
content/
  rules/
    defaults.yaml           # ^templates
    infantry/rifleman.yaml
    infantry/engineer.yaml
    vehicles/tanks.yaml
    buildings/economy.yaml
  weapons/
    ballistics.yaml
    missiles.yaml
  models/
    infantry.yaml
    buildings.yaml
  settings/
    simulation.yaml
    match.yaml
```

Все файлы под rules передаются одному UnitCatalog. Группировка по фракциям, технологическим веткам или типам — выбор автора игры; имена директорий не добавляются к ID. Порядок файлов не служит механизмом переопределения баланса: одинаковые ID запрещены. Новые файлы внутри подключённой директории обнаруживаются автоматически.

## Распределение параметров

Каждый параметр получает одну типизированную секцию и одного потребителя. Файлы организуют данные; они не меняют их владельца. Несколько файлов с повторным `Simulation:` сейчас не сливаются: секцию нужно собрать в одном файле.

| Файл / секция | Что хранить | Статус и следующий шаг | Владелец / хеш |
| --- | --- | --- | --- |
| `settings/simulation.yaml` / Simulation | TickRate=25, SpeedPerTick=128 | Реализовано для карт. Скорость — базовый ход; terrain modifiers уже у локомоторов | Аргументы мира, входят в существующий state hash |
| `settings/match.yaml` / Match | DefaultMap/DefaultStartSet/AiConfig/AiProfile, Maps/StartingSets с подписями | Реализовано. Далее default числа игроков, FogMode, доступность ИИ с проверкой ограничений карты | Хост/меню; выбранные игровые значения попадают в подготовленный матч, подписи локальны |
| `settings/world.yaml` / World | FogCellSize (сейчас sandbox задаёт 1024), FogVisibilityHoldTicks (CLI 0..250), начальная раскрытость карты | Следующий игровой срез. Retention пока экспериментальный; default FogMode уже переопределяется меню | GameplayMechanics/visibility; значения, меняющие результат, канонически сериализуются |
| `start-sets/*.yaml` | credits, типы и точные offsets стартовых юнитов | Уже существует. Расширять фракциями/режимами; credits не дублировать в глобальных defaults | StartingSet → начальное состояние мира |
| `maps/*.yaml` | Размеры, cellSize, terrain, surface, объекты/стартовые слоты по текущему формату | Уже есть. Seed/настройки генерации расширять вместе с генератором; не делать картовые параметры глобальными обязательными defaults | GameMap/подготовка матча |
| `settings/player.yaml` / Player | Будущая дипломатия, правила neutral, поражение при потере базы/всех объектов | Предложение. Сейчас PlayerDefinition и прототип враждебности заданы кодом, полного Diplomacy нет | Gameplay и initial Players; новые правила требуют версии и хеша |
| `settings/economy.yaml` / Economy | Будущие глобальные refunds, lowpower slowdown/disable policy | Предложение. Сначала уточнить контракты 020/024. Формулы оплаты сейчас алгоритм; лимит кредитов пока оставить защитой диапазона | Production/Economy; целочисленная арифметика и canonical rules identity |
| `settings/production.yaml` / Production | SpawnBudgetPerTick (сейчас 32), ConstructionPayment | Предложение. Capacity/Payment/Exit/ExitSearchRadius оставить у очереди/actor; предел 256 пока ограничение контракта | Production/Construction; бюджет выпуска меняет тик результата и является правилом игры |
| `settings/navigation.yaml` / Navigation | FieldsPerTick=2, CachedFields=8, LocalSearchBudget=64; позднее таймауты уступания/остановки | После отдельной проверки. Бюджет поиска может менять продвижение на конкретном тике; классифицировать каждый параметр | Navigator/Gameplay. Workers — параметр хоста, результаты не должны от него зависеть |
| `rules/`, `terrain/`, `weapons/` | HP/тела/зрение/действия, Traverse/CanStop/Speed/Cost, damage/range/cooldowns | Уже каталоги. Свободно распределять по директориям. Баланс оружия остаётся у оружия, профиль terrain — у локомотора | Gameplay definitions, существующий fingerprint |
| `skirmish-ai.yaml`, позднее `ai/*.yaml` | Macro/tactics intervals, составы, лимиты/приоритеты построек, снабжение | Уже профиль; сейчас AiConfig указывает один файл. Несколько источников добавить с перегрузкой каталога и потребителем | AI вне Core; профиль и выдаваемые команды должны восстанавливаться для replay |
| `settings/presentation.yaml` | TeamColor/TeamMarkerColor, fog/radar display styles, zoom/pan bounds, размер выделения на экране | Следующий адаптерный срез. Ресурсы/части моделей остаются в Models/Effects; Model.Size не становится физикой | Godot/host; не входит в gameplay fingerprint |
| `settings/input.yaml`, `hotkeys.yaml` | Глобальные клавиши, double-click interval, drag threshold, скорость камеры, размер batch команд | Предложение. ActionButtonBinding уже отделён от действий. Результат ввода фиксируется как команда | Локальный ввод; preferences не меняют проигрывание записанных команд |
| `settings/audio.yaml`, `ui.yaml` | Будущие sound IDs/уведомления/локализация, тема/масштаб HUD | После появления соответствующего потребителя; пустые файлы заранее не создавать | Адаптер и локальные preferences |
| Пользовательский файл вне мода | FPS cap, громкость, binding overrides, workers, разрешение окна, локальные display styles | Будущий механизм. CLI остаётся текущим способом overrides, персональные файлы не коммитятся | Хост/профиль пользователя, отдельно от общих правил матча |

TickRate меняет длительность тика и действий в секундах; PlaybackSpeed меняет только реальное расписание сессии. Меню скорости не должно незаметно менять правила производства/перезарядки. SpeedPerTick задаётся в world units, CellSize не пересчитывает его автоматически.

## Что оставлять в коде

LocalPath capacity=16, пределы mounts/profiles/animations/states, глубина YAML, joint cell budget и версии хешей — ограничения реализации. Их вынос в YAML не обеспечивает корректность произвольных значений. Порядок фаз, округление, tie-break по ID, коллизии и ECS ownership тоже остаются кодом.

Если игре понадобится менять такой предел, сначала расширить представление данных и проверить крайние значения/стоимость. YAML не выбирает произвольные CLR-типы, assemblies и callbacks. Регистрация механик по ID возможна позже при появлении потребителя; сейчас манифест управляет данными.

## Этапы и проверяемые гипотезы

1. **Манифест и базовые настройки — реализовано.** Гипотеза: раскрытия источников достаточно для свободной структуры. Сравнить fingerprint объединённого текста/поддиректорий, порядок виртуальной/физической файловых систем и прежние каталоги. Ошибки структуры завершают загрузку до создания мира.
2. **World/Match defaults — предложение.** Вынести fog cell size, default FogMode/числа игроков. Передавать одинаково в обоих хостах; меню переопределяет разрешённые defaults. Проверить масштабы сеток/режимы видимости и direct/worker/host parity. Если параметр не имеет единого смысла для карт, оставить его картовым.
3. **Presentation/Input — предложение.** Начать с существующих палитр, камеры и хоткеев. Проверка: смена UI не меняет fingerprint и воспроизведение команд, конфликты клавиш диагностируются. Пользовательский слой добавить вместе с UI сохранения preferences.
4. **Production/Economy/Navigation — предложение.** По одному правилу с canonical serialization и граничными сценариями. Навигационные бюджеты сравнивать на одинаковом журнале при разных workers; отдельно измерять первый приказ и дальнейшие тики. Если параметр меняет путь/тик выпуска, это версия правил. Если корректность зависит от константы, оставить её до переработки алгоритма.
5. **Пакеты и изменения правил картой — открытая задача.** Общие библиотеки нескольких игр, dependencies, overlays и пользовательские карты требуют отдельного разрешения конфликтов. Сейчас повторный ID остаётся ошибкой; last-wins merge не вводить заранее.

Будущий порядок overrides должен быть явным: defaults типизированной реализации → правила игры → разрешённые правила карты/лобби → подготовленный матч. Профиль пользователя меняет локальные настройки. CLI выбирает матч/overrides, которые хост проверяет до создания мира. Blanket override любых полей не планируется.

## Проверки и ограничения первого этапа

[ConfigurationChecks](../../../tests/OpenRTS.Tests/ConfigurationChecks.cs) проверяет рекурсивность, сортировку, посторонние файлы, дедупликацию путей, наследование, duplicate IDs, source diagnostics, строгие настройки и применение TickRate/SpeedPerTick с изменением state hash. Реальные манифесты сравниваются с прежними списками; Model/Effect каталоги тоже загружаются.

Не реализованы hot reload, glob/exclude, необязательные отсутствующие файлы, includes других манифестов, доступ за пределы корня, архивы/dependencies, персональные preference-файлы и сетевой обмен конфигами. Maps/StartingSets меню перечисляются явно; directories манифеста объединяют definitions и не создают пункты меню автоматически.

Исходники следующих этапов: [GameplayMechanics](../../../src/OpenRTS.Gameplay/Simulation/GameplayMechanics.cs), [конструктор мира](../../../src/OpenRTS.Gameplay/Simulation/SimulationWorld.cs), [лимит приказов](../../../src/OpenRTS.Gameplay/Orders/SimulationWorld.Orders.cs), [production budget](../../../src/OpenRTS.Gameplay/Production/ProductionSystem.cs), [local search budget](../../../src/OpenRTS.Gameplay/Simulation/SimulationWorld.Navigation.cs), [local path](../../../src/OpenRTS.Gameplay/Navigation/LocalMotionSearch.cs), [StartingSet](../../../src/OpenRTS.Content/Maps/StartingSet.cs), [AI profile](../../../src/OpenRTS.AI/SkirmishAiProfile.cs).
