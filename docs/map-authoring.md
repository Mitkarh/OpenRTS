# Создание и запуск карт

## Форматы карт

YAML позволяет задать карту непосредственно для симуляции. Сцены Godot дают возможность рисовать рельеф и размещать маркеры в редакторе. Общий компилятор выпускает независимый `.ortmap.json` для обоих путей. Для 3D можно также собрать `.scenemap.json`, который связывает проекцию с исходной сценой Godot и её ресурсами.

## Запуск и компиляция YAML-карт

Штатный skirmish читает [YAML-карту](../sandbox/content/maps/skirmish.yaml) и [стартовый набор](../sandbox/content/start-sets/supply.yaml). Карта задаёт грунт, нейтральные объекты и точки старта. Набор задаёт кредиты игроков и состав сил с точными смещениями от стартовой точки. Для карты с готовой расстановкой используйте `kind: fixture` и именованные слоты владельцев.

```sh
./tools/dev.sh run --map=content/maps/fixture.yaml
./tools/dev.sh run --skirmish --start-set=content/start-sets/infantry.yaml
./tools/dev.sh headless --map=content/maps/skirmish.yaml --start-set=content/start-sets/infantry.yaml --ticks=25
```

Относительные пути к картам и стартовым наборам отсчитываются от `sandbox/`. После редактирования YAML запустите карту заново. При прямом запуске оба хоста назначают до четырёх игроков по порядку имён слотов. Флаги `--skirmish` и `--map` запускают матч сразу. Для конечного прогона в Godot добавьте `--verify-simulation` и `--ticks`. [Формат и правила загрузки](research/game-maps/README.md).

Скомпилировать базовую карту и проверить результат можно в headless.

```sh
./tools/dev.sh headless --map=content/maps/fixture.yaml --compile-map=/tmp/fixture.ortmap.json
./tools/dev.sh headless --map=/tmp/fixture.ortmap.json --ticks=32
```

Компилятор также принимает `.mapsource.json`. Контракт компиляции сохраняет совместимость с YAML формата M1. [Пример MapSource](../sandbox/content/maps/compilation-fixture.mapsource.json) · [Контракт компиляции](decisions/047-map-compilation.md).

## Редактор Godot

Запустите `./tools/dev.sh editor`. В sandbox опциональный addon OpenRTS Map Authoring включён по умолчанию. Для обзора возможностей откройте [AuthoringMap](../sandbox/examples/maps/AuthoringMap.tscn) или [TerrainAuthoringMap](../sandbox/examples/maps/TerrainAuthoringMap.tscn). В редакторе доступны повторно используемые группы, предпросмотр Models, тел и footprint. [Узлы и Inspector](decisions/048-godot-map-authoring.md) · [Кисти и предпросмотр](decisions/049-godot-terrain-models-preview.md) · [Стартовые свойства](decisions/051-map-initial-properties.md).

Чтобы начать 2D-карту, откройте пустую сцену и выберите **New top-down map** или **New isometric map** в панели OpenRTS. Рисуйте на `Terrain` обычными инструментами TileMapLayer. В панели можно выбрать тип и владельца нового объекта, добавить старт и дублировать маркеры. Положения и свойства маркеров редактируются в Inspector. Для skirmish доступны нейтральные объекты, для fixture также доступны владельцы из `FixtureSlots`.

В обоих 2D-шаблонах **Terrain → Terrains → Connect** подбирает переходы между **Grass** и **Dirt (visual)**. Четыре точных диагонали находятся в нижнем ряду **Tiles**. Выбирайте готовый тайл нужного направления. Трава, грунт и переходы этой кисти имеют один игровой тип `grass`. Ручной тайл дороги в том же ряду задаёт `road`. Готовый переход можно открыть и запустить через **Build and play map**. Атласы и текстурные ресурсы собираются из штатных текстур Godot-скриптом. [Сцена DiagonalTransitions](../sandbox/examples/maps/DiagonalTransitions.tscn) · [Инструкция генератора переходов](../tools/prepare_terrain_transitions.md).

