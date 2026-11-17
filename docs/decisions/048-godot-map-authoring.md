# 048. Опциональный scene authoring addon — B2

**Дата:** 18 ноября 2026. **Статус:** реализован B2 поверх [контракта 047](047-map-compilation.md). [План B](../research/godot-integration/stage-b-design.md), [проверки](../verification/048-godot-map-authoring.md).

## Поставка и граница ответственности

Addon находится в [sandbox/addons/openrts_editor](../../sandbox/addons/openrts_editor/), включён для Sandbox через `editor_plugins`. Его можно выключить в Project Settings → Plugins. Godot .NET 4.7.2 собирает C# plugin до включения; compiler/Core/Gameplay не зависят от addon. Новых пакетов и сборок нет. В другом Godot .NET проекте нужны исходники addon, ссылка на Content и включение этих `.cs` в compile items, если default items выключены.

EditorPlugin/gizmos/editor checks закрыты `#if TOOLS`. Авторские `[Tool, GlobalClass]` wrappers доступны в Create Node и Inspector, но не создают игровых сущностей в `_Ready`. Уже полученный `.ortmap.json` читает прежний headless без Godot; отключение plugin не меняет файл или игровой fingerprint. Полная проверка runtime export, исключение authoring markers и loader entry сцены относятся к B4.

