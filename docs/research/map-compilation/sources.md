# Источники, доказательства и ограничения

[Обзор](README.md). Дата подготовки пакета: **16 ноября 2026 года**. Материал объединяет исследование навигации из обсуждения 15 ноября и дополнительное исследование подготовки ИИ/подсистем. Метод — целевое чтение исходников и первичных публикаций, не систематический обзор всех RTS.

## Локальная основа

### S001

**OpenRTS:** `b6ffbf0551beeffaf2d2f30d6822e45ec982f86b`; рабочее дерево было чистым при начале этого дополнения. Предыдущая часть обсуждения читала `9d7dafd239ca4bc9737ce4f62dc61ad7439beccf` с локальными изменениями; существенное навигационное поведение перепроверено на новой основе.

Изучены:

- [GameMap](../../../src/OpenRTS.Content/Maps/GameMap.cs): `Load` и `Prepare`, схема terrain/brushes, slots, исходные IDs и порядок placements.
- [TerrainLayout](../../../src/OpenRTS.Content/Maps/TerrainLayout.cs): независимые terrain/visual и `CreateGrid`.
- [MapMatch](../../../src/OpenRTS.Content/Maps/MapMatch.cs): `CreateWorld`, строки 25–57; обычное создание размещений, диагностика и Dispose при ошибке.
- [RouteGeometry](../../../src/OpenRTS.Gameplay/Navigation/RouteGeometry.cs): `Prepare`, строка 37; ленивые допустимость узлов/рёбер точного графа.
- [DestinationResolver](../../../src/OpenRTS.Gameplay/Navigation/DestinationResolver.cs): `Component`, строка 23; первый запрос размечает граф; `CanReach` сохраняет прямую проверку.
- [WeightedNavigator](../../../src/OpenRTS.Gameplay/Navigation/WeightedNavigator.cs): `SetGrid` 49, `Prepare` 73, `Build` 141, `WriteState` 167. Поля по профилю/цели, полный сброс, FIFO и метаданные в записываемом состоянии. Таймер начинается после подготовки geometry, около строки 96.
- [TerrainRoutePlanner](../../../src/OpenRTS.Gameplay/Navigation/TerrainRoutePlanner.cs): выбор режима, `WeightedGroupRoute` 146, `WriteState` 177; разделяемые деревья и включение runtime-состояния навигации.
- [SimulationWorld.Locomotion](../../../src/OpenRTS.Gameplay/Simulation/SimulationWorld.Locomotion.cs): `PrepareWeightedRoutes` 36; прямой путь, проверка связности, группа/личные поля.
- [SimulationWorld.Footprints](../../../src/OpenRTS.Gameplay/Simulation/SimulationWorld.Footprints.cs): `ComposeFootprints` 31 и `CommitGrid` 89; композиция по ID и инвалидация агентов.
- [Locomotor](../../../src/OpenRTS.Gameplay/Navigation/Locomotor.cs): независимые Speed/Cost, тело и ограничения поверхности. Текущий `NavigationKey` не следует автоматически считать полным внешним ключом постоянного файла: он используется внутри уже согласованных definitions.

Номера строк — ориентиры этой ревизии, не обещание неизменности ссылок после будущих изменений. Текущие данные не содержат готового постоянного кэша карты, секторного стратегического атласа или реализованных этапов MC1–MC9.

### S002

Исторические [проверки N1](../../verification/010-terrain-locomotors.md) и [N2](../../verification/013-buildings-and-geometry.md), особенно раздел N2 «Измерение»: один запуск на Apple M3 Max/.NET 10 Release, 10000 объектов в независимых рядах, изменение здания на тике 20. Для 1 worker указаны первый тик 82,30 мс, тик изменения 280,52 мс, остальные P95 30,27 мс. В N1 другой сценарий дал первый массовый тик 217,6 мс. Это разные исторические нагрузки и не текущая оценка всех боёв.

Используются как свидетельство существовавших пиков и необходимости раздельных замеров. Новых результатов из этих чисел не выводим; поиск не считается единственным источником пика.

### S003