## Сборка, локальный запуск и повтор

**Build and play map** для skirmish предлагает выбрать до четырёх стартов и стартовый набор из манифеста карты. Порядок стартов в списке задаёт порядок игроков. Для fixture используются фиксированные слоты и расстановка. Перед запуском редактор проверяет расстановку в симуляции. При ошибке **Select error source** выбирает исходный маркер или слой рельефа, а координаты проблемного тайла показаны в сообщении. 2D запускается из скомпилированных данных, для 3D редактор также собирает bundle с исходной сценой.

**Build and verify map** проверяет тот же матч без открытия игрового окна. Godot и симуляция без Godot читают одну сборку и сравнивают отчёты после восьми тиков. Редактор публикует карту после совпадения. Из терминала тот же путь доступен через `map-build --verify`. [Команды для обоих форматов и правила установки](decisions/050-godot-scene-map-bundle.md#сборка-и-проверка).

**Build and record playtest** запускает локальный матч с записью. В меню матча выберите **Сохранить повтор и выйти**. Зарегистрированные 2D compiled maps и 3D bundles доступны в сетевом лобби. Повтор можно открыть в Godot или проверить командой `./tools/dev.sh replay /path/to/match.journal`. Для архивной карты укажите исходный артефакт. [Запуск, совместимость и проверка B6](decisions/052-map-match-replays.md).

```sh
./tools/dev.sh map-build res://examples/maps/RockyPass.tscn --format=compiled --verify
./tools/dev.sh map-build res://examples/maps/AuthoringMap.tscn --format=bundle --verify --start=west --start-set=content/start-sets/infantry.yaml
./tools/dev.sh verify-authoring
```

Чтобы добавить skirmish-карту в меню, зарегистрируйте путь к итоговой сборке в `Match.Maps` игрового манифеста. Для установки и поставки через PCK используйте артефакты под `res://content/`. [Регистрация и экспорт карт](decisions/050-godot-scene-map-bundle.md#сборка-и-проверка).

## Пример с высотой и рампами

Для редактирования «Скалистого перевала» откройте сцену, выберите `Terrain` и используйте обычную кисть TileMap. В палитре слева направо идут низина, плато и восемь участков рампы сверху вниз. Маркеры размещаются на базовой XY-сетке. Высоту старта или объекта компилятор вычисляет по выбранному тайлу. В панели OpenRTS нажмите **Build and play map**, чтобы запустить результат в обычном sandbox. [Сцена RockyPass](../sandbox/examples/maps/RockyPass.tscn) · [Контракт рельефной карты](decisions/047-map-compilation.md#версия-3--одна-поверхность).

После **Compile map** ту же карту можно запустить из терминала.

```sh
./tools/dev.sh run --map=res://artifacts/maps/rocky-pass.ortmap.json --mod=res://rocky-pass-game.yaml
./tools/dev.sh headless --map=artifacts/maps/rocky-pass.ortmap.json --mod=sandbox/rocky-pass-game.yaml --ticks=32
```

[Диагностический сценарий и проверка рамп](sandbox.md#диагностические-сценарии).

Для своего диагонального склона задайте в TileSet явный тайл с ненулевыми компонентами `Rise.x` и `Rise.y`. Например, `Rise = (128, -128)` даёт уклон 182 для `MaximumSlope`. На стыке с соседним тайлом сравните высоты обоих концов общей грани. Совпадение обеспечивает плавный переход. Чтобы локомотор с `MaximumStep = 0` мог пройти с диагональной рампы на плато, соедините их переходными участками с уклоном по одной оси. Скальные стенки строятся вдоль границ клеток. Диагональный контур на спрайте сам по себе оставляет геометрию клетки прежней.

## Полигон диагональных рамп и обрывов

Откройте [DiagonalRamps.tscn](../sandbox/examples/maps/DiagonalRamps.tscn) в Godot и нажмите **Build and play map**. На северо-западе карты находится диагональный подъём, с юга и востока плато обрывается. Выберите пехотинца или танк, отдайте приказ подняться на плато, затем нажмите ПКМ на низине за южным обрывом. Чтобы попасть туда, юнит обходит стенку и спускается по склону.

```sh
./tools/dev.sh map-build res://examples/maps/DiagonalRamps.tscn --verify
./tools/dev.sh run --map=res://artifacts/maps/diagonal-ramps.ortmap.json --mod=res://rocky-pass-game.yaml
./tools/dev.sh run --map=res://artifacts/maps/diagonal-ramps.ortmap.json --mod=res://rocky-pass-game.yaml --verify-ramps
```

В [TileSet полигона](../sandbox/examples/maps/DiagonalRampTiles.tres) есть участки с подъёмом по двум осям, переходы к ровной поверхности и скальная кромка. Атлас использует существующие фактуры травы, рампы, гор и скальной стенки. В игровом sandbox стенки строятся по перепаду высот общей грани.

## Диагональный обрыв внутри клетки

Откройте [DiagonalCliffs.tscn](../sandbox/examples/maps/DiagonalCliffs.tscn). В [палитре](../sandbox/examples/maps/DiagonalCliffTiles.tres) слева идут низина, плато, четыре обрыва и ровный берег. Для собственной палитры задайте в custom data `Elevation` для нижней половины, `Rise = (0, 0)`, `UpperCorner` от 1 до 4 по часовой стрелке от северо-запада и `UpperElevation` для верхней половины. У обычного тайла оба поля `UpperCorner` и `UpperElevation` равны нулю. Выбирайте вариант нужного направления в палитре **Tiles**. [Контракт карты v4](decisions/047-map-compilation.md#версия-4--диагональные-обрывы).

Нажмите **Build and play map**. Нижний пехотинец обойдёт плато при приказе двигаться на противоположную сторону. Верхнего можно направить на верхнюю половину пограничного тайла. Клик по самой стенке пропускается.

```sh
./tools/dev.sh map-build res://examples/maps/DiagonalCliffs.tscn --verify
./tools/dev.sh run --map=res://artifacts/maps/diagonal-cliffs.ortmap.json --mod=res://rocky-pass-game.yaml
```

## Разные типы грунта в одной клетке

В том же TileSet добавьте строковый слой `UpperTerrainId`. Обычный `TerrainId` задаёт грунт нижнего треугольника, а `UpperTerrainId` — грунт треугольника со стороны `UpperCorner`. Пустое поле сохраняет общий грунт. Для отдельной фактуры в игре добавьте строковый `UpperVisualId`. Изображение самого атласного тайла задаёт, как обе половины выглядят в редакторе.

В `DiagonalCliffs` плато покрыто грязью, а низина — травой. Тайл `(1, 1)` показывает берег с травой и водой на одинаковой высоте. Пехота может остановиться на траве, если всё тело помещается на ней. [Контракт карты v5](decisions/047-map-compilation.md#версия-5--раздельный-грунт).

## Проверка стрельбы через обрыв

`./tools/dev.sh verify-authoring` запускает проверки стрельбы на скомпилированной карте `DiagonalCliffs`. Тест ставит пушки и цели по обе стороны откоса. Для `Trajectory: Direct` линия огня с низины на плато и обратно проходит над кромкой, если позволяет угол, и блокируется породой при пересечении стенки. В отдельном прогоне цель меняет положение после запуска ракеты. Ракета меняет курс и попадает в стенку без урона цели. Для своего оружия задайте `Trajectory` в блоке `Weapon`. [Режимы траекторий](decisions/011-weapons-and-projectiles.md#траектория-и-диагональные-обрывы).
