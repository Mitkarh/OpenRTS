# 048. Проверки scene authoring B2

**Дата:** 18 ноября 2026. macOS arm64, .NET SDK 10.0.105, Godot 4.7.2 .NET. Основа — `8f20068b68be1088442ace4543e49043dc43e210` и изменения B2. [Решение/инструкция 048](../decisions/048-godot-map-authoring.md), [план B](../research/godot-integration/stage-b-design.md), [машинные результаты](048-godot-map-authoring/results.json), [краткий вывод](048-godot-map-authoring/checks.txt).

## Сборка и исполняемые проверки

`./tools/dev.sh verify-authoring` выполняет общую Debug build, import, Godot runtime checks и отдельный запуск настоящего EditorPlugin в headless editor. В этой среде restore использовал локальный NuGet feed и `NuGetAudit=false`; Godot editor запускался с разрешением локального IDE socket. Финальный результат:

- Debug solution: **0 warnings / 0 errors**.
- Scene extraction — **25 checks**, code 0.
- Editor operations — **24 checks**, code 0.
- Регрессии независимых Maps: **14/14**, code 0 (текущие Debug binaries, `--filter=Maps`).
- Дополнительно `dotnet build sandbox/OpenRTS.Sandbox.csproj --configuration ExportRelease --no-restore`: **0 warnings / 0 errors**. Это проверка компиляции без TOOLS/editor API, не полный export игры.

Существующий полный Gameplay набор в B2 не повторялся: Core/Gameplay/Content/Network и M1 loader не менялись. Восемь известных сбоев полного набора из [047](047-map-compilation.md) этим результатом не объявляются исправленными.

## Что проверяют сцены

[MapAuthoringChecks](../../sandbox/Verification/MapAuthoringChecks.cs) исполняется самим Godot, а не эмуляцией Nodes в .NET:

1. Загружает [AuthoringMap](../../sandbox/examples/maps/AuthoringMap.tscn), её instanced squad с rifleman/tank/engineer и два starts; проверяет exported значения, префиксы IDs, точные X/Z координаты и Reverse.
2. Rename prefab, изменение порядка SceneTree, изменение только decoration и reparent внутри контекста не меняют canonical compiled данные.
3. PackedScene pack/reopen сохраняет игровую проекцию; два instances получают раздельные namespace и явный order. Duplicate namespace отклоняется.
4. Native duplicate конфликтует по ID/order; явная Repair устраняет конфликт. Отдельно проверена граница Int32 Order, без выдачи отрицательного ключа при переполнении.
5. Half world-unit округляется AwayFromZero. Unsupported scale, rotation, height, TopLevel, NaN, nested roots, root transform, duplicate order, unknown type и starts в fixture отвергаются.
6. Fixture slots и owner blue извлекаются независимо от skirmish starts. Skirmish с infantry starting set создаёт 9 entities; результат reader одинаков при workers 1/4.

## Что проверяет EditorPlugin

[MapEditorChecks](../../sandbox/addons/openrts_editor/Tests/MapEditorChecks.cs) запускается флагом `--verify-map-editor`, работает с настоящим EditorInterface/EditorUndoRedoManager и не сохраняет исходный sample:

1. Dock прикреплён к editor tree; Add назначает ID/Owner, undo удаляет marker, redo возвращает тот же ID.
2. Inspector-style position action извлекается точно и отменяется. Duplicate объектов/групп выполняется одной action; после undo восстанавливается прежний input.
3. Дублированный prefab сохраняет детей и новые instance IDs в PackedScene. Repair undo/redo не меняет уже выданный ID при redo.
4. SaveSceneAs и повторное чтение `.tscn` сохраняют данные; файл проверки находится в ignored `artifacts/maps/`.
5. Изменение source между началом и завершением compiler, явная отмена и plugin disable сохраняют предыдущий target. Успешная сборка читается общим MapCompiler, Extract source создаёт отдельный `.mapsource.json`.
6. Неизвестный type приводит к Failed и не заменяет хороший artifact. Plugin повторно включается после отключения.

В выводе намеренно присутствует `Unknown unit 'missing'` из отрицательной проверки. Headless SaveSceneAs также пишет Godot dummy-renderer `texture_2d_get: Parameter t is null` при попытке thumbnail; файл успешно сохраняется и повторно читается. Ограниченный macOS runtime может выдавать прежнее `get_system_ca_certificates`; checks и хосты завершаются code 0. Это не скрытые успешным summary исключения собственного extractor.

## Игра из результата editor

Headless:

```sh
./tools/dev.sh headless --map=artifacts/maps/editor-check.ortmap.json --start-set=content/start-sets/infantry.yaml --ticks=32
```

Godot с нынешней Sandbox scene:

```sh
../Godot_mono.app/Contents/MacOS/Godot --headless --path sandbox -- --verify-simulation --map=res://artifacts/maps/editor-check.ortmap.json --start-set=content/start-sets/infantry.yaml --ticks=32
```

Оба процесса завершились code 0: tick **32**, entities **9**, hash `AF97B861E70E99ECF32088C9B8C516AB2A0163EE6960D49A090AC225EB50490C`. Это один artifact, созданный editor pipeline, и одинаковый initialization path; entry scene loader произвольной карты не добавлялся.

## Границы подтверждения

Подтверждены B2 авторские данные, сериализация, undo/redo, фоновые job lifecycle и независимый результат. Native Duplicate/Paste требует явного Repair при конфликте экспортированных IDs; автоматического изменения чужих editor actions нет. Terrain однородный, простые gizmos не заменяют Models/body/footprint preview B3. Compile проверяет структуру; выбранные starting sets и физическое размещение окончательно проверяются при world creation.

Редактор с графическим окном запускался; полноценную визуальную проверку dock/gizmos через GUI завершить не удалось из-за сбоя инструмента UI automation. Утверждения о расположении/читаемости всех элементов и ручном viewport drag здесь не делаются. Нет clean export package, проверки standalone игры без addon файлов, второго game project и измерений 100/1000/10000 markers. Это остаётся B3/B4/B7; сборка ExportRelease сама по себе этих проверок не заменяет.
