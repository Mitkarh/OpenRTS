# 047. Проверки независимой карты B0–B1

**Дата:** 18 ноября 2026. macOS/arm64, .NET SDK 10.0.105, Godot 4.7.2 .NET, Debug. Основа — `92ae9cab4028e5f6b3f120a0c479adefba4dc3e5` и рабочие изменения B0–B1. [Контракт 047](../decisions/047-map-compilation.md), [этапы B](../research/godot-integration/stage-b-design.md).

## Независимый M1 baseline

До сравнения извлечены исходные HEAD версии GameMap/MapMatch/MapYaml/TerrainLayout/StartingSet в временный reference project. Они собраны отдельно с прежним загрузчиком и неизменёнными definitions/Gameplay, не через новый MapCompiler. Четыре штатные карты: fixture, skirmish, crossroads, riverside-station. Seed 77, один worker, MoveAll на тике 1 в `(12288,6144)`, затем до тика 32.

Сохранены 33 state hashes каждой карты, размеры партий и одиночные load/alloc samples. [Исходные результаты](047-map-compilation/baseline.json), [результаты новой реализации](047-map-compilation/current.json). Все **132** границы совпали. Это проверка первоначального M1 loader против нового пути, а не сравнение двух вызовов одного нового builder.

Новые тесты также сравнивают M1 → MapSource JSON → compiler → compiled reader → World при workers 1/4, обратном порядке assignments и тех же командах. Сравниваются отчёты каждого тика и все terrain/visual клетки, точные позиции, owners и порядок объектов.

## Автоматические проверки

Выполнены штатные `tools/dev.sh test` и `tools/dev.sh test --filter=Maps`; использован локальный NuGet feed и отключён audit во время восстановления. Полная сборка решения: **0 warnings / 0 errors**.

- Network — **81/81**.
- DeclarativeContent — **20/20**.
- Итоговый целевой набор Maps — **14/14**, включая 6 новых compiled-map проверок; после последней сборки повторён на актуальных binaries.
- Полный Gameplay прогон — **354 pass / 8 fail**. Перечень совпадает с уже отмеченными baseline-сбоями: factory repair, пять ECS golden traces, skirmish production/income/power, Valley supply contract. Они не исправлялись и не объявляются исправленными этим срезом. [Предыдущая фиксация известных восьми](045-network-audit.md), [вывод проверок](047-map-compilation/checks.txt).

Основные новые сценарии:

1. Исходный M1 baseline и compiled путь дают те же состояния для четырёх карт.
2. Перестановка palette/slot entries и форматирование JSON не меняют канонический результат. Порядок objects сохраняется и меняет gameplay identity/state при перестановке.
3. Visual-only правка не меняет gameplay digest/World, но меняет полный data digest; подмена visual без корректного data digest отклоняется.
4. Другой каталог, неверные/duplicate поля, неподдержанные format/compiler/geometry versions, trailing JSON, дробные/лишние координаты, переполнение размеров, неизвестные type/terrain/owner, duplicate IDs, неверные palette indices и слишком длинные IDs отвергаются.
5. DTO не сохраняет изменяемые caller arrays; несовместимый body placement отклоняется при World creation, следующий запуск корректен. Cancel не заменяет уже сохранённый artifact. Content assembly не зависит от Godot/Network.
6. Малый независимый fixture 4×3 создаёт двух юнитов. Отдельно валидная skirmish карта отвергает недопустимый starting set и остаётся пригодной с правильным набором.

## CLI и действующий Godot host

Успешны:

```sh
./tools/dev.sh headless --map=content/maps/fixture.yaml --compile-map=/tmp/openrts-fixture.ortmap.json
./tools/dev.sh headless --map=/tmp/openrts-fixture.ortmap.json --ticks=32
```

После общей сборки исполнялись те же headless binaries без повторного build. Отдельно проверены компиляция [tiny mapsource](../../sandbox/content/maps/compilation-fixture.mapsource.json) и запуск его результата на 2 тиках.

Godot запущен с `--headless --path sandbox -- --verify-simulation --map=/tmp/openrts-fixture.ortmap.json --ticks=32`. Процесс завершился с code 0. M1 headless, compiled headless и compiled Godot дали одинаковый отчёт: tick 32, 3 entities, hash `F32969AC2EBC2531641DD5D0ABD2505CC93C31BF0FCC355017365DF51D3CE864`. [Отчёты хостов](047-map-compilation/hosts.json).

В логе Godot было прежнее сообщение macOS `get_system_ca_certificates`; конечная проверка карты прошла. Визуальная оценка и пользовательское редактирование в Godot не выполнялись: нового editor plugin ещё нет. Этот запуск использует нынешнюю Sandbox scene с compiled игровым входом, а не новый loader произвольной entry сцены карты.

## Размеры и стоимость подготовки

В `current.json` записаны одиночные Debug samples M1 load, отдельного compile, чтения готового файла, managed allocations и размера файла. Карты дали 1804 / 19264 / 19980 / 23495 bytes compiled JSON. M1 load в новой реализации включает проверку и fingerprints, поэтому не сравнивается с прежним parsing как одинаковая операция. Первые вызовы содержат JIT; отдельные compile/read идут уже после части прогрева.

Это **baseline для последующих работ**, не сравнительный benchmark и не доказательство ускорения. Нет percentile-статистики, полного учёта всех worker allocations, Release-профиля больших карт и editor/GPU measurements. B1 решает независимый контракт, целостность и одинаковый игровой вход; кеширование дорогой навигации/ИИ не добавлено.

## Границы результата

B0–B1 выполнены. Godot extractor/addon, полноценный scene-map manifest/loader, регистрация такого формата в меню, начальные HP/variants, authoring ID receipt и новый Network recipe остаются B2–B6. YAML M1 сохранён, но новый общий путь имеет явно зафиксированные квоты формата; безусловная совместимость ранее неограниченных по длине/числу записей входов не обещается.
