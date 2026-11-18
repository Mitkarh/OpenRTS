# Источники и проверка выводов

[Обзор](README.md) · [Godot 2D](godot-native-2d.md) · [Граница симуляции](simulation-boundaries.md) · [Генерация карт](../map-generation/README.md) · [Слои карты](../layered-maps/README.md).

Проверка от 18 ноября 2026 года. Основа OpenRTS — `16ccdd2` (`Terrain Ramps`), включая изменения `NavigationGrid` и геометрии для `sandbox3d`. Использовались чтение исходников, решений, инструкций к сценариям и официальной документации Godot. Проекты указывают `Godot.NET.Sdk/4.7.2`. Страницы `stable` проверялись в ветке документации 4.7. Прочитаны существующие записи о проверках, новых игровых запусков и бенчмарков для этого исследования не проводилось.

## OpenRTS

### S001

Сцену B2–B5 сопровождают собственные маркеры и terrain resource. `Extract` собирает transform вдоль сцены, формирует ID вложенных групп, сортирует игровые размещения и возвращает `MapSource`. В B4 loader проверяет тип корня, ID карты, манифест мода и зависимости сцены. [MapSceneExtractor](../../../sandbox/addons/openrts_editor/MapSceneExtractor.cs#L19) · [Кисть B3](../../../sandbox/addons/openrts_editor/OpenRtsEditorPlugin.Terrain.cs#L183) · [Загрузка сцены](../../../sandbox/Sandbox.SceneMaps.cs#L46) · [B4](../../decisions/050-godot-scene-map-bundle.md) · [B5](../../decisions/051-map-initial-properties.md).

### S002

`MapSource` v1/v2 и `MapCompiler` поддерживают целочисленные плоские позиции. `WorldPosition` имеет Elevation. В `sandbox3d` сценарий создаёт отдельную `NavigationGrid` с высотами, а `TerrainSurface` строит меш и вычисляет попадание луча камеры. Это один сценарий и одна поверхность. [MapSource](../../../src/OpenRTS.Content/Maps/MapSource.cs#L34) · [MapCompiler](../../../src/OpenRTS.Content/Maps/MapCompiler.cs#L39) · [WorldPosition](../../../src/OpenRTS.Core/SimulationTypes.cs#L6) · [NavigationGrid](../../../src/OpenRTS.Core/Navigation/NavigationGrid.cs#L120) · [TerrainScenario](../../../sandbox3d/TerrainScenario.cs#L25) · [TerrainSurface](../../../sandbox3d/TerrainSurface.cs#L77) · [План 3D](../3d-terrain/plan.md).

### S003

Сессия владеет временем и исполнением, хост потребляет кадры и отдельные события. Network использует журналы с проверками хеша и умеет восстанавливать мир через snapshot либо исполнение префикса. В существующем плане B6 связывает compiled карту с сетевым запуском и выбором артефакта для повтора. [Core SimulationSession](../../../src/OpenRTS.Core/Hosting/SimulationSession.cs#L14) · [SandboxSession](../../../sandbox/Controls/SandboxSession.cs) · [FrameExchange](../../../src/OpenRTS.Gameplay/Hosting/FrameExchange.cs#L91) · [Network](../../../src/OpenRTS.Network/README.md) · [Network reuse](../godot-integration/network-reuse.md).

### S004

Текущий рендер использует `MultiMeshInstance3D`. Модель задаёт визуальные части и состояния, а тело и footprint читаются из gameplay. `GameplayMechanics.GridPathfinder` уже имеет узкий контракт подмены. Существующая проверка 10 000 поз относится к текущим буферам. [SpriteWorldView](../../../src/OpenRTS.Godot/Rendering/SpriteWorldView.cs#L62) · [SpriteBatch](../../../src/OpenRTS.Godot/Rendering/SpriteBatch.cs#L6) · [ObjectGeometry](../../../src/OpenRTS.Gameplay/Geometry/ObjectGeometry.cs) · [GameplayMechanics](../../../src/OpenRTS.Gameplay/Simulation/GameplayMechanics.cs#L35) · [Проверка B3](../../verification/049-godot-terrain-models-preview.md).

## Первичные внешние источники

### S005

**Godot — Using TileMaps, Using TileSets, TileMapLayer, TileSet.** Проверены повторно используемые шаблоны, Connect/Path, тайловые ID и custom data, преобразование координат, отложенные обновления и предел знаковых 16-битных индексов при сериализации. Документация описывает редакторский инструмент и формат Godot. Семантика грунта для OpenRTS проектируется отдельно. [Using TileMaps](https://docs.godotengine.org/en/stable/tutorials/2d/using_tilemaps.html) · [Using TileSets](https://docs.godotengine.org/en/stable/tutorials/2d/using_tilesets.html) · [TileMapLayer](https://docs.godotengine.org/en/stable/classes/class_tilemaplayer.html) · [TileSet](https://docs.godotengine.org/en/stable/classes/class_tileset.html).

### S006

**Godot — Node2D, Camera2D, Viewport and canvas transforms, EditorPlugin.** Проверены 2D transform, camera limits/smoothing, различие пространства canvas и viewport, редакторские callbacks для canvas overlay и ввода. [Node2D](https://docs.godotengine.org/en/4.7/classes/class_node2d.html) · [Camera2D](https://docs.godotengine.org/en/stable/classes/class_camera2d.html) · [Canvas transforms](https://docs.godotengine.org/en/4.7/tutorials/2d/2d_transforms.html) · [EditorPlugin](https://docs.godotengine.org/en/stable/classes/class_editorplugin.html).

### S007

**Godot — CanvasItem, MultiMeshInstance2D, Optimization using MultiMeshes.** Проверены порядок CanvasItem, сортировка дочерних элементов и 2D instancing. Статья об оптимизации, помеченная как требующая обновления для 4.7, приводит крупный MultiMesh и разбиение по областям как пример отсечения. Практическая эффективность для OpenRTS не установлена. [CanvasItem](https://docs.godotengine.org/en/stable/classes/class_canvasitem.html) · [MultiMeshInstance2D](https://docs.godotengine.org/en/stable/classes/class_multimeshinstance2d.html) · [MultiMesh optimization](https://docs.godotengine.org/en/stable/tutorials/performance/using_multimesh.html).

### S008

**Godot — PackedScene, AnimationPlayer.** Проверены упаковка дерева по `Node.owner`, инстанцирование и воспроизведение анимаций сцены. [PackedScene](https://docs.godotengine.org/en/4.7/classes/class_packedscene.html) · [AnimationPlayer](https://docs.godotengine.org/en/4.7/classes/class_animationplayer.html).

### S009

**Godot — 2D lights and shadows.** Описаны PointLight2D, DirectionalLight2D, CanvasModulate и 2D occluders. Они подтверждают наличие нативного оформления. Цена света на картах OpenRTS остаётся вопросом для профилирования. [2D lights and shadows](https://docs.godotengine.org/en/stable/tutorials/2d/2d_lights_and_shadows.html).

### S010

**Godot — NavigationServer2D, Using NavigationLayers, Using NavigationLinks, NavigationLink2D.** Server выполняет отложенные обновления навигации, маска фильтрует геометрию для расчёта маршрута, а ссылка задаёт участок маршрута с особым исполнением. Документация не описывает детерминированный lockstep для OpenRTS. [NavigationServer2D](https://docs.godotengine.org/en/stable/classes/class_navigationserver2d.html) · [NavigationLayers](https://docs.godotengine.org/en/stable/tutorials/navigation/navigation_using_navigationlayers.html) · [Using NavigationLinks](https://docs.godotengine.org/en/4.7/tutorials/navigation/navigation_using_navigationlinks.html) · [NavigationLink2D](https://docs.godotengine.org/en/stable/classes/class_navigationlink2d.html).

### S011

**OpenRA — ClassicMapGenerator.** Прочитан локальный checkout `9636b63eae`. `Parameters` включает seed, число игроков и симметрию. В ходе генерации создаются отдельные RNG для нескольких задач, в том числе топологии и декора. Исходник под GPL используется как референс. **Togelius и соавторы — Procedural Content Generation: Goals, Challenges and Actionable Steps.** Работа даёт контекст методов и задач PCG. Из неё нельзя вывести игровые критерии справедливости для OpenRTS. [Исходник OpenRA](https://raw.githubusercontent.com/OpenRA/OpenRA/bleed/OpenRA.Mods.Common/Traits/World/ClassicMapGenerator.cs) · [Параметры мода Red Alert](https://github.com/OpenRA/OpenRA/blob/bleed/mods/ra/rules/map-generators.yaml) · [Dagstuhl 2013](https://drops.dagstuhl.de/entities/document/10.4230/DFU.Vol6.12191.61).

### S012

**Recast Navigation — dtNavMeshCreateParams.** Off-mesh connection задаёт ребро между точками графа. Источник иллюстрирует навигационное представление. Правила боя, доступа, ожидания и восстановления в ходе перехода определяет конкретная симуляция. [Detour reference](https://recastnav.com/structdtNavMeshCreateParams.html).

### S013

**Godot 4.7 — изометрическая сетка и Isometric Game Demo.** `TileSet` задаёт изометрическую форму, layout и ось смещения. `TileMapLayer.map_to_local` возвращает центр клетки без учёта графического смещения тайла. Официальное демо показывает отдельные спрайты и высотные смещения в сцене. Проверка совместимости layout, непрерывных координат и пакетной сортировки OpenRTS отнесена к пилоту. [TileSet](https://docs.godotengine.org/en/4.7/classes/class_tileset.html) · [TileMapLayer](https://docs.godotengine.org/en/4.7/classes/class_tilemaplayer.html) · [Демо Godot](https://github.com/godotengine/godot-demo-projects/blob/master/2d/isometric/README.md).

## От чего зависит пересмотр выводов

| Вывод | Основание | Что может изменить решение |
| --- | --- | --- |
| C001. Нативный 2D даёт более подходящие инструменты для тайловой карты | [S001](#s001), [S005](#s005), [S006](#s006) | Затраты на атлас и извлечение семантики превысят экономию на повторных правках |
| C002. Canvas runtime заслуживает отдельного сравнения | [S004](#s004), [S007](#s007), [S009](#s009) | Сортировка, окклюзия, lighting или batching обойдутся слишком дорого по качеству или времени кадра |
| C003. Общий игровой контракт для Godot-хостов уже имеет опорные точки | [S001](#s001), [S002](#s002), [S003](#s003) | C выявит несовместимые требования к жизненному циклу или наблюдению в конкретной игре |
| C004. Генерацию сцены можно начать на плоском M1 | [S001](#s001), [S002](#s002), [S008](#s008) | Правила авторского оформления потребуют дополнительного входа или явной передачи владения сцене |
| C005. Генерация игровой карты требует валидатора режима | [S002](#s002), [S011](#s011) | Пороговые метрики доступности и баланса зависят от стартовых наборов, локомоторов и экономики |
| C006. Граф переходов годится как часть модели слоёв | [S002](#s002), [S010](#s010), [S012](#s012) | Непрерывные взаимодействия через границу или несопоставимые масштабы потребуют другого пространства либо более богатой геометрии |