[Снабжение](../supply-economy/README.md): конечный запас Mine, груз, доставка, одноканальное обслуживание Mine/Station, оборудование и neutralization; истощаемые/исчезающие источники и возможность добавления источников игровым правилом. Основа для различения статических ресурсных точек и текущей ценности.

Документ и ссылки на реализацию прочитаны; новые supply-прогоны не выполнялись. Ни текущая автоматика транспорта, ни её throughput не заменены предложенными признаками маршрута.

### S004

Текущий ИИ и предшествующий план:

- [BaseConstructionPlanner](../../../src/OpenRTS.AI/BaseConstructionPlanner.cs): `SearchBudget = 256` (13), `FindSite` (126), terrain/occupancy/access/range checks (149–154), `SiteOccupancy` (187).
- [BuildingSiteSearch](../../../src/OpenRTS.AI/BuildingSiteSearch.cs): `IBuildingSiteSearch` и кольца `CompactBuildingSiteSearch`, четыре поворота.
- [SkirmishAi](../../../src/OpenRTS.AI/SkirmishAi.cs): `Plan` (55), копирование и фильтрация poses (64–65), `PlanCapture` (178), `PlanMineUpgrade` (220), сортировка по квадрату прямого расстояния (209/255). Полный world остаётся доступен планировщику; наличие фильтра poses не доказывает полной защиты от скрытой информации.
- [ArmyPlanner](../../../src/OpenRTS.AI/ArmyPlanner.cs): переданные rally/objective и вычисление `RallyPoint`; стратегический атлас карты здесь пока не реализован.
- [План баз](../rts-ai-architecture/base-management.md) и [общий проект ИИ](../rts-ai-architecture/README.md): статусы отдельных срезов сохранены; предложения этой папки уточняют географический вход.

Это статическое чтение, не игровой аудит силы или полная проверка всех каналов knowledge leak.

### S005

[ADR033](../../decisions/033-fog-of-war.md) и [ADR034](../../decisions/034-sensor-observations.md): круговой обзор без terrain/height occlusion, режимы/история/контакты, фильтрация по viewer; полный grid не равен knowledge-aware навигации. Более поздние дополнения ADR033 о режимах учтены: историческая фраза о выключенном fog в skirmish не принята за текущее состояние.

Следствие для предложения: запечённые высотные преимущества зрения и полная статическая видимость не являются действующими правилами OpenRTS.

### S006

[Формат карт](../game-maps/README.md), [исследование Godot](../godot-integration/README.md), его [авторская подготовка](../godot-integration/authoring.md) и [текущая граница](../godot-integration/current-boundary.md), [навигационные слои](../navigation-layers/README.md) и [N4](../navigation-layers/roadmap.md). Также просмотрены относящиеся к теме части [README](../../../README.md), [плана](../../plan.md) и [архитектуры](../../architecture.md).

Связанный чат Godot `01a1d0c7-e391-74d1-bea6-abd0ea545fc8` прочитан инструментом `read_thread` в предыдущей части работы; его результаты сохранены в указанной папке. Факты о текущей реализации уточнялись по нынешним исходникам, а не по заголовку чата.

## Внешние первичные источники

Доступные страницы/исходники проверены в ходе этого обсуждения 15–16 ноября. Для изменяемых `master`/`develop`/`stable` ниже указана ветка: внешний commit не закреплён и байтовый снимок не сохранён. Это ограничение воспроизводимости обзора, не основание приписывать им неизменный современный контракт. Перед возможным внедрением зависимости потребуется отдельная проверка конкретной поставки.

### S101

