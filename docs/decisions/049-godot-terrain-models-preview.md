# 049. B3: terrain brushes, Models preview и физические контуры

**Дата:** 18 ноября 2026. **Статус:** реализован B3 поверх [B2 addon](048-godot-map-authoring.md) и [независимого формата 047](047-map-compilation.md). [Проверки и изображение](../verification/049-godot-terrain-models-preview.md), [план B](../research/godot-integration/stage-b-design.md).

## Источник terrain в сцене

`OpenRtsMap.TerrainId/VisualId` задают базовые однородные слои. Новый exported `TerrainStrokes` — сохранённая упорядоченная последовательность ресурсов `OpenRtsTerrainStroke`:

- `StrokeId` — стабильный ID для диагностики; выдаётся при начале рисования.
- `Brush` — snapshot `OpenRtsTerrainBrush`: Size и row-major массивы символических Terrain/Visual IDs.
- `Cells` — последовательность верхних левых клеток stamp, `QuarterTurns` — 0..3 по часовой стрелке.

В brush пустая строка означает «сохранить предыдущий слой». Оба пустых поля клетки создают отверстие кисти; полностью пустая кисть недопустима. Поэтому поддерживаются независимые terrain-only, visual-only, совместные и разреженные shaped brushes. Последующие stamps перекрывают предыдущие отдельно по полям, как в M1. Порядок массива strokes значим, NodePath и сортировка GUID его не задают.

`MapTerrain.Compose` использует существующий `TerrainLayout.Apply`: повороты, sparse cells и полное вхождение прямоугольника проверяет тот же алгоритм, что применяется M1. Затем `MapSceneExtractor` возвращает обычные `MapLayer` в `MapSource`. В compiler не добавлены Godot refs, brush resources или новый формат карты. YAML и compiled v1 не изменились.

Корень и плоскость остаются B2 ground-plane v1: Godot X/Z, Y=0, 1 unit=1024 world units. Hit кисти округляется вниз до **клетки terrain**; это не меняет точность размещения юнитов. Частично выходящая за карту кисть показывается красной и не применяется; извлечение вручную повреждённого stroke завершается ошибкой, без обрезания. Размер квадратной UI-кисти 1..16; Resource brush допускает стороны 1..64. Ограничения: до 1 млн stamps и до 16 Mi суммарных painted-cell применений, плюс квоты 047. Это ограничения входа, не обещание интерактивной работы на максимумах.

## Работа кистью и undo

Dock получает palette из текущего LocomotionCatalog, отдельные terrain/visual selectors, режим слоя, square size, quarter-turn и ResourcePicker для `.tres` shaped brush. Пример — [RoadBrush.tres](../../sandbox/examples/maps/RoadBrush.tres). Это новый authoring Resource; YAML brush parser не дублируется и не расширяется. Общая семантика применения остаётся `TerrainLayout`.

В режиме **Paint terrain** left drag проецируется лучом viewport Camera3D на плоскость карты. Между mouse samples добавляются промежуточные клетки; одинаковые anchors внутри одного drag не повторяются. Alt и обычная навигация камерой проходят редактору; Esc, right-click во время мазка, смена сцены, выключение painting/plugin отменяют незавершённый мазок.

До release preview читает временный stroke, а авторский `TerrainStrokes` не меняется. На release кисть проверяется, и вся новая последовательность назначается одной EditorUndoRedo action. Undo снимает весь drag, redo возвращает те же IDs/значения. Кисть копируется в stroke: последующее изменение palette/preset не меняет уже нанесённую карту. Strokes и их Brush сохраняются обычными subresources; для сложных ручных правок действуют стандартные Godot правила shared resources/Make Unique.

Compile сначала завершает текущий stroke; неправильный preset не создаёт частичную action. **Extract source** по-прежнему выпускает независимый inspection document. Одновременная публикация `.tscn` и compiled bundle, F6/export integration и provenance остаются B4.

## Пакетный Models preview

`MapEditorPreview` читает `ModelCatalog` и использует действующий `SpriteWorldView`. Для каждого authored object формируется статический `EntityPose`: текущий UnitType, точная позиция, owner color, idle playback и полное HP. Мир, ECS handles и runtime orders ради изображения не создаются. Отображаются реальные Model parts/Parent/Pivot/Offset/Layer и SpriteFrames через общий renderer; preview статический, без игрового тика, боя и проигрывания сценария.

`TerrainView` рисует visual IDs независимо от физического terrain. `OpenRtsMap.VisualTextures` позволяет явно сопоставить visual ID с Texture2D/AtlasTexture; fallback для текущего host — `res://resources/terrain/<id>.tres`, как в Sandbox. Binding/Model art не входят в gameplay fingerprint. Замена Texture2D binding обновляет preview; после внешних изменений Models/SpriteFrames или Atlas region используется **Reload Models and textures**.

