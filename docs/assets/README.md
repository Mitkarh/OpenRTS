# Набор графики полигона

## 3D-полигон

В [AssetShowcase.tscn](../../sandbox3d/AssetShowcase.tscn) собраны четыре здания и четыре юнита. Для просмотра можно открыть сцену через `./tools/dev.sh editor3d res://AssetShowcase.tscn` и запустить её клавишей F6. Размеры и повороты экземпляров в витрине заданы для сравнения силуэтов.

| Роль | Ресурс в `sandbox3d/assets` | Происхождение |
| --- | --- | --- |
| Завод, электростанция, казармы, нефтепереработка | `kenney/factory.glb`, `power-plant.glb`, `barracks.glb`, `refinery.glb` | `building-m`, `building-e`, `building-b`, `building-c` из [Kenney City Kit (Industrial) 2.0](https://kenney.nl/assets/city-kit-industrial). Названия файлов адаптированы для полигона. GLB используют общий `Textures/colormap.png`. |
| Танк и гусеничный БТР | `quaternius/tank.glb`, `apc.glb` | `Tank2.glb` и `Tank3.glb` из [Animated Tanks Pack](https://quaternius.com/packs/animatedtanks.html). Взяты из [публичного зеркала GLB](https://github.com/trebeljahr/quaternius-showcase/tree/main/public/glb/tank_pack). |
| Инженерная машина | `quaternius/engineer.glb` | `Tank4.glb` из того же зеркала. В Blender 5.3 удалён меш `Tank_Gun`, применён масштаб оставшегося `Tank_Turret` и переэкспортированы четыре клипа гусениц. [Исходник Blender](sources/sandbox3d-engineer.blend) · [Папка оригинальных .blend](https://drive.google.com/drive/folders/1MsH24X-roZs9c_UILBbchk2Ue5cMeE9A). |
| Пехотинец | `quaternius/Infantry.tscn` | Манекен и 46 клипов из [Universal Animation Library Standard](https://store.godotengine.org/asset/quaternius/universal-animation-library/). [Пистолет Quaternius](https://poly.pizza/m/J3i9KDQ3kt) закреплён на `DEF-hand.R`. При запуске сцена проигрывает `Pistol_Idle`. |

Модели распространяются по CC0-1.0. Уведомления авторов лежат рядом с GLB в [каталоге Kenney](../../sandbox3d/assets/kenney/LICENSE.txt) и [каталоге Quaternius](../../sandbox3d/assets/quaternius/LICENSE.txt). Для инженерной машины проверена геометрия с однотонными материалами. В исходном кузове и гусеницах есть раздельные вершины на швах и скрытые пары граней, UV-развёрток нет.

### SHA-256 исходников

| Файл | SHA-256 |
| --- | --- |
| `kenney_city-kit-industrial_2.0.zip` | `5b381164e5760f3830a2dbee43b972deee38b2a695d091b56e238ab2910c96d2` |
| `Universal_Animation_LibraryStandard.zip` | `18ff1a7215f4852b320203e8aaf02a1578b5c8eef9027fbaedfcedc7b85a3ac2` |
| `Tank2.glb` | `d46acaeb563f401bfb389bd7708342f97d0cd62f9b2c6dc88f8537be8c85ed80` |
| `Tank3.glb` | `1763818d11cb5cef32dcbe9cf52af108710310c034591e9bdf4555847e73e979` |
| `Tank4.glb` | `dd99da632fd89cae38aa6bc4af4179364bb592f87fac3e2abac4c60af55f2f1a` |
| `pistol.glb` | `4622ab2909aa0f4e88b74a13f52f9e28183a6ff5fca5896fc7e98d44008f2148` |

## 2D-полигон

16 ноября 2026 добавлены инженерный конвертоплан, конвертоплан снабжения и тяжёлый четырёхроторный транспорт. [Промпты, источники и SHA-256](air-support-art.json) · [Техническая нормализация через Godot](../../tools/prepare_air_support_art.gd). Встроенный imagegen создаёт RGBA-источники, нормализация сохраняет изображение и альфу, укладывает их в 1024×1024 и создаёт SpriteFrames. Роторы используют существующий отдельный анимируемый слой.

Сгенерирован 4 ноября 2026 года встроенным `image_gen`, затем скопирован в проект. Внешние игровые ассеты не заимствовались. [Промпты, происхождение и SHA-256](generated-art.json).

- `sandbox/assets/units`: семь RGBA PNG — два пехотинца, корпус БТР, две его башни, корпус/башня танка. Вид сверху; рисунок поворачивается адаптером. Для ракетной башни используется первый вариант: уточняющая правка перспективы не прошла фильтр сервиса.
- `sandbox/assets/terrain`: семь непрозрачных PNG — road, grass, mud, water, forest, swamp, mountains. Пока один рисунок на тип, без переходных тайлов между типами.
- `sandbox/resources/units`: SpriteFrames ресурсы с последовательностями stand/travel/shoot. У пехоты travel содержит четыре кадра, stand/shoot — нейтральный кадр того же цикла. У техники пока по одному рисунку; выстрел дополняется процедурными эффектами.
- `sandbox/resources/terrain`: Texture2D/AtlasTexture ресурсы. `.png.import` фиксирует mipmaps для масштаба камеры; `.godot` в репозиторий не входит.

Первоначальные исходники 1254×1254. Дополнительные листы ходьбы 2172×724 сохранены в `sources`; игровые атласы `*-walk.png` — 2048×512. [Промпты и SHA-256 ходьбы](infantry-walk.json), [скрипт нормализации](../../tools/prepare_infantry_walk.py), [предпросмотр исходной окраски](infantry-walk-preview.gif). Встроенный image_gen использован для генерации и правок; скрипт выполняет техническую нарезку/выравнивание, без рисования новых поз. Альфа — прозрачность, magenta-панели RGB — командная окраска. Нормированные pivots и размеры в models.yaml учитывают, что центр вращения башни не совпадает с центром canvas. Ресурсы можно заменить самостоятельно; Unit и симуляция от файлов картинок не зависят.

Стандартный `tools/dev.sh run` импортирует ресурсы перед запуском, поэтому свежему checkout не нужен заранее подготовленный импорт-кэш. Headless сценарий `--showcase` читает units.yaml и terrain.yaml, без графических ресурсов.

После замечания о мигании ракетчика добавлен целый лист `sources/rocketeer-walk-exposure-final.png`, подготовленный встроенным image_gen; прежние источники сохранены. Его использует скрипт нормализации. История промптов/замены и текущие SHA-256 находятся в infantry-walk.json. [Сравнение и проверка яркости](../verification/010-terrain-locomotors.md). С N1 headless showcase также загружает соседний terrain.yaml: графика по-прежнему не нужна.

На этапе оружия добавлены инженерная машина, комбинированная пушечно-ракетная башня и основание ДОТа. [Промпты/исходники/SHA-256](weapon-stage-art.json), [техническая нормализация](../../tools/prepare_weapon_art.py). Источники встроенного imagegen сохранены в sources; игровые RGBA нормированы в 1254×1254 с сохранением пропорций и альфы. Базовая гранатомётная пехота временно использует рисунок автоматчика. Headless загружает также weapons.yaml; effects.yaml остаётся визуальным.


N2 добавляет `assets/buildings`: семь зданий, отдельный радар, общие руины, деревья/камни/скалу/обломки, трёхкадровые полосы двери и сварки. [Промпты, исходники, история открытых проёмов и SHA-256](buildings-art.json), [воспроизводимая техническая нормализация](../../tools/prepare_building_art.py) (Pillow). Статические рисунки — RGBA1024² с единым alpha-bbox и fit928 без увеличения; полосы —1536×512 с общей геометрией кадров. Размер в мире задают Model объявления отдельно от физических footprint. Состояния повреждения пока tint, работы — pulse/сварка, разрушения — общий отдельный рисунок. Встроенные двери исходных зданий заменены на открытый проём, поверх которого адаптер рисует закрывающий/открывающий слой. Art не добавляет внешних лицензированных игровых ассетов.