[Spring Default PathEstimator.cpp, develop](https://raw.githubusercontent.com/spring/spring/develop/rts/Sim/Path/Default/PathEstimator.cpp). Изучены `InitEstimator` 133–202, `ReadFile` 858–915, `WriteFile` 922–961, `CalcHash` 1023–1037. Они подтверждают загрузку/создание постоянного кэша и его зависимости. Это Default path estimator конкретной ветки; его поведение не приписывается автоматически современному QTPFS Recoil. Код не копировался и Spring не запускался.

### S102

[Alex Kring, Alex J. Champandard, Nick Samarin. DHPA* and SHPA*: Efficient Hierarchical Pathfinding in Dynamic and Static Game Worlds, AIIDE 2010](https://cdn.aaai.org/ojs/12397/12397-52-15925-1-2-20201228.pdf). Прочитаны введение и описание построения/поиска, включая локальное обновление кластеров и компромиссы памяти/качества. Оригинальная статья HPA* 2004 в прежней части не загрузилась; здесь не выдаётся за непосредственно прочитанную. Чужие коэффициенты ускорения не используются как прогноз OpenRTS.

### S103

[OpenRA HierarchicalPathFinder](../../../../OpenRA/OpenRA.Mods.Common/Pathfinder/HierarchicalPathFinder.cs), локально изученная ревизия предыдущей части `9636b63eaee62fcb60ef2e896e7985bdf2f1ee68`. Прочитаны комментарии архитектуры около строк 45–100 и точки обновления графа: области внутри grid, направляющий абстрактный поиск, учёт выбранной неподвижной блокировки. Это пример иерархии, а не доказательство постоянного файлового кэша OpenRA. Код GPL не переносился.

### S104

[Recast Navigation README](https://github.com/recastnavigation/recastnavigation), [Sample_TileMesh.cpp, main](https://github.com/recastnavigation/recastnavigation/blob/main/RecastDemo/Source/Sample_TileMesh.cpp), [DetourTileCache.cpp, main](https://raw.githubusercontent.com/recastnavigation/recastnavigation/main/DetourTileCache/Source/DetourTileCache.cpp). Проверены разделение генерации/исполнения, Save/Load тайлов в демо и `queryTiles`/`update` для изменений препятствий. Не выполнялась интеграция с C#, fixed-point или нашими weighted rules; пригодность как прямой замены не доказана.

### S105

[Godot: Using navigation meshes, stable](https://docs.godotengine.org/en/stable/tutorials/navigation/navigation_using_navigationmeshes.html), особенно разделы source geometry и baking chunks. Подтверждает отдельное извлечение, сериализацию и повторное использование geometry, профильные параметры, ограничения потока SceneTree и стыковку чанков.

[Using NavigationObstacles](https://docs.godotengine.org/en/stable/tutorials/navigation/navigation_using_navigationobstacles.html): различаются участие в baking и avoidance. Подвижное препятствие для avoidance не объявлено автоматически изменением маршрутизирующего графа. Версия stable не равна гарантированному API проекта с закреплённым Godot; прототип не выполнялся.

### S106

[Unreal Engine: Navigation System](https://dev.epicgames.com/documentation/en-us/unreal-engine/navigation-system-in-unreal-engine). Подтверждены построение графа из collision geometry, тайлы/стоимости, режимы Static/Dynamic/Dynamic Modifiers Only и отдельные avoidance-возможности. Используется как пример разделения обязанностей; Unreal не исследован по исходникам и не тестировался.

### S107

[Alberto Uriarte, Santiago Ontañón. Improving Terrain Analysis and Applications to RTS Game AI, AIIDE Workshop 2016](https://cdn.aaai.org/ojs/12889/12889-52-16406-1-2-20201228.pdf). Прочитаны описание регионов/chokepoints и «Extra cache operations for AI queries»: компоненты, resource clustering, места баз, расстояния и nearest points. Пример относится к StarCraft. Его DBSCAN-параметры, игровые предпосылки и опубликованные ускорения не объявлены подходящими OpenRTS. Не доказывает рост силы произвольного ИИ только от наличия анализа.

### S108

[BWEM homepage](https://bwem.sourceforge.net/) и [FAQ](https://bwem.sourceforge.net/faq.html), сайт версии 1.4. Подтверждены areas/chokepoints/bases, назначенные ресурсы, distance-to-obstacle «altitudes», частичное обновление при исчезновении блокирующих минералов и ограничения автоматических результатов. Это документация автора, не проверка реализации/запуск на наших картах. Заявления автора о скорости не использованы как собственные измерения.

### S109

[BWEB README](https://github.com/Cmccrave/BWEB). Изучены Walls/Blocks/Stations, сохранение выхода, кандидаты обороны и размещения производства. Это документированный набор возможностей, не проверка полноты/оптимальности алгоритма на OpenRTS. Код не заимствован, addon не запускался.

### S110

[Blizzard s2client-api sc2_search.cc, master](https://raw.githubusercontent.com/Blizzard/s2client-api/master/src/sc2lib/sc2_search.cc). Прочитаны `Cluster` (32–59), `CalculateQueries` (8–31), `CalculateExpansionLocations` (61–124): ресурсный фильтр, группировка, пакетная проверка размещения и выбор точки. У этого примера конкретные StarCraft-типы и Command Center; он не является универсальным размещением базы и не переносится буквально.

### S111

[0 A.D. Petra headquarters.js, master](https://raw.githubusercontent.com/0ad/0ad/master/binaries/data/mods/public/simulation/ai/petra/headquarters.js). Прочитаны `findEconomicCCLocation` (593 и далее), `findStrategicCCLocation` (777 и далее), `findDefensiveLocation` (1024 и далее): текущие карты ресурсов, территории/доступа, текущие центры, препятствия и угрозы. Это контрпример идее целиком запечь выбор экономики/обороны. Морская логистика и территория Petra не являются механиками OpenRTS по умолчанию. Код GPL изучен только как референс.

## Матрица существенных выводов

| ID | Вывод | Основание | Статус и границы |
| --- | --- | --- | --- |
| C001 | Постоянная подготовка навигации применяется в RTS | S101 | Факт о Spring Default, не любой RTS |
| C002 | Иерархия и файл дают разные виды экономии | S001, S101–S104 | Вывод по устройству; величина эффекта OpenRTS не измерена |
| C003 | Terrain может быть постоянен при изменяемой глобальной связности | S001, S108 | Факт о механизмах; стратегия обновления MC5 — предложение |
| C004 | Нынешняя холодная цена шире имеющегося таймера полей | S001, S002 | Факт по расположению измерения; новый baseline отсутствует |
| C005 | Географические данные полезно готовить для ИИ | S107–S110 | Подтверждены механизмы референсов; польза OpenRTS — гипотеза MC3/MC6 |
| C006 | Выгодность расширения и оборона зависят от матча | S003, S111 | Подтверждённая зависимость; предложенные оценки требуют проверки |
| C007 | Площадки имеют непосредственного потребителя в текущем ИИ | S004 | Факт о точке расширения; ускорение/качество не измерены |
| C008 | «Тыл» и независимость захода нельзя заменить постоянным направлением карты | Геометрические контрпримеры в ai-analysis.md | Проектное рассуждение; метод классификации пока гипотеза |
| C009 | Текущий обзор не обосновывает bake высотного LoS | S005 | Факт действующих правил, не отрицание будущего расширения |
| C010 | Сцена Godot может быть источником независимого артефакта | S006, S105 | Архитектурное предложение; функции извлечения документированы, пилота нет |
| C011 | Наличие технической карты не разрешает ИИ скрытые runtime-факты | S004–S005 и контракт T6 | Требование предлагаемого потребителя; полнота существующей изоляции не доказана |
| C012 | Следует начать с baseline и ограниченных потребителей | Синтез C002–C011 | Рекомендация; приоритеты пересматриваются по MC0 |

## Ограничения и пробелы

Не выполнялись прототипы, новые benchmarks, турниры, проверка лицензий конкретных поставок для внедрения, полный разбор всех чужих backend или полный аудит ИИ. Не выбраны размеры секторов, коэффициенты стратегической оценки, формат файла и численные бюджеты кроме уже известной длительности тика. Нет доказанного процента ускорения, полной непрерывной достижимости, оптимальной расстановки баз и общего разрешения заторов.

Основные неизвестные, способные изменить рекомендацию: доля текущей подготовки в реальной нагрузке, цена смены effective grid, число профилей/целей, качество географических кандидатов на нетипичных картах, стоимость их актуализации, разрешённые знания в режиме игры и требуемая сложность авторских карт. Для них предусмотрены MC0 и эксперименты в [validation.md](validation.md).