Preview — внутренний `Owner=null` child корня, исключённый из обычного обхода/сохранения. Внутри используются batches, без Node на каждый игровой объект. После scene switch/disable/failure они освобождаются. Неудачная загрузка SpriteFrames также освобождает частично созданный renderer.

Для editor preview в `SpriteWorldView` передаётся конкретный loader SpriteFrames с `ResourceLoader.CacheMode.Ignore`. Это не помещает мигрировавшие старые SpriteFrames в editor auto-save cache; внешние texture dependencies продолжают разделять обычный cache. Runtime по умолчанию использует прежний GD.Load. Без этой изоляции Godot при сохранении сцены переписывал загруженные старые SpriteFrames в новый текстовый синтаксис. Проверено, что итоговые authoring checks не меняют `sandbox/resources`. В PNG import-файлах спрайтов/terrain сохранён `compress/mode=0`, а `detect_3d/compress_to=0` отключает автоматический перевод в VRAM-сжатие при показе в 3D viewport. Исходные PNG и их SHA не менялись; иначе GPU preview сам переписывал import-конфигурацию и мог исказить RGBA/team-color маркер.

Preview обновляется после editor actions и при движении выбранных объектов с ограничением частоты около 0.15 s. Каталоги не компилируются заново на каждый mouse motion; terrain batches перестраиваются при изменении слоя, размера или texture binding. Это первый профиль B3, без измеренных гарантий 10000 markers — B7 остаётся отдельным этапом.

## Body, footprint, выбор и диагностика

Gizmos рисуют физические данные UnitDefinition, не Model.Size:

| Контур | Значение |
| --- | --- |
| Cyan circle / axis-aligned box | BodyShape с настоящими half extents в world units |
| Blue footprint cell | Явный Traverse=false |
| Amber cell | Явный CanStop=false при отсутствии Traverse=false |
| Green cell | Остальные разрешающие/наследуемые overrides; не гарантия доступности всем locomotors |
| Pink diagonal | Targetable=false, отдельно от проходимости |

Используются только реальные sparse `FootprintDefinition.Cells`, их размеры и смещения. Bounds всего rectangle не превращаются в заполненный collider. Gizmos поддерживают picking; root сохраняет контур карты. Неподдержанные scale/rotation/height по-прежнему отвергаются извлечением, не исправляются видом картинки.

**Select by ID** находит точный контекстный ID вроде `patrol/tank`, включая prefab. Повторяющийся/отсутствующий ID даёт ошибку вместо случайного выбора. Inspector получает выбранный Node; dock показывает read-only HP, Model ID, Body и число footprint cells. **Select error source** выбирает marker по scene locator или stroke Resource; поле присутствует в сообщении. Для общих ошибок выбирается map root.

Compile в addon теперь дополнительно проверяет **только authored objects** через временную fixture-проекцию и обычные `MapMatch.CreateWorld` placement rules на compiler worker. Проверяются тела, footprint, bounds и stopping terrain. Ошибка сохраняет предыдущий artifact. Это не проверка стартовых армий: их состав, назначения и конкретный starting set проверяются при запуске матча. Общий `MapCompiler.Compile` остаётся структурным, как в 047; новой второй реализации collision rules нет.

## Как проверить

1. `./tools/dev.sh editor`; открыть [TerrainAuthoringMap.tscn](../../sandbox/examples/maps/TerrainAuthoringMap.tscn).
2. Открыть OpenRTS dock через Project → Tools → OpenRTS map authoring. Models и terrain preview включены по умолчанию; при необходимости нажать **Load type catalog** или **Reload Models and textures**.
3. Включить **Paint terrain**, выбрать IDs/слой/размер и рисовать left drag. Для shaped brush загрузить RoadBrush.tres в picker; выбрать quarter-turn.
4. Выделить Factory или `patrol/tank`, посмотреть физические gizmos/Inspector. Изменить Transform, проверить undo. Сохранить сцену и **Compile map**.
5. Headless читает результат без addon:

```sh
./tools/dev.sh headless --map=artifacts/maps/terrain-authoring-sample.ortmap.json --start-set=content/start-sets/infantry.yaml --ticks=32
./tools/dev.sh verify-authoring
```

Scene entry loader, полный экспорт и игровой playtest из F6 остаются B4. Высоты, mesh-to-terrain bake, navigation/AI caches, map-local stats и новые механики в B3 не добавлены.
