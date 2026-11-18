# Документация OpenRTS

## Как пользоваться движком и полигонами

- [Сборка и проверки](development.md) — окружение, команды, замеры и настройка игрового контента.
- [Создание и запуск карт](map-authoring.md) — YAML, редактор Godot, сборка 2D-карт и 3D bundles, запуск с записью повтора.
- [Игровые полигоны](sandbox.md) — Skirmish, управление, ИИ и диагностические сценарии 2D и 3D.
- [Генератор переходов рельефа](../tools/prepare_terrain_transitions.md) — команды и параметры атласов.
- [Сеть и повторы](../src/OpenRTS.Network/README.md) — lockstep, лобби, WAN, переподключение и поиск матчей.
- [Графика](assets/README.md) — происхождение ассетов, лицензии и подготовка ресурсов.

## Устройство и развитие

- [Архитектура](architecture.md) · [План разработки](plan.md) · [Внешние референсы](references.md).
- [Симуляция и фиксированный тик](decisions/002-simulation.md) · [Граница Core и Gameplay](decisions/023-core-and-gameplay.md) · [ECS](research/ecs-selection/README.md) · [Подключаемые механики](research/mechanic-modularity/README.md).
- [Навигация](research/navigation-layers/README.md) · [Групповое движение](research/rts-group-navigation/README.md) · [Приказы](decisions/010-orders-and-controls.md) · [Геометрия зданий](decisions/012-buildings-and-geometry.md).
- [Оружие и снаряды](decisions/011-weapons-and-projectiles.md) · [Авиация](decisions/032-aviation-and-target-categories.md) · [Способности и производство](decisions/013-actions-and-production.md) · [Экономика и захват](decisions/020-players-economy-capture.md).
- [Интеграция Godot](research/godot-integration/README.md) · [Компиляция карт](research/map-compilation/README.md) · [3D-рельеф](research/3d-terrain/README.md) · [Сетевые исследования](research/networking/README.md).

Полные списки хранятся в каталогах [решений](decisions/), [исследований](research/) и [отчётов о проверках](verification/).

## Зависимости и лицензии

OpenRTS — самостоятельная реализация под [MIT](../LICENSE). Совместимость с API, модами, форматами и реплеями OpenRA вне области проекта. OpenRTSv1 служит исследовательским референсом. GPL-код референсов в ядре OpenRTS отсутствует.

ECS использует Frent 0.9.1-beta. [Лицензия Frent](vendor/frent-license.txt) · [Решение о внедрении ECS](decisions/014-ecs-foundation.md).

MIT-библиотека DeclarativeContent перенесена из Hyperfleets с тестами и уведомлением о лицензии. Репозиторий содержит библиотеку целиком, вместе с данными о версии импорта. [Исходники](../libs/DeclarativeContent/) · [Происхождение и SHA-256](vendor/declarative-content.json).
