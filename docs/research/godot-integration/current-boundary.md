# Текущая граница OpenRTS–Godot

[Обзор исследования](README.md). **Основа:** OpenRTS `30195380a42424014f10bd5a413517d7b43a0738`, чистое дерево при начале чтения; статическое исследование 12 ноября 2026 года. Появившиеся позднее параллельные изменения учтены в [ограничениях снимка](sources.md). Ниже — фактическое поведение изученной границы. Предлагаемые изменения явно помечены.

**Обновление 17 ноября:** это исторический снимок. Для текущего проекта B map/content граница перепроверена на `afb7aef5a46bb937b39c8b63269e935c2a5b91da`; реализованные после исходного исследования Network, replay и runtime snapshots разобраны в [отдельной записи](network-reuse.md). Сценарий запуска и текущие изменения описаны в [проекте B](stage-b-design.md).

## Зависимости и владение

`OpenRTS.Core` зависит от .NET и закреплённого Frent `0.9.1-beta`. Проект содержит build guard, запрещающий другие project/reference/package dependencies. Gameplay строит RTS-правила поверх Core, Content загружает определения и карты для Gameplay. Godot сюда не входит. [Core project](../../../src/OpenRTS.Core/OpenRTS.Core.csproj), [решение 023](../../decisions/023-core-and-gameplay.md).

`src/OpenRTS.Godot` сейчас не отдельный `.csproj`: его исходники включены в sandbox через `Compile Include`. Sandbox использует `Godot.NET.Sdk/4.7.2`, `net10.0` и ссылку на Content. Главная сцена содержит один `Node3D` со скриптом `Sandbox`; игровая сцена в основном собирается программно. В просмотренном адаптере и sandbox не найден pipeline авторских `[Tool]`/`[Export]`-объектов карт. [Sandbox project](../../../sandbox/OpenRTS.Sandbox.csproj), [Main.tscn](../../../sandbox/Main.tscn), [Sandbox](../../../sandbox/Sandbox.cs).

Это уже **опциональность runtime**, но ещё не готовый переносимый SDK редактора. Внешний OpenRTS.CnC также компилирует выбранные `Controls` и `Rendering` исходники OpenRTS.Godot, а не получает отдельный пакет. Проверен его чистый checkout `6076917e5b85162ba9eed5040723079374f6e3d8`. Этот потребитель подтверждает полезность границы поставки; другая среда исполнения здесь не проверялась. [CnC project](../../../../OpenRTS.CnC/game/OpenRTS.CnC.csproj).

## Трасса 1: файл карты → начальный мир

**Назначение:** один игровой вход для графического и headless хоста. **Метод:** ручная трасса исходников, без запуска.

1. `GameMap.Load` получает `ContentSource`, `UnitCatalog` и необязательные источники кистей. Проверяет поля, версию, terrain/type/owner references, размеры и уникальность object IDs. В текущем файловом формате `cellSize` строго равен 1024, хотя сам `NavigationGrid` поддерживает другой размер samples.
2. Базовые строки terrain разворачиваются в массив. `TerrainLayout` хранит отдельно terrain IDs и visual IDs. Кисти имеют разреженные клетки с независимыми nullable `Terrain`/`Visual`; размещения применяются по порядку с поворотами на четверть оборота. Кисть должна целиком помещаться в карту. `CreateGrid` формирует игровую сетку, а `VisualAt` обеспечивает независимую картинку.
3. Для `skirmish` карта содержит нейтральные объекты и точки старта; выбранный стартовый набор и назначения игроков передаются отдельно. `fixture` использует именованные слоты владельцев.
4. `GameMap.Prepare` проверяет назначения и строит список размещений. Сначала идут занятые слоты в ordinal-порядке, внутри них записи набора; затем объекты карты в исходном порядке. Этот порядок намеренно сохраняет ID штатного журнала skirmish.
5. `MapMatch.CreateWorld` создаёт временный `SimulationWorld` с теми же definitions и сеткой, затем последовательно вызывает `AddInitialMapUnit`. Размещение проверяет игровые ограничения; ошибка получает label/source контекст. При любом отказе временный мир освобождается, наружу не возвращается частично собранное состояние.

Источники: [GameMap](../../../src/OpenRTS.Content/Maps/GameMap.cs), [TerrainLayout](../../../src/OpenRTS.Content/Maps/TerrainLayout.cs), [MapMatch](../../../src/OpenRTS.Content/Maps/MapMatch.cs), [SimulationWorld.AddInitialMapUnit](../../../src/OpenRTS.Gameplay/Simulation/SimulationWorld.cs), [MapScenario](../../../sandbox/Scenarios/MapScenario.cs).

**Следствие для редактора.** Нельзя создавать игровые сущности из каждого `_Ready`: тогда порядок SceneTree становится порядком выдачи ID и композиции footprint. Нужно сначала собрать полный документ, сохранить определённый порядок размещений и только затем строить мир. Перестановка узла ради организации дерева не должна незаметно менять игровые результаты. Если новый формат задаст иной порядок, это явное изменение семантики и журналов.

Идентификатор объекта файла сейчас используется для валидации и label ошибки; `MapMatch` не публикует полноценное соответствие authoring ID → runtime EntityId. Оно понадобится для выбора объекта из диагностики и live inspection. Не следует подменять его NodePath, Godot instance ID или внутренним handle Frent.

## Трасса 2: мир → изображение и ввод

