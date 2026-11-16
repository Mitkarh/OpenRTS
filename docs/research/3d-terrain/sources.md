# Источники, версии и границы выводов

[Обзор](README.md) · [Контракты](contracts.md) · [План](plan.md).

## Метод и текущая версия

Проверены оба указанных чата через `read_thread`: [интеграция с Godot](thread://01a1d0c7-e391-74d1-bea6-abd0ea545fc8?hostId=local) и [компиляция карт](thread://01a1e0ef-d50b-7740-a4f6-336a230db96b?hostId=local). Их выводы сверены с документами и актуальными исходниками. Тексты чатов не используются как источник уже реализованных features.

Срез OpenRTS: `b6ffbf0551beeffaf2d2f30d6822e45ec982f86b`, 16 ноября 2026. До работы в дереве уже находилась незакоммиченная папка `docs/research/map-compilation/`; она прочитана как входное исследование и не изменялась этой работой. В проектном коде локальных изменений при начале проверки не было.

Статически прослежены: surface → locomotor/geometry → route/follower → position; ground/air → range/projectile collision; footprint → construction; карта → grid; camera ray → command point. Прочитаны существующие N2/aviation проверки. Они **не запускались заново**. Сборка, Godot prototype, игровые benchmark и визуальная проверка новой карты не выполнялись. Уверенность высокая для перечисленных текущих ограничений; предлагаемые алгоритмы и их стоимость остаются гипотезами.

## Локальная доказательная база

| ID | Проверенный код / документ и значимый участок | Что устанавливает |
| --- | --- | --- |
| S01 | [SimulationTypes](../../../src/OpenRTS.Core/SimulationTypes.cs), [SpatialState](../../../src/OpenRTS.Core/Simulation/SpatialState.cs), [ADR023](../../decisions/023-core-and-gameplay.md) | X/Y — земля, Elevation отдельно; fixed-point масштаб; ECS источник и границы сборок |
| S02 | [EntityPose](../../../src/OpenRTS.Gameplay/Presentation/EntityPose.cs), [SpriteWorldView](../../../src/OpenRTS.Godot/Rendering/SpriteWorldView.cs), `Interpolate`/`DisplayPosition` | Высота доходит до adapter; направления семантические и горизонтальные |
| S03 | [NavigationGrid](../../../src/OpenRTS.Core/Navigation/NavigationGrid.cs), `SurfaceSample`, `CellAt`, `Compose`, `WriteState` | Один surface sample на XY-cell, XY WorldBox, limits/revision; Compose меняет masks/terrain, не создаёт этажи |
| S04 | [Locomotor](../../../src/OpenRTS.Gameplay/Navigation/Locomotor.cs), [TerrainGeometry](../../../src/OpenRTS.Gameplay/Navigation/TerrainGeometry.cs), [Locomotion](../../../src/OpenRTS.Gameplay/Simulation/SimulationWorld.Locomotion.cs), [TerrainCost](../../../src/OpenRTS.Gameplay/Navigation/TerrainCost.cs), [FootprintChecks](../../../tests/OpenRTS.Tests/FootprintChecks.cs), тест `N2: locomotor slope, step and depth limits constrain geometry and elevation follows the surface` | Slope/depth из входа, перепад samples, ступенчатая GroundPosition; цена по горизонтальной длине и правила скорости |
| S05 | [TerrainRoutePlanner](../../../src/OpenRTS.Gameplay/Navigation/TerrainRoutePlanner.cs), [RouteGeometry](../../../src/OpenRTS.Gameplay/Navigation/RouteGeometry.cs), [Navigation execution](../../../src/OpenRTS.Gameplay/Simulation/SimulationWorld.Navigation.cs), [ADR028](../../decisions/028-navigation-route-planner.md) | Planner/executor разделены; exact решётка512 ограничена; область нынешней сменной регистрации |
| S06 | [BodyShape/BodyGeometry](../../../src/OpenRTS.Gameplay/Geometry/BodyShape.cs), [MotionIndex](../../../src/OpenRTS.Gameplay/Navigation/MotionIndex.cs), `ClearMove`/`ClearSegment` | Физика в XY, HalfHeight — горизонтальный размер; относительный sweep и ID tie-break |
| S07 | [SpatialProjectiles](../../../src/OpenRTS.Gameplay/Combat/SimulationWorld.SpatialProjectiles.cs), [Projectiles](../../../src/OpenRTS.Gameplay/Combat/SimulationWorld.Projectiles.cs), [Combat](../../../src/OpenRTS.Gameplay/Combat/SimulationWorld.Combat.cs), `InWeaponRange`/`Impact`, [ObjectGeometry](../../../src/OpenRTS.Gameplay/Geometry/ObjectGeometry.cs) | Реальные пространственные траектории уже есть; conditional air range, условные высоты преград/наземных целей, упрощённый splash |
| S08 | [ADR033](../../decisions/033-fog-of-war.md), [ADR034](../../decisions/034-sensor-observations.md), [FogOfWar](../../../src/OpenRTS.Gameplay/Visibility/FogOfWar.cs), [Sensors](../../../src/OpenRTS.Gameplay/Visibility/SimulationWorld.Sensors.cs), [Viewer frames](../../../src/OpenRTS.Gameplay/Visibility/FrameExchange.Visibility.cs) | F1/сигнатуры уже реализованы, но не terrain-aware LoS/несколько геометрических поверхностей |
| S09 | [Flight controller](../../../src/OpenRTS.Gameplay/Aviation/SimulationWorld.Flight.cs), `ValidateFlightSurface`, [ADR032](../../decisions/032-aviation-and-target-categories.md), [AviationChecks](../../../tests/OpenRTS.Tests/AviationChecks.cs), тест `unsupported variable-height flight surfaces reject before consuming identity` | Flat-only — действующий проверяемый контракт, а не отсутствие поля Z |
| S10 | [FootprintDefinition](../../../src/OpenRTS.Gameplay/Definitions/FootprintDefinition.cs), `AllowsConstruction`, [Construction operations](../../../src/OpenRTS.Gameplay/Simulation/SimulationWorld.ConstructionOperations.cs), `SiteTerrainAllows`, [Footprint composition](../../../src/OpenRTS.Gameplay/Simulation/SimulationWorld.Footprints.cs) | Проверка cell terrain/stop/obstacles, XY stamp; общей проверки плоскости основания/вертикального объёма нет |
| S11 | [GameMap](../../../src/OpenRTS.Content/Maps/GameMap.cs), [TerrainLayout](../../../src/OpenRTS.Content/Maps/TerrainLayout.cs), `CreateGrid`, [MapMatch](../../../src/OpenRTS.Content/Maps/MapMatch.cs), [TerrainView](../../../src/OpenRTS.Godot/Rendering/TerrainView.cs), [Sandbox](../../../sandbox/Sandbox.cs), `GroundPoint`/`MousePoint` | M1 и нынешний путь terrain не несут полный 3D input; плоские тайлы и picking plane |
| S12 | [README](../../../README.md), [Architecture](../../architecture.md), [Plan](../../plan.md), [N2 ADR012](../../decisions/012-buildings-and-geometry.md), [Navigation roadmap](../navigation-layers/roadmap.md) | Действующие инварианты, область 2.5D, измерения отдельно; исторические этапы не равны текущей полноте |
| S13 | [Godot integration](../godot-integration/README.md), [Authoring](../godot-integration/authoring.md), [Map compilation plan](../map-compilation/plan.md), [Other subsystems](../map-compilation/other-subsystems.md), [Fog research](../fog-visibility/README.md) | Рекомендуемый независимый authoring pipeline, MC0–MC9, F4; это проекты, не готовый compiler |

## Внешние первичные источники

Проверены 16 ноября 2026. Исходники Generals прочитаны через GitHub connector, Godot API — через актуальные официальные страницы. Изучались относящиеся к вопросу участки, не весь движок.

### E01 — CnC Generals / Zero Hour

Репозиторий `electronicarts/CnC_Generals_Zero_Hour`, HEAD на момент проверки `0a05454d8574207440a5fb15241b98ad0b435590` (commit 27 февраля 2025). Ветки исходной игры и Zero Hour различаются; здесь использован **GeneralsMD**.

- [TerrainLogic.h](https://github.com/electronicarts/CnC_Generals_Zero_Hour/blob/0a05454d8574207440a5fb15241b98ad0b435590/GeneralsMD/Code/GameEngine/Include/GameLogic/TerrainLogic.h#L162): Bridge хранит свой layer, TerrainLogic различает ground/layer height.
- [W3DTerrainLogic.cpp, строки280–354](https://github.com/electronicarts/CnC_Generals_Zero_Hour/blob/0a05454d8574207440a5fb15241b98ad0b435590/GeneralsMD/Code/GameEngineDevice/Source/W3DDevice/GameLogic/W3DTerrainLogic.cpp#L280): ground height приходит из heightmap; layer height учитывает wall/bridge и normal.
- [TerrainLogic.cpp, getLayerForDestination](https://github.com/electronicarts/CnC_Generals_Zero_Hour/blob/0a05454d8574207440a5fb15241b98ad0b435590/GeneralsMD/Code/GameEngine/Source/GameLogic/Map/TerrainLogic.cpp#L1692): различает опоры по положению, а не одной XY.
- [AIPathfind.h](https://github.com/electronicarts/CnC_Generals_Zero_Hour/blob/0a05454d8574207440a5fb15241b98ad0b435590/GeneralsMD/Code/GameEngine/Include/GameLogic/AIPathfind.h#L360): у клетки есть layer и connect layer; у узлов пути — layer.
- [AIPathfind.cpp, Path::optimize](https://github.com/electronicarts/CnC_Generals_Zero_Hour/blob/0a05454d8574207440a5fb15241b98ad0b435590/GeneralsMD/Code/GameEngine/Source/GameLogic/AI/AIPathfind.cpp#L475): сглаживание специально ограничено у смены layer. [addBridge/updateLayer](https://github.com/electronicarts/CnC_Generals_Zero_Hour/blob/0a05454d8574207440a5fb15241b98ad0b435590/GeneralsMD/Code/GameEngine/Source/GameLogic/AI/AIPathfind.cpp#L3971), [changeBridgeState](https://github.com/electronicarts/CnC_Generals_Zero_Hour/blob/0a05454d8574207440a5fb15241b98ad0b435590/GeneralsMD/Code/GameEngine/Source/GameLogic/AI/AIPathfind.cpp#L9686): bridge state меняет доступность и dirty zones.

**Применимость:** полезен принцип раздельных heightfield/bridge layers и сохранения переходов. Это не доказательство полноты поиска или масштабирования OpenRTS. В частности, выбор nearest-height из Generals не предлагается копировать как универсальный resolver нашей команды. Float-арифметика, конкретный enum/лимит мостов и legacy архитектура не переносятся. [Лицензия источника](https://github.com/electronicarts/CnC_Generals_Zero_Hour/blob/0a05454d8574207440a5fb15241b98ad0b435590/LICENSE.md): GPLv3 с дополнительными условиями; использован как исследовательский референс, код/ассеты не добавлялись в MIT проект.

### E02 — Godot HeightMapShape3D

[Описание](https://docs.godotengine.org/en/stable/classes/class_heightmapshape3d.html), секция Description: одна сетка высот с треугольниками, не описывает навесы/пещеры; holes и отдельные mesh collisions имеют собственную семантику. Поддерживает вывод о границах heightfield, не утверждает совместимость его physics с OpenRTS.

### E03 — Godot navigation

[Using navigation meshes](https://docs.godotengine.org/en/stable/tutorials/navigation/navigation_using_navigationmeshes.html): navmesh описывает поверхность для центра; размер через bake margin; rendering/physics не являются автоматически данными поиска. [NavigationObstacle3D](https://docs.godotengine.org/en/stable/classes/class_navigationobstacle3d.html): режимы bake/avoidance и их отдельные флаги. Поддерживают необходимость явного перевода и обновления, не доказывают, что navmesh непригоден для RTS.

### E04 — Godot editor, камера, пакеты

[EditorNode3DGizmoPlugin](https://docs.godotengine.org/en/stable/classes/class_editornode3dgizmoplugin.html), [Camera3D](https://docs.godotengine.org/en/stable/classes/class_camera3d.html), [MultiMeshes](https://docs.godotengine.org/en/stable/tutorials/performance/using_multimesh.html). Проверены возможности gizmos и проекции луча, ограничение индивидуального culling MultiMesh. Последняя страница помечена как не полностью обновлённая для Godot4.7. Ни страница, ни нынешние 10k спрайты не являются замером 10k skeletal models.

### E05 — Terrain3D

Проверенная ревизия `185145cedadc93251934aba6f60625050ef155ce`, commit 10 ноября 2026. [README](https://github.com/TokisanGames/Terrain3D/blob/185145cedadc93251934aba6f60625050ef155ce/README.md): GDExtension, C# доступ, sculpting/import, MIT. [Import/export](https://github.com/TokisanGames/Terrain3D/blob/185145cedadc93251934aba6f60625050ef155ce/doc/docs/import_export.md): height/control/color — разные данные, требуется согласование форматов и масштаба. [Installation](https://github.com/TokisanGames/Terrain3D/blob/185145cedadc93251934aba6f60625050ef155ce/doc/docs/installation.md): совместимость Godot и platforms/renderers зависит от версии. Это кандидат authoring, не протестированная зависимость OpenRTS.

## Ключевые выводы и пределы

| Вывод | Основание | Статус |
| --- | --- | --- |
| Не нужен полный rewrite симуляции ради 3D-карт | S01–S05, S12; изоляция состояния/представления | Архитектурный вывод; объём работ в плане остаётся оценкой |
| Есть высота, но нет сквозной готовности | S03–S11 | Факт по коду |
| Многослойность затрагивает команды, тела, бой, fog и стройку | S05–S11 + bridge scenario | Вывод из текущих XY-контрактов; proposed solution в contracts.md |
| Heightfield + sparse patches — разумный первый кандидат | E01/E02, S03–S06, границы продукта | Предложение; подтверждается/отвергается T1/T6 |
| Компиляция не добавит сама нужную игровую модель | S11/S13, E02/E03 | Вывод; поэтому сначала T0 и согласованный артефакт |
| Готовность на 10k 3D юнитах неизвестна | Нет нового benchmark, E04 — только API | Открытая измерительная задача T9 |

Не выполнен аудит полных исходников Generals, SC2/RA3 не исследованы как реализации; другие RTS не добавлялись ради количества источников. Не выбраны окончательный формат файла, точный terrain addon, интерполяция, high-ground policy, occupants outcome при разрушении моста и границы поддержанной авиации. План показывает момент выбора и сценарии, позволяющие сделать его проверяемым.
