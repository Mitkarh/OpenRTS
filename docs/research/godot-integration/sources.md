# Источники, доказательства и ограничения

[Обзор исследования](README.md). Дата чтения внешних материалов и исходников: **12 ноября 2026 года**. Ниже отделены факты источников, наши выводы и будущие проверки.

## Метод и исходная основа

Изучены действующая граница сборок, map/content loader, начальное размещение, session/frame boundary, sprite/terrain renderer и способ подключения адаптера во внешней игре. Затем проверены официальные Godot API для authoring, ресурсов, export, потоков, rendering и navigation. Для ECS сопоставлены три разные модели интеграции — Node/Resource addon, модуль движка и native GDExtension — с уже выбранным .NET ECS.

Поиск был направленным, не исчерпывающим обзором всех Godot addons. Для существенных технических выводов использованы официальная документация и исходные репозитории. Форумные обсуждения и сторонние каталоги не служат доказательством свойств библиотек. Внешние файлы читались через web и GitHub connector; commit IDs уточнялись через GitHub API, затем существенные ECS-файлы читались по закреплённым refs.

OpenRTS исследовался с HEAD `30195380a42424014f10bd5a413517d7b43a0738`. При начале чтения рабочее дерево было чистым. К моменту завершающей проверки появились параллельные изменения вне папки исследования: sensor/radar display, sandbox и проверки. Связанные diff `SimulationWorld`, `Sandbox` и `SpriteWorldView` просмотрены; они не меняют описанные map/content/session/authoring границы. Эти изменения не выполнены и не исправлялись в данной работе. Дальнейшее состояние изменяемого дерева не является закреплённым снимком исследования.

Внешний OpenRTS.CnC читался на `6076917e5b85162ba9eed5040723079374f6e3d8`, без изменений его файлов. Локальная конфигурация OpenRTS задаёт Godot .NET SDK `4.7.2`, `net10.0`, Frent `[0.9.1-beta]`. Фактический запуск этих версий в данном исследовании не выполнялся. Предыдущие probes отмечены как исторические результаты их авторского отчёта.

Страницы Godot `/en/stable/` — изменяемая документация, а не закреплённый patch SDK. Одна из performance how-to страниц предупреждала о неполном обновлении к 4.7; существенное ограничение MultiMesh дополнительно проверено по class reference. Поддержка перечисленного API в конкретном plugin build и экспортированных binaries ещё требует пилота.

## Локальные источники

| ID | Материалы | Что проверено |
| --- | --- | --- |
| L001 | [Архитектура](../../architecture.md), [план](../../plan.md), [граница сборок 023](../../decisions/023-core-and-gameplay.md), [Core project](../../../src/OpenRTS.Core/OpenRTS.Core.csproj) | Независимый Core, встроенный Gameplay, ограничение зависимостей и цели проекта |
| L002 | [GameMap](../../../src/OpenRTS.Content/Maps/GameMap.cs), [MapMatch](../../../src/OpenRTS.Content/Maps/MapMatch.cs), [TerrainLayout](../../../src/OpenRTS.Content/Maps/TerrainLayout.cs) | Поля файловой карты, кисти/visual, порядок размещений, создание и освобождение временного мира |
| L003 | [UnitCatalog](../../../src/OpenRTS.Content/Catalogs/UnitCatalog.cs), [EntityDefinition](../../../src/OpenRTS.Gameplay/Definitions/EntityDefinition.cs), [UnitDefinition](../../../src/OpenRTS.Gameplay/Definitions/UnitDefinition.cs) | Подготовка definitions, зависимости capabilities, immutable recipe и повороты |
| L004 | [SimulationWorld](../../../src/OpenRTS.Gameplay/Simulation/SimulationWorld.cs), [session](../../../src/OpenRTS.Core/Hosting/SimulationSession.cs), [RTS session](../../../src/OpenRTS.Gameplay/Hosting/SimulationSession.cs), [frames](../../../src/OpenRTS.Gameplay/Hosting/FrameExchange.cs) | Начальное размещение, команды, tick ownership и публикация |
| L005 | [Sandbox project](../../../sandbox/OpenRTS.Sandbox.csproj), [сцена](../../../sandbox/Main.tscn), [Sandbox](../../../sandbox/Sandbox.cs), [sprite view](../../../src/OpenRTS.Godot/Rendering/SpriteWorldView.cs), [batches](../../../src/OpenRTS.Godot/Rendering/SpriteBatch.cs), [terrain view](../../../src/OpenRTS.Godot/Rendering/TerrainView.cs) | Source inclusion, построение сцены и применение опубликованных данных |
| L006 | [NavigationGrid](../../../src/OpenRTS.Core/Navigation/NavigationGrid.cs), [численные типы](../../../src/OpenRTS.Core/SimulationTypes.cs), [решение 028](../../decisions/028-navigation-route-planner.md) | SurfaceSample/obstacles, размеры и граница сеточного pathfinder |
| L007 | [Выбор ECS](../ecs-selection/README.md), [probe report](../ecs-selection/probe/README.md), [завершение миграции](../../decisions/019-ecs-integration.md) | Предыдущее обоснование Frent; probe не запускался повторно |
| L008 | [Карты](../game-maps/README.md), [декорации](../large-map-decorations/README.md), [presentation](../../decisions/007-presentation.md) | Исторический контекст и различие игровых/визуальных данных; текущая реализация сверена отдельно |
| L009 | [CnC project](../../../../OpenRTS.CnC/game/OpenRTS.CnC.csproj), [CnC README](../../../../OpenRTS.CnC/README.md) | Внешний потребитель source-linked Godot adapter; иные runtime не тестировались |

