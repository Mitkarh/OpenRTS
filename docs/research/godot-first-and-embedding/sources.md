# Источники и границы доказательств

**Проверено 18 ноября 2026 года.** Сопоставлены проекты сборки и точки входа, ручная трасса карты/матча, существующие отчёты проверок, документация Godot 4.7 и отдельные участки исходников тега 4.7.2-stable. Внешний поиск охватывал scene composition, C# packaging, экспорт, серверы, навигацию, физику и multiplayer. Документация Godot описывает контракты API и ограничения. Нагрузку, результирующий код и трудоёмкость миграции она не измеряет. Новых runtime-прогонов нет.

## Репозитории и опубликованные проверки

### S001

**OpenRTS. Граница сборок и host.** Ревизия `4887a49fe19bac3dd332c80a3ff83e32a4ef32e3`, рабочее дерево на старте чистое. Прочитаны `Compile`/`ProjectReference`/framework, `SimulationSession.Run`, `Submit`, `SetPlayback`, `Dispose`, `FrameExchange.TryPublish`/`AcquireLatest`, `Sandbox._Process`/`_ExitTree`, ввод и ресурсный loader. [Sandbox project](../../../sandbox/OpenRTS.Sandbox.csproj), [Headless project](../../../tools/OpenRTS.Headless/OpenRTS.Headless.csproj), [Core session](../../../src/OpenRTS.Core/Hosting/SimulationSession.cs), [Gameplay frames](../../../src/OpenRTS.Gameplay/Hosting/FrameExchange.cs), [Sandbox](../../../sandbox/Sandbox.cs), [решение 023](../../decisions/023-core-and-gameplay.md).

### S002

**OpenRTS. Scene-map и компилятор.** Чтение контрактов `MapSceneExtractor.Extract`, `MapCompiler.Compile/Read`, `MapMatch.CreateWorld`, stage B4 и отчётов 047–049. Отчёты описывают ранее выполненные проверки. К концу исследования также прочитаны появившиеся изменения B4 в рабочем дереве: `SceneMapBundle.LoadMap`, `Sandbox.InstantiateMapScene`, проверочный сценарий и export preset. [Проект B](../godot-integration/stage-b-design.md), [компилятор](../../../src/OpenRTS.Content/Maps/MapCompiler.cs), [MapMatch](../../../src/OpenRTS.Content/Maps/MapMatch.cs), [проверки 047](../../verification/047-map-compilation.md), [048](../../verification/048-godot-map-authoring.md), [049](../../verification/049-godot-terrain-models-preview.md), [loader B4 в рабочем дереве](../../../sandbox/Sandbox.SceneMaps.cs).

### S003

**OpenRTS. Правила, пакетный рендер и сеть.** Документация версионированных решений и публичные APIs `StarSession`/`ReplaySession`, в том числе `Completion`, `Dispose` и `StarSession.Pause`. Буфер `SpriteBatch` подтверждает использование MultiMesh. [Навигация](../../decisions/009-terrain-locomotors.md), [геометрия](../../decisions/012-buildings-and-geometry.md), [SpriteBatch](../../../src/OpenRTS.Godot/Rendering/SpriteBatch.cs), [Network README](../../../src/OpenRTS.Network/README.md), [StarSession](../../../src/OpenRTS.Network/StarSession.cs), [ReplaySession](../../../src/OpenRTS.Network/ReplaySession.cs), [rejoin](../../decisions/043-net4-runtime-snapshot-rejoin.md).

### S004

**OpenRTS.CnC. Независимый потребитель.** Локальный checkout `6076917`, проверены tracked `game/OpenRTS.CnC.csproj`, `game/project.godot` и README. В дереве присутствовал untracked `art/`, который в анализ не входил. `OpenRTSRoot`, ссылка на Content и `Compile Link` к Controls/Rendering подтверждают source-based reuse. Отдельную работоспособность экспортов и scene-level isolation по этим файлам подтвердить нельзя. [Проект потребителя](../../../../OpenRTS.CnC/game/OpenRTS.CnC.csproj), [настройки Godot](../../../../OpenRTS.CnC/game/project.godot), [описание запуска](../../../../OpenRTS.CnC/README.md).

