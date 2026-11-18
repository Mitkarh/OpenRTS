# Генератор переходов рельефа

Скрипт [prepare_terrain_transitions.gd](prepare_terrain_transitions.gd) собирает PNG-атласы для top-down и изометрии, два ресурса TileSet и текстуры вариантов для игрового представления.

## Входные данные и запуск

Запускайте команды из корня репозитория. Задайте `OPENRTS_GODOT` полным путём к исполняемому файлу Godot .NET. Для каждой фактуры нужны одноимённые `<id>.png` в [sandbox/assets/terrain](../sandbox/assets/terrain) и `<id>.tres` в [sandbox/resources/terrain](../sandbox/resources/terrain). Для собственных пар используйте ID из строчных латинских букв, цифр и дефисов, начиная с буквы.

```sh
"$OPENRTS_GODOT" --headless --path sandbox --script "$PWD/tools/prepare_terrain_transitions.gd" -- water grass grass
"$OPENRTS_GODOT" --headless --path sandbox --script "$PWD/tools/prepare_terrain_transitions.gd" -- --angular water concrete road
```

После `--` передайте две разные фактуры и при необходимости общий игровой `TerrainId`. Если опустить третий аргумент, `TerrainId` берётся из первого ID фактуры. Запуск без аргументов пересобирает штатный `grass-mud` с `TerrainId=grass` и дополнительным тайлом дороги.

Скруглённый контур используется по умолчанию. Флаг `--angular` включает исходный угловатый контур, который подходит для бетона, металла и других искусственных материалов. В обоих режимах сохраняются стыки по граням и четыре ручных тайла с прямыми диагоналями. Повторный запуск для той же пары перезаписывает её атласы и ресурсы.

## Использование в Godot

Назначьте слою `Terrain` TileSet с суффиксом `TopDown.tres` или `Isometric.tres`. Кисть **Terrains → Connect** подбирает переход по соседям, а четыре точные диагонали находятся в нижнем ряду **Tiles**. Для обеих фактур и промежуточных тайлов компилятор получает один и тот же игровой `TerrainId`. Например, вода в `WaterGrass` использует правила движения травы.

| Пара | Имя TileSet | Контур | `TerrainId` |
| --- | --- | --- | --- |
| Вода и трава | `WaterGrass` | Скруглённый | `grass` |
| Вода и грязь | `WaterMud` | Скруглённый | `mud` |
| Вода и бетон | `WaterConcrete` | Угловатый | `road` |
| Трава и бетон | `GrassConcrete` | Угловатый | `grass` |
| Грязь и бетон | `MudConcrete` | Угловатый | `mud` |

См. [редактирование и сборку карт Godot](../docs/map-authoring.md) и [сцену DiagonalTransitions](../sandbox/examples/maps/DiagonalTransitions.tscn).