## Внешние источники

### S001

[C# exported properties](https://docs.godotengine.org/en/stable/tutorials/scripting/c_sharp/c_sharp_exports.html): сохранение и Inspector через `[Export]`, Variant-compatible типы, группы и hints. Подтверждает средства оболочки, не автоматическую поддержку наших definitions.

### S002

[C# global classes](https://docs.godotengine.org/en/stable/tutorials/scripting/c_sharp/c_sharp_global_classes.html): `[GlobalClass]`, диалоги Node/Resource и ограничения имён файлов.

### S003

[Running code in the editor](https://docs.godotengine.org/en/stable/tutorials/plugins/running_code_in_the_editor.html): `[Tool]`, editor hint, preview и риски изменения сцены. Наличие API не заменяет lifecycle и undo нашего плагина.

### S004

[Resources](https://docs.godotengine.org/en/stable/tutorials/scripting/resources.html): data containers, сериализация, общие загруженные экземпляры и сцены. Не следует считать каждый Resource независимым runtime state размещения.

### S005

[EditorPlugin](https://docs.godotengine.org/en/stable/classes/class_editorplugin.html), [EditorInspectorPlugin](https://docs.godotengine.org/en/stable/classes/class_editorinspectorplugin.html), [EditorNode3DGizmoPlugin](https://docs.godotengine.org/en/stable/classes/class_editornode3dgizmoplugin.html): расширение UI, свойств и 3D viewport.

### S006

[EditorUndoRedoManager](https://docs.godotengine.org/en/stable/classes/class_editorundoredomanager.html), [Node.Owner](https://docs.godotengine.org/en/stable/classes/class_node.html#class-node-property-owner): история действий и попадание узлов в PackedScene; не игровой Owner.

### S007

[EditorImportPlugin](https://docs.godotengine.org/en/stable/classes/class_editorimportplugin.html), [ResourceFormatLoader](https://docs.godotengine.org/en/stable/classes/class_resourceformatloader.html), [ResourceFormatSaver](https://docs.godotengine.org/en/stable/classes/class_resourceformatsaver.html): разные обязанности импорта производного ресурса и чтения/сохранения формата. Сохранение YAML inheritance/comments не обещано.

### S008

[EditorExportPlugin](https://docs.godotengine.org/en/stable/classes/class_editorexportplugin.html): export lifecycle, resource/scene customization и configuration hash. Это не универсальный hook перед каждым локальным запуском сцены.

### S009

[Using TileMaps](https://docs.godotengine.org/en/stable/tutorials/2d/using_tilemaps.html), [Using TileSets](https://docs.godotengine.org/en/stable/tutorials/2d/using_tilesets.html): покраска, patterns, custom data, terrain connections. Подтверждены shared tile metadata и накладные расходы scene tiles. Godot terrain connections не определяют правила OpenRTS.

### S010

[Using GridMaps](https://docs.godotengine.org/en/stable/tutorials/3d/using_gridmaps.html): MeshLibrary и модульные меши; экспортируется ограниченный набор node types, это не общий placer произвольных сцен.

### S011

Terrain3D, `185145cedadc93251934aba6f60625050ef155ce`, 10 ноября 2026 года: [README](https://github.com/TokisanGames/Terrain3D/blob/185145cedadc93251934aba6f60625050ef155ce/README.md), [import/export](https://github.com/TokisanGames/Terrain3D/blob/185145cedadc93251934aba6f60625050ef155ce/doc/docs/import_export.md). Подтверждены GDExtension, sculpt/paint, height/control/color и собственный control format; README указывает MIT. Это development snapshot, не рекомендация конкретного release. Binary/renderer compatibility не проверена.

### S012

[Thread-safe APIs](https://docs.godotengine.org/en/stable/tutorials/performance/thread_safe_apis.html): ограничения active SceneTree, Resources и условия server API. Не доказывает детерминизм физических результатов.

### S013

[Navigation overview](https://docs.godotengine.org/en/stable/tutorials/navigation/navigation_introduction_3d.html), [Using NavigationServer](https://docs.godotengine.org/en/stable/tutorials/navigation/navigation_using_navigationservers.html): navmesh/graph/avoidance, NavigationObstacle и синхронизация изменений. Подмена нашего pathfinder — проектный вывод, не готовая возможность этих API.

### S014

[Optimization using Servers](https://docs.godotengine.org/en/stable/tutorials/performance/using_servers.html), [MultiMesh reference](https://docs.godotengine.org/en/stable/classes/class_multimesh.html): обход SceneTree, instancing и общая пространственная видимость набора. Собственные чанки и экономия на нашей карте ещё не измерены.

### S015

[GDExtension](https://docs.godotengine.org/en/stable/engine_details/engine_api/gdextension/what_is_gdextension.html): native shared-library boundary без пересборки Godot. Не обязательна для существующего C# host.

### S016

[Command line tutorial](https://docs.godotengine.org/en/stable/tutorials/editor/command_line_tutorial.html): headless и export/CLI. Godot headless не устраняет зависимость приложения от Godot.

### S017

[EditorDebuggerPlugin](https://docs.godotengine.org/en/stable/classes/class_editordebuggerplugin.html): editor sessions, сообщения и пользовательские вкладки. Frent inspection требует нашей проекции; встроенного подключения Frent источник не обещает.

### S018

GECS, `8d2d2b2ddc21b2825712e17419192cedca3d4dd3`, 18 сентября 2026: [README](https://github.com/csprance/gecs/blob/8d2d2b2ddc21b2825712e17419192cedca3d4dd3/README.md), [plugin version](https://github.com/csprance/gecs/blob/8d2d2b2ddc21b2825712e17419192cedca3d4dd3/addons/gecs/plugin.cfg), [Entity](https://github.com/csprance/gecs/blob/8d2d2b2ddc21b2825712e17419192cedca3d4dd3/addons/gecs/ecs/entity.gd), [Component](https://github.com/csprance/gecs/blob/8d2d2b2ddc21b2825712e17419192cedca3d4dd3/addons/gecs/ecs/component.gd), [LICENSE](https://github.com/csprance/gecs/blob/8d2d2b2ddc21b2825712e17419192cedca3d4dd3/LICENSE). `9.3.4`, требование `4.6+`, Nodes/Resources. README MIT расходится с CC0-1.0 в LICENSE. Заявления README о скорости/готовности не приняты как измерения OpenRTS.

### S019

Godex, `dafb7b0fc80248fdbe9ea8cfa474ac0a2f9daa73`, 18 сентября 2023: [README](https://github.com/GodotECS/godex/blob/dafb7b0fc80248fdbe9ea8cfa474ac0a2f9daa73/README.md), [SCsub](https://github.com/GodotECS/godex/blob/dafb7b0fc80248fdbe9ea8cfa474ac0a2f9daa73/SCsub), [LICENSE](https://github.com/GodotECS/godex/blob/dafb7b0fc80248fdbe9ea8cfa474ac0a2f9daa73/LICENSE), [setup](https://github.com/GodotECS/godex/wiki/Setup), [repository metadata](https://api.github.com/repos/GodotECS/godex). Сборка модуля и MIT подтверждены. Дата commit не равна дате закрытия проекта; repo не archived. Новые сторонние forks не исследованы.

### S020

Stagehand, `c5ecc3b254d30a828fb67d6eb55c07fd15e9dd98`, 21 сентября 2026: [README](https://github.com/60k41p/Stagehand/blob/c5ecc3b254d30a828fb67d6eb55c07fd15e9dd98/README.md), [composer](https://github.com/60k41p/Stagehand/blob/c5ecc3b254d30a828fb67d6eb55c07fd15e9dd98/editor_plugins/entity_composer/entity_composer.gd), [LICENSE](https://github.com/60k41p/Stagehand/blob/c5ecc3b254d30a828fb67d6eb55c07fd15e9dd98/LICENSE). Native Flecs/Godot bridge и ранняя стадия заявлены автором; GraphEdit прочитан. LICENSE — BSD-2-Clause с Commons Clause. Не выполнены полный license audit транзитивных dependencies и трасса compiler/round-trip всех prefab данных.

## Матрица ключевых выводов

| ID | Вывод | Основание | Характер и предел |
| --- | --- | --- | --- |
| C001 | Богатый Godot editor может быть опциональным относительно симуляции | L001–L005, S001–S008 | Архитектурный вывод; будущий compiler ещё не создан |
| C002 | Работа в редакторе не требует runtime Node на каждый юнит | L005, S005, S014 | Различие ролей и имеющийся batch renderer; стоимость editor preview неизвестна |
| C003 | Расстановка требует явного порядка и IDs | L002, L004, S006 | Факт поведения нынешнего world initialization; правила нового документа — предложение |
| C004 | Godot Resources нельзя просто сделать второй mutable копией definition/state | L003–L004, S001, S004 | Ограничения API и проектный вывод о владении |
| C005 | Более богатый terrain editor не добавляет автоматически gameplay surfaces | L002, L006, S010–S013 | Факт неполного файлового входа и различия контрактов |
| C006 | Navmesh Godot не drop-in реализация нынешнего сеточного пути | L006, S013 | Сопоставление контрактов; runtime performance не сравнивалась |
| C007 | GECS решает другую модель ECS integration | S018, L007 | Подтверждённое Node/Resource наследование; сравнительной скорости нет |
| C008 | Godex требует сопровождения engine module | S019 | Подтверждённый build path; 4.7-совместимость не установлена |
| C009 | Stagehand нельзя учитывать как обычную BSD/MIT-поставку | S020 | Факт наличия дополнительного условия в LICENSE; это не вывод о лицензии Flecs |
| C010 | Пилот B с YAML типами — разумная первая проверка | C001–C009 и цель задачи | Рекомендация; требование независимого authoring может сделать A предпочтительнее |

## Что осталось неизвестным

1. Какая реальная карта и рабочий процесс дадут наибольшую пользу: CnC-подобные тайлы, высотное поле или модульный 3D-level. Поэтому terrain backend не выбран.
2. Цена полного SceneTree preview на 10000 размещений, преимущества selective markers и чанков. Публичные demos других проектов этого не доказывают.
3. Цена round-trip YAML с наследованием/комментариями против `.tscn` compiler; особенности duplicate/reparent/subscene overrides нужно проверять в редакторе.
4. Детали packaging C# editor addon, reload/unload и export binaries на целевых desktop платформах. Новая поставка не создавалась.
5. Семантика initial HP/facing/variants/regions и их участие в hash/save: это новые контракты, а не имеющиеся поля файловой карты.
6. Поведение Godot physics/navigation в требуемом детерминированном runtime и цена адаптации. Никакая гарантия паритета с OpenRTS не заявляется.

Проверки и условия пересмотра выбора перечислены в [плане пилотов](experiments.md). В этой работе выполнены чтение и ручная трасса исходников, проверка primary sources и ссылок документов. Build/test/playtest, benchmarks, установка plugins и изменение рабочих проектов не выполнялись. Отчёт не является ADR и не меняет прежние архитектурные решения.