Используются стандартные [Godot C# plugins](https://docs.godotengine.org/en/stable/tutorials/plugins/editor/making_plugins.html), [tool scripts](https://docs.godotengine.org/en/stable/tutorials/plugins/running_code_in_the_editor.html) и [EditorUndoRedoManager](https://docs.godotengine.org/en/stable/classes/class_editorundoredomanager.html). Addon не заменяет SceneTree/Inspector собственным документом.

## Авторские узлы

| Узел | Поля Inspector | Роль |
| --- | --- | --- |
| `OpenRtsMap` | MapId, IsSkirmish, Size, TerrainId/VisualId, FixtureSlots, ContentManifest, CompiledPath | Единственный root сцены; bounds и однородные слои B2 |
| `OpenRtsMapObject` | ObjectId, Order, TypeId, OwnerSlot; обычный Transform | Объект известного gameplay типа; в skirmish только neutral, в fixture также slot |
| `OpenRtsMapStart` | StartId, Reverse; обычный Transform | Skirmish start; в fixture запрещён |
| `OpenRtsMapGroup` | InstanceId, Order; обычный Transform | Контекст повторно используемой группы/subscene |

Все игровые позиции лежат на X/Z Godot, Y=0; 1 Godot unit = 1024 world units. Целочисленная координата вычисляется один раз через `Round(AwayFromZero)`; точный unit placement не округляется до navcell. Допускается обычный перевод parent Nodes, включая prefab instance. Итоговый basis каждого semantic marker — identity, с численной точностью Godot; rotation/scale/mirror, TopLevel в цепочке и ненулевая высота отклоняются. Root имеет identity Transform. Вложенный map root — ошибка, для композиции используется group.

Пока нет brushes/TileMap/mesh extraction: TerrainId и VisualId задают два независимых однородных слоя Size. B3 добавит рисование и Models/body/footprint preview. B2 gizmos показывают рамку bounds, кресты/маркеры объектов, стартов и групп, позволяют выбрать узел в viewport. Это не игровые тела и не sprite/model preview.

## IDs, порядок и обычные операции сцены

Локальные IDs непустые, ordinal, без `/`, `.` и `..`. Каждый group создаёт префикс `InstanceId/`; например `patrol/rifle`. Имена и NodePath используются только для диагностики. Prefix/ID хранится в сцене, поэтому rename, перестановка в SceneTree и reparent внутри того же контекста с сохранением transform не меняют вход. Перенос в другой semantic group намеренно меняет контекст ID. Starts получают такой же префикс; fixture slots задаются на root.

У objects/groups обязательный неотрицательный `Order`, уникальный в ближайшем map/group контексте, независимо от организационных промежуточных Nodes. Размещения сортируются лексикографически по цепочке group Order и локальному Order. Поэтому prefab сохраняет локальный порядок, а его instances получают явное место в общей последовательности. Порядок starts остаётся ordinal по итоговому slot ID, как в 047. Обычная перестановка SceneTree порядок спавна не задаёт.

Dock поддерживает:

- **New map** в пустой сцене; **Load type catalog** из ContentManifest; палитру всех готовых UnitCatalog типов.
- **Add object/start/group** под выбранным map/group, иначе под root; стандартное выделение и Inspector. Новому marker сразу выдаётся сохранённый ID; object/group получает следующий свободный Order текущего контекста.
- **Duplicate selected** для нескольких markers, с отбрасыванием вложенных выбранных descendants. Новый ID/Order назначается в одной undo action. При копировании prefab меняется namespace instance, локальные ID сохраняются.
- **Repair IDs and order** — явная undo action для пустых/duplicate локальных IDs и конфликтующих Orders. Redo повторяет те же значения, не генерирует новый GUID.

Native Duplicate/Paste также доступны, но Godot копирует exported IDs. Поэтому после них при необходимости применяется **Repair IDs and order**; compiler сам ничего не чинит. Для внутренних свойств read-only prefab нужно открыть исходную subscene или разрешить Editable Children стандартным способом Godot. Addon не заменяет правила владения сценами. Общие stats/HP/quarter-turn overrides остаются B5.

## Извлечение и публикация

`MapSceneExtractor.Extract(OpenRtsMap)` вызывается целиком на Godot owner thread, без await. Читает все declared markers, в том числе под decoration Nodes и внутри instanced scenes; Mesh/Light/CollisionShape сами по себе gameplay не создают. Возвращает immutable `MapSource`, без Godot refs. `Origin` указывает на scene/NodePath; ошибки TypeId/owner/transform/order содержат locator и поле. Ограничения обхода: до миллиона Nodes, depth≤128; лимиты карты — 047.

**Compile map:** снять snapshot → загрузить immutable catalog через GameManifest → `Task.Run(MapCompiler.Compile)` → вернуться на owner thread → проверить token/generation, текущую сцену, повторно извлечённый source и fingerprint повторно загруженного каталога → `MapCompiler.Save`. Только затем показывается успех. Scene/undo change, cancel или plugin disable инвалидируют job; запоздалый ответ не заменяет существующий artifact. Snapshot сравнивается и для изменений, не прошедших через undo. Node setters не запускают compiler на каждом mouse motion.

**Extract source** использует ту же подготовку/проверку и отдельно атомарно записывает нормализованный `.mapsource.json`, рядом с выбранным CompiledPath. Это inspection/CI документ, производный от сцены; кнопка не публикует compiled artifact. Compile не перезаписывает авторскую `.tscn` и не требует её предварительного сохранения: несохранённое состояние явно остаётся ответственностью автора. Сохранение сцены — обычный Godot Save.

Пути input/output должны находиться внутри `res://` проекта. При ошибке/отмене целевой файл сохраняется. Создание directory допустимо, незавершённый temporary удаляется. Source и compiled — отдельные операции; атомарного scene-map bundle/manifest, provenance и автопересборки F6/export здесь ещё нет (B4). Изменения внешних файлов после последней проверки не блокируются файловой транзакцией.

Результат означает структурную корректность, не проверку всех назначений игроков/start sets или отсутствие пересечения тел. Финальная проверка остаётся `Prepare(...).CreateWorld(...)`, как в B1. В status это указано явно; старый artifact после failed/dirty не объявляется новым.

## Работа в Sandbox

1. `./tools/dev.sh editor`; открыть `res://examples/maps/AuthoringMap.tscn`.
2. Если dock скрыт: Project → Tools → **OpenRTS map authoring**, либо Editor → Editor Docks → OpenRTS. Если plugin выключен — включить **OpenRTS Map Authoring** в Project Settings → Plugins после сборки C#.
3. **Load type catalog**, выбрать тип, **Add object**. Выделять, двигать в 3D viewport и менять TypeId/OwnerSlot/Order в Inspector. Для старта — **Add start**, затем StartId/Reverse и Transform.
4. Группу можно сохранить обычным Save Branch as Scene и использовать несколько раз с разными InstanceId/Order. Пример — [AuthoringSquad.tscn](../../sandbox/examples/maps/AuthoringSquad.tscn).
5. Сохранить сцену; **Extract source** и/или **Compile map**. Пример пишет в `res://artifacts/maps/scene-authoring-sample.*` (ignored).
6. Запустить проекцию независимым host:

```sh
./tools/dev.sh headless --map=artifacts/maps/scene-authoring-sample.ortmap.json --start-set=content/start-sets/infantry.yaml --ticks=32
```

Это headless игра с проекцией, не F6-запуск полной сцены карты. Полноценная Godot entry сцена остаётся целью B4; сцена B2 сохраняет обычные Godot ветки для этой дальнейшей работы. Проверки B2 запускаются через `./tools/dev.sh verify-authoring` при включённом addon; если он выключен, команда сообщает об этом и завершает работу до запуска editor checks.