`Sandbox.StartSession` передаёт фабрику мира `SimulationSession`. Сессия владеет потоком и публикацией. В `_Process` Godot берёт `Frames.AcquireLatest`, использует данные внутри `using`, рассчитывает интерполяцию и обновляет представление. Команды идут через `Submit`/`Issue`; мир не выдаёт узлам mutable ECS refs. [Sandbox](../../../sandbox/Sandbox.cs), [общая сессия](../../../src/OpenRTS.Core/Hosting/SimulationSession.cs), [RTS session](../../../src/OpenRTS.Gameplay/Hosting/SimulationSession.cs), [FrameExchange](../../../src/OpenRTS.Gameplay/Hosting/FrameExchange.cs).

`SpriteWorldView` связывает semantic playback с `ModelCatalog` и Godot `SpriteFrames`. `SpriteBatch` формирует буфер `MultiMeshInstance3D`, а не отдельный узел на юнита. Terrain также строится пакетами по visual ID. Это полезная основа редакторского preview, но готовой интеграцией со стандартными редакторскими selection/gizmo она пока не является. [SpriteWorldView](../../../src/OpenRTS.Godot/Rendering/SpriteWorldView.cs), [SpriteBatch](../../../src/OpenRTS.Godot/Rendering/SpriteBatch.cs), [TerrainView](../../../src/OpenRTS.Godot/Rendering/TerrainView.cs).

**Следствие:** авторский узел и runtime view — разные роли. После компиляции карты авторские markers не обязаны оставаться в активной игре. Для специальных представлений можно добавить соответствие EntityId → view, сохранив массовый путь пакетным.

## Что значит «управлять характеристиками» сейчас

| Уровень | Фактическая модель | Что потребуется редактору |
| --- | --- | --- |
| Определение типа | `UnitCatalog` компилирует YAML; immutable `EntityDefinition`, spatial view `UnitDefinition`, проверки зависимостей capabilities | Оболочка редактируемого источника и повторная компиляция; не прямая запись в definition |
| Расстановка | `objects` допускает только `id`, `type`, `position`, `owner` | Этого хватает для первого editor placement; facing/variant/initial HP здесь ещё не заданы |
| Runtime | Здоровье, приказы, движение, бой и прочее принадлежат ECS/системам | Чтение через опубликованную диагностику; изменение через разрешённую команду |
| Представление | Model/resources и UI bindings отделены от gameplay definition | Можно редактировать внешний вид без изменения боевого баланса; физическое тело отдельно |

Источники: [UnitCatalog](../../../src/OpenRTS.Content/Catalogs/UnitCatalog.cs), [EntityDefinition](../../../src/OpenRTS.Gameplay/Definitions/EntityDefinition.cs), [UnitDefinition](../../../src/OpenRTS.Gameplay/Definitions/UnitDefinition.cs), [GameMap object fields](../../../src/OpenRTS.Content/Maps/GameMap.cs), [presentation decision](../../decisions/007-presentation.md).

Внутри Gameplay уже есть повороты `UnitDefinition.WithRotation` и соответствующая runtime-семантика. Это не означает, что файловая карта принимает поворот каждого исходного размещения. Нельзя обещать Inspector поля HP/facing/weapon overrides без изменения и проверки соответствующего входного контракта.

## Рельеф и координаты

`WorldPosition` использует целые `X`, `Y`, `Elevation`, масштаб 1024. Текущий Godot view проецирует их в `Vector3(X, Elevation, Y) / 1024`. Сетка terrain и точные позиции объектов — разные данные: snap к сетке допустим как выбранный инструмент автора, а не обязательное округление всех юнитов. [SimulationTypes](../../../src/OpenRTS.Core/SimulationTypes.cs), [SpriteWorldView.Interpolate](../../../src/OpenRTS.Godot/Rendering/SpriteWorldView.cs).

`NavigationGrid` поддерживает `SurfaceSample(Elevation, Slope, Depth)` и world-space axis-aligned obstacles. Однако текущий `GameMap.Load` через `TerrainLayout.CreateGrid` создаёт terrain-сетку без файловых surface/obstacle слоёв. Файловая карта также не задаёт регионы, triggers или сценарные связи. Доступность этих типов в Core не означает готовность editor pipeline сложного рельефа. [NavigationGrid](../../../src/OpenRTS.Core/Navigation/NavigationGrid.cs), [TerrainLayout](../../../src/OpenRTS.Content/Maps/TerrainLayout.cs).

Текущий sprite view берёт `_flightSurface` из первого surface sample. Он не доказывает корректное изображение произвольного холмистого поля или многоуровневого здания. Переход к рельефной карте потребует отдельной проверки высоты отображения, выбора объектов и путей; наличие поля Elevation само по себе недостаточно.

## Пробел на границе подготовки

`GameMap` — результат загрузки, а не полноценный редактируемый документ. Конструктор закрыт, размещения приватны, наружу доступны позиции, но не полный публичный список объектов для правки; исходные размещения кистей при чтении разворачиваются в итоговый layout. В просмотренных map-классах нет симметричного writer и сохранения авторской истории слоёв.

**Предложение:** редактору нужен явный авторский документ либо собственная сцена с компилятором. Минимальный независимый пилот может писать существующий M1 и проверять его существующим загрузчиком. Для rich authoring потребуется общий этап подготовки из типизированных входных данных, чтобы YAML и Godot не дублировали правила. Появление такого потребителя обосновывает контракт; универсальный serializer всего ECS заранее не нужен.

Историческое [исследование карт](../game-maps/README.md) содержит исходные предложения и статус M1; [исследование декораций](../large-map-decorations/README.md) обсуждает разделение terrain/visual. Здесь текущий код проверен повторно: независимые visual IDs и кисти уже реализованы, считать их отсутствующими по старому тексту нельзя.
