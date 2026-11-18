# 050. Поставка и запуск scene-map B4

**Дата** 18 ноября 2026. B4 расширяет [компиляцию 047](047-map-compilation.md) и [редактор 049](049-godot-terrain-models-preview.md). [Результаты проверок](../verification/050-godot-scene-map-bundle.md).

`*.scenemap.json` версии 1 связывает полноценную project-сцену Godot, игровой манифест и независимую проекцию. Проекция хранится рядом как `<catalog fingerprint>-<data fingerprint>.ortmap.json`. Прежний `CompiledPath` задаёт имя указателя, заменяя окончание `.ortmap.json` на `.scenemap.json`. Каждый новый набор данных получает собственный файл. Сборка записывает проекцию атомарно, проверяет зависимости и публикует указатель последним. Незавершённая сборка может оставить невостребованный файл проекции.

Редактор сохраняет сцену и строит список SHA-256 её транзитивных зависимостей, найденных Godot. Перед публикацией он сверяет сохранённую сцену с извлечёнными игровыми данными и проверяет, что зависимости всё ещё соответствуют снимку. Зависимости, загружаемые скриптами по строкам, поставляются по правилам проекта Godot. На диске bundle использует исходную project-сцену. Правка этой сцены либо перечисленной зависимости требует новой сборки. PCK содержит снимок ресурсов на момент экспорта.

Во время игры Sandbox загружает проекцию через `MapCompiler.Read`, проверяет отпечатки и инстанцирует исходную `PackedScene` дочерним узлом матча. Сохраняются скрипты, вложенные сцены, группы и окружение. OpenRTS создаёт игровые сущности из проекции. Слой карты может задавать `VisualTextures` и отключать встроенное покрытие через `DrawCompiledTerrain`. При запуске активируется камера Sandbox, а скриптам карты доступны обычные возможности Godot для дальнейшего управления камерой. Headless использует тот же manifest reader для загрузки проекции. При работе со своим набором правил headless принимает `--mod` как обычно.

## Рабочий процесс

1. В Godot открыть `res://examples/maps/TerrainAuthoringMap.tscn`. **Build scene-map bundle** сохраняет и публикует карту. **Build and play map** запускает Sandbox с готовым манифестом. Авторские карты используют эту кнопку для запуска. F6 открывает авторский корень напрямую и показывает диагностическое сообщение.
2. Для запуска из меню указать путь к `.scenemap.json` в `Match.Maps` игрового манифеста. «Учебная карта Godot» зарегистрирована в Sandbox. Для локального запуска любого bundle доступен `--map`. Сетевой запуск scene-map запланирован для B6.
3. Для поставки разместить манифест и проекцию под `res://content/`. Пресет **Scene Map Pack** упаковывает ресурсы Godot, YAML и проекции. Каталог `artifacts/` предназначен для локальных сборок. Команда экспорта проверяет все зарегистрированные bundles до упаковки. Экспортированные ресурсы используют remap Godot, поэтому в PCK проверяются проекция и соответствие корня карты. Сверка хешей исходных файлов выполняется до упаковки. [Документация Godot по экспорту](https://docs.godotengine.org/en/stable/tutorials/export/exporting_projects.html).

```sh
./tools/dev.sh scene-map res://examples/maps/TerrainAuthoringMap.tscn
./tools/dev.sh headless --map=content/scene-maps/terrain-authoring-sample.scenemap.json --ticks=32
./tools/dev.sh scene-map-pack /tmp/openrts.pck
```

Пресет PCK настроен на macOS arm64. Для экспорта Godot должен видеть совместимый с `global.json` SDK и пакеты целевого .NET runtime. Для сборки приложения нужен установленный Godot .NET export template.

## Сборка и проверка

В панели OpenRTS **Compile map** выпускает `.ortmap.json`, а **Build scene-map bundle** выпускает `.scenemap.json` для 3D. **Build and verify map** выбирает формат по корню сцены. Skirmish использует выбранные старты и стартовый набор, fixture использует фиксированную расстановку. Сохраните 3D-сцену в проекте перед сборкой bundle.

CLI читает сохранённую `.tscn`, при необходимости импортирует ресурсы и вызывает ту же сборку. Формат определяется корнем сцены или флагом `--format`. Bundle требует корня `OpenRtsMap`. Для `OpenRtsMap2D` доступен формат `compiled`. Прежняя команда `scene-map` соответствует `map-build --format=bundle`.

| Профиль | Сборка с проверкой | Результат |
|---|---|---|
| 2D compiled map | `./tools/dev.sh map-build res://examples/maps/RockyPass.tscn --format=compiled --verify` | `res://artifacts/maps/rocky-pass.ortmap.json` |
| 3D bundle | `./tools/dev.sh map-build res://examples/maps/AuthoringMap.tscn --format=bundle --verify --start=west --start-set=content/start-sets/infantry.yaml` | `res://artifacts/maps/scene-authoring-sample.scenemap.json` и проекция рядом |

При `--verify` без `--start` skirmish использует все старты карты в порядке ID. На картах с более чем четырьмя кандидатами передайте от одного до четырёх `--start=ID`. Порядок аргументов задаёт порядок игроков. Без `--start-set` используется набор из настроек манифеста. Без `--verify` сборка проверяет игровой контент и размещённые объекты.

Проверка читает временный артефакт с диска, проверяет выбранный матч и сравнивает тик, число сущностей и хеш состояния после восьми тиков в Godot и в независимой симуляции. Godot при этом загружает 3D entry-сцену bundle, а для 2D строит карту из проекции. При ошибке, отмене или изменении исходников во время проверки предыдущий опубликованный файл сохраняется. `OPENRTS_MAP_BUILD` в CLI содержит путь, формат, манифест и результат проверки. Для повторения того же матча в отдельном headless-процессе можно передать поле `Playtest` как `--local-playtest` вместе с `--map` и `--mod`.

```sh
./tools/dev.sh headless --map=artifacts/maps/rocky-pass.ortmap.json --mod=sandbox/rocky-pass-game.yaml --ticks=8
./tools/dev.sh headless --map=artifacts/maps/scene-authoring-sample.scenemap.json --mod=sandbox/game.yaml --start-set=content/start-sets/infantry.yaml --ticks=8
```

Для установки задайте `CompiledPath` под `res://content/` и добавьте итоговый путь skirmish-карты в `Match.Maps` манифеста этой игры. ID карты должен быть уникальным в меню. Для 2D укажите `.ortmap.json`. Для 3D укажите `.scenemap.json` и сохраните рядом его проекцию. Пресет экспорта проверяет оба типа зарегистрированных артефактов. Fixture запускается напрямую через `--map` или редактор. `./tools/dev.sh verify-authoring` проверяет авторинг и обе CLI-сборки.