## Первичные источники Godot

### S005

**Godot Engine. Godot 4.7.2 и C# basics.** Релиз 18 августа 2026 года, Godot commit `ed1daf0bf`. Проверены .NET поставка, требования SDK, ограничения Web export и cross-language методы, свойства и сигналы. [Релиз 4.7.2](https://godotengine.org/article/maintenance-release-godot-4-7-2/), [C# basics](https://docs.godotengine.org/en/4.7/tutorials/scripting/c_sharp/c_sharp_basics.html), [Cross-language scripting](https://docs.godotengine.org/en/4.7/tutorials/scripting/cross_language_scripting.html).

### S006

**Godot Engine. PackedScene, Resource, Scene organization.** Прочитаны описание instantiate, сцепление со средой и свойство `resource_local_to_scene`. В исходнике `PackedScene::instantiate` восстанавливает дерево из SceneState и возвращает созданный корень. Исходник также показывает ограничение редакторского режима вне tools build. [Код PackedScene 4.7.2](https://github.com/godotengine/godot/blob/4.7.2-stable/scene/resources/packed_scene.cpp#L2335). [PackedScene](https://docs.godotengine.org/en/4.7/classes/class_packedscene.html), [Resource](https://docs.godotengine.org/en/4.7/classes/class_resource.html#class-resource-property-resource-local-to-scene), [Scene organization](https://docs.godotengine.org/en/4.7/tutorials/best_practices/scene_organization.html).

### S007

**Godot Engine. C# exports, AnimationTree, Installing plugins.** Проверены типы exported properties, использование AnimationTree для управления анимациями, отличие editor plugin от runtime add-on. [C# exported properties](https://docs.godotengine.org/en/4.7/tutorials/scripting/c_sharp/c_sharp_exports.html), [AnimationTree](https://docs.godotengine.org/en/4.7/tutorials/animation/animation_tree.html), [Installing plugins](https://docs.godotengine.org/en/4.7/tutorials/plugins/editor/installing_plugins.html).

### S008

**Godot Engine. InputEvent и viewport.** Проверены порядок обработки ввода с GUI, передача ввода в подоконные viewport и свойства мира. [Using InputEvent](https://docs.godotengine.org/en/4.7/tutorials/inputs/inputevent.html), [SubViewportContainer](https://docs.godotengine.org/en/4.7/classes/class_subviewportcontainer.html), [Viewport](https://docs.godotengine.org/en/4.7/classes/class_viewport.html).

### S009

**Godot Engine. Process modes and pause.** В исходнике `SceneTree::set_pause` проверяет главный поток, переключает активность physics servers и распространяет pause notification через root. [Код SceneTree 4.7.2](https://github.com/godotengine/godot/blob/4.7.2-stable/scene/main/scene_tree.cpp#L1049). Документ описывает планировщик Godot. Поведение worker .NET следует из S001. [Pausing games and process mode](https://docs.godotengine.org/en/4.7/tutorials/scripting/pausing_games.html).

### S010

**Godot Engine. Physics ticks, interpolation, raycasting.** Проверены разделение physics/render ticks и работа с physics space из скрипта. Эти документы задают модель вызова, без гарантии равенства RTS state hash на разных платформах. [Physics interpolation](https://docs.godotengine.org/en/4.7/tutorials/physics/interpolation/physics_interpolation_introduction.html), [Ray-casting](https://docs.godotengine.org/en/4.7/tutorials/physics/ray-casting.html).

### S011

**Godot Engine. Навигация.** Проверены синхронизация maps, deprecated `map_force_update`, avoidance, размеры агентов, bake препятствий и веса/ограничение jumping в AStarGrid2D. Реализация `get_id_path` запускает поиск с заданным началом и концом, затем собирает цепочку точек через predecessor links. [Код AStarGrid2D 4.7.2](https://github.com/godotengine/godot/blob/4.7.2-stable/core/math/a_star_grid_2d.cpp#L637). [NavigationServer2D](https://docs.godotengine.org/en/4.7/classes/class_navigationserver2d.html), [NavigationAgent2D](https://docs.godotengine.org/en/4.7/classes/class_navigationagent2d.html), [Different actor types](https://docs.godotengine.org/en/4.7/tutorials/navigation/navigation_different_actor_types.html), [NavigationObstacles](https://docs.godotengine.org/en/4.7/tutorials/navigation/navigation_using_navigationobstacles.html), [AStarGrid2D](https://docs.godotengine.org/en/4.7/classes/class_astargrid2d.html).

### S012

**Godot Engine. Threads, Servers, MultiMesh, C# collections.** Проверены ограничения активного дерева, оговорки по thread-model, стоимость маршалинга коллекций, RIDs и пакетное рисование. [Thread-safe APIs](https://docs.godotengine.org/en/4.7/tutorials/performance/thread_safe_apis.html), [Optimization using Servers](https://docs.godotengine.org/en/4.7/tutorials/performance/using_servers.html), [MultiMesh](https://docs.godotengine.org/en/4.7/classes/class_multimesh.html), [C# collections](https://docs.godotengine.org/en/4.7/tutorials/scripting/c_sharp/c_sharp_collections.html).

### S013

**Godot Engine. High-level multiplayer.** Проверены peers/RPC, привязка `MultiplayerAPI` к ветке и привязка RPC к NodePath. Эквивалентность протоколу OpenRTS устанавливается только сравнением с S003. [High-level multiplayer](https://docs.godotengine.org/en/4.7/tutorials/networking/high_level_multiplayer.html).

### S014

**Godot Engine. Dedicated server и лицензии.** Проверены `--headless`, dedicated server presets, Strip Visuals/Remove и Godot MIT attribution. Данные о цене запуска, RAM и размере конкретного экспорта OpenRTS отсутствуют. При поставке применяется лицензирование Godot и зависимостей игры и отдельно учитываются лицензии арта и сторонних addons. [Exporting for dedicated servers](https://docs.godotengine.org/en/4.7/tutorials/export/exporting_for_dedicated_servers.html), [Complying with licenses](https://docs.godotengine.org/en/4.7/about/complying_with_licenses.html).

### S015

**Godot Engine. Экспорт и паки.** Проверены экспорт по зависимостям, фильтры для файлов данных, чтение упакованных данных и отдельная загрузка DLL C# проекта в описанном PCK workflow. [Exporting projects](https://docs.godotengine.org/en/4.7/tutorials/export/exporting_projects.html), [Exporting packs, patches, and mods](https://docs.godotengine.org/en/4.7/tutorials/export/exporting_pcks.html), [Runtime file loading and saving](https://docs.godotengine.org/en/4.7/tutorials/io/runtime_file_loading_and_saving.html).

## Существенные выводы и уровень подтверждения

| ID | Вывод | Опора и граница |
| --- | --- | --- |
| C001 | Можно поддерживать один графический продукт на Godot и сохранять .NET расчёт. | S001/S002, разделение уже существует. Влияние на скорость работы команды — гипотеза. |
| C002 | Полноценная сцена как первичный источник оформления совместима с проверяемой игровой проекцией. | S002/S006, механизмы B0–B3 проверены ранее, реализация B4 появилась в рабочем дереве. Результат сквозного прогона B4 здесь не оценён. |
| C003 | Сетевые и массовые игровые гарантии требуют отдельной проверки при замене правил на Godot. | S003 и S010–S013 описывают разные контракты, в том числе отложенную синхронизацию, семантику навигации и игру по журналу. Сравнительная производительность и возможность отдельной эквивалентной замены остаются открыты. |
| C004 | Переиспользование между .NET проектами Godot уже применяется. Подключаемая сцена возможна на уровне платформы. | S004 показывает сборку из исходников, S005–S008 — строительные блоки. Контракт сторонней игры, чистый экспорт и изоляция экземпляров требуют пилота. |
| C005 | Скрытие/пауза дерева и состояние RTS session независимы. | S001/S009 и ручная трасса `AwaitTick`/`_Process`. Время завершения и накладные расходы жизненного цикла измеряются отдельно. |
| C006 | Godot headless и существующий .NET headless жизнеспособны для разных задач. | S001/S014. Первый позволяет исполнять Godot-зависимый код без дисплея. Второй уже покрывает batch-сценарии. Решение о едином серверном runtime требует сравнения бюджета и эксплуатации. |
