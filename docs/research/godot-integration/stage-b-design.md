# Этап B — редактор карт Godot и независимая компиляция

**План:** 17 ноября 2026 года; уточнён 18 ноября. **Статус:** B0–B1 реализованы: [контракт 047](../../decisions/047-map-compilation.md), [проверки](../../verification/047-map-compilation.md). B2 добавил [опциональный scene authoring addon и извлечение 048](../../decisions/048-godot-map-authoring.md). B3 добавил [terrain brushes и Models/physical preview 049](../../decisions/049-godot-terrain-models-preview.md). B4 добавил [scene-map bundle и запуск 050](../../decisions/050-godot-scene-map-bundle.md). B5 добавил [начальные свойства 051](../../decisions/051-map-initial-properties.md). B6–B7 ещё впереди; реализованные границы закреплены в 047–051, остальные операции остаются проектом.

[Общий план B/C/D](roadmap.md) · [Network/replay и переиспользование](network-reuse.md). Источники текущего поведения — OpenRTS `afb7aef5a46bb937b39c8b63269e935c2a5b91da`; рабочее дерево при чтении чистое. Ссылки на код приведены рядом с соответствующими ограничениями.

## Пользовательский результат

Автор создаёт карту в Godot, выбирает тип из каталога, расставляет юниты/здания/neutral props, настраивает владельцев и поддержанные свойства. Видит картинки, тело и footprint, редактирует плоский terrain и старты. Свет, декоративные эффекты, брифинг и другие presentation-only сцены может делать обычными средствами Godot.

**Карта нового формата — полноценная сцена Godot плюс её игровая проекция.** При выборе карты игра запускает соответствующую сцену и скомпилированные из неё данные/поддержанные кэши симуляции. Compiler не заменяет обычные возможности сцены собственной системой оформления. M1 YAML развивается параллельно как простой simulation-facing формат. Самый ранний срез может ограничиться удобной расстановкой и экспортом данных, но B завершён только после scene-based запуска; это не смена цели на вариант A.

«Проверить», «Скомпилировать» и «Играть» используют один pipeline. При ошибке редактор выбирает объект и поле; при успехе карта регистрируется в игре. Готовые игровые данные читаются обычным .NET headless без Godot и без editor plugin. Godot runtime дополнительно загружает совместимое оформление. После playtest исходная расстановка сохраняется; изменившиеся в матче HP/позиции автоматически в неё не записываются.

Первый playtest и просмотр replay открываются в отдельном runtime-процессе с существующей игровой панелью pause/step/seek. Static preview остаётся в редакторе. Встраивание живого мира и расширенной панели диагностики в editor не блокирует B и относится к следующему потребителю C.

### Объём первой версии

Встроенный Gameplay, текущая плоская наземная модель, M1-совместимые terrain/visual IDs и кисти, skirmish/fixture. Нет произвольного mesh-to-gameplay bake, высотного поля/мостов, новых навигационных алгоритмов, анализа ИИ, универсального редактора ECS и горячей замены definitions работающего мира. Масштаб terrain samples первой версии сохраняет 1024 по действующему M1; это ограничение версии, а не физическое округление всех размещений.

Ограниченная настройка свойств входит в B, но реализуется после базовой расстановки: сначала объявленные поля и выбор типа, затем локальный вариант MaxHealth и отдельный initial HP. Редактирование любого оружия/производства/locomotor для каждого экземпляра не является условием готовности.

## Архитектура и зависимости

| Часть | Обязанности и владелец | Зависимости |
| --- | --- | --- |
| Godot authoring addon | Палитра, markers, Inspector, gizmos, undo, сохранение сцены, извлечение снимка | Godot .NET, Content; runtime preview через адаптер |
| Независимая подготовка карты | DTO документа, нормализация, каталоги, проверка, compiler/reader, gameplay identity, diagnostics | `OpenRTS.Content` → Gameplay → Core; без Godot/Network |
| Инициализация Gameplay | Обычный lifecycle и строгая проверка размещений, начальные параметры, receipt соответствия IDs | Существующие Gameplay/Core |
| Минимальный Godot runtime карты | Загрузка manifest, полноценной entry сцены карты, terrain bindings и нынешних Models; команды/кадры | Godot + Content/Gameplay, без включённого editor plugin |
| Host playtest/replay | Пауза, шаг, stop/restart, запись/просмотр поддержанного журнала | SimulationSession; Network только при использовании его replay API |

Начать C# addon в стандартном `addons/openrts_editor` существующего Godot-проекта. Source inclusion нынешнего `OpenRTS.Godot` можно сохранить на первом срезе; второй проверяемый game project определит минимальную общую поставку. Не нужны обязательные NuGet-пакеты, общий DI-контейнер и новые пустые сборки. Editor API и его compile/export configuration должны быть отделены от runtime использования: исключение markers из сцены ещё не доказывает отсутствие editor-зависимостей в экспортированной сборке.

Godot поддерживает C# plugins, требующие сборки до включения, и `[Export]` для Variant-compatible полей. Поэтому авторские Node/Resource wrappers принадлежат addon, а immutable `EntityDefinition` не изменяется ради Godot-атрибутов. [Making plugins](https://docs.godotengine.org/en/stable/tutorials/plugins/editor/making_plugins.html), [C# exports](https://docs.godotengine.org/en/stable/tutorials/scripting/c_sharp/c_sharp_exports.html).

Первый compiler обслуживает встроенный Gameplay. Игра на одном Core со своими компонентами подключит свои конкретные authoring/compile правила, когда такой потребитель появится; автоматическая поддержка произвольного мира здесь не обещается.

## Документы и единый источник истины

| Документ | Содержание | Как изменяется |
| --- | --- | --- |
| **Авторская сцена** `.tscn` / `.tres` | Размещения, стабильные IDs, порядок, исходные свойства, terrain brushes, starts, ссылки на оформление | Редактор Godot; первичный источник этой карты |
| **Независимый исходный документ карты** | Числа/IDs/упорядоченные записи, нормализованные transforms, локальные overrides, версия; без Nodes/Resources | Результат извлечения, вход общего компилятора. Можно сохранить для review и .NET CI, но он производный от сцены |
| **Скомпилированные игровые данные** | Проверенные terrain, starts/slots, ordered placements, подготовленные варианты/initializers, версии/отпечатки | Compiler Content; достаточны для игры с установленным совместимым контентом |
| **Игровая сцена карты** | Полноценная runtime `.tscn`: сохранённые Nodes/scripts/groups, Models, окружение, камера, брифинг и Godot resources | Godot build/export; игровые решения идут через OpenRTS, косметические байты не часть игрового hash |
| **Manifest поставки и source map** | Связь игровых/визуальных результатов одной сборки, provenance, диагностика ID → source | Build pipeline; source map может поставляться отдельно от игры |
| **Настройки запуска** | Выбранная карта по digest, starting set, назначения игроков, seed, tick/settings, механики | Хост; не зашиваются в общий compiled terrain |
| **Snapshot / replay** | Runtime state K либо команды/хеши истории | Существующие recovery/replay APIs, не compiler карты |

Для первой поставки предлагается простой версионированный UTF-8 JSON для независимого документа и семантических игровых данных, с явными типами/полями и детерминированным writer. Это обменный формат, не обещание ускорения. Крупные секции измеряются по памяти/размеру; если текст неприемлем, бинарная форма вводится новой версией с тем же смыслом, без заимствования wire-формата матчей. Импорт M1 и извлечение из Godot сходятся в один builder; экспорт M1 может быть диагностическим средством, но не обязательным промежуточным сериализационным кругом.

Игра может использовать независимый документ как первичный источник **другой** карты или генератора. У одной карты нет двух равноправных редактируемых копий. Если автор правит производный JSON вручную, это явный fork с новым источником, а не двусторонняя синхронизация со сценой.

## Авторская структура и первый terrain source

Одна map-root сцена содержит ветки игровых размещений, плоского terrain, стартов и косметического окружения. Извлечение читает только объявленные semantic markers и terrain resources. Обычный Mesh/CollisionShape/Area не становится игровым препятствием по наличию узла. Ссылка на объект из gameplay marker должна разрешаться в игровой ID, а не сериализоваться как NodePath.

Ветка оформления допускает обычные Godot Nodes и scripts, материалы, свет, weather/VFX и camera rigs. Игровой marker, ошибочно помещённый в исключённую ветку, даёт диагностику; его нельзя просто потерять при обходе. Gameplay markers не создают runtime entities в `_Ready` и не должны оказываться вторым спавнером после загрузки compiled map.

**Первый terrain tool:** custom brush на нынешнем `TerrainLayout`, в 3D viewport текущего host. Он непосредственно соответствует terrain/visual/quarter-turn semantics и не требует синхронной второй 2D-сцены. Палитра и brush definitions берутся из текущего контента. TileMapLayer остаётся возможным следующим source adapter для нуждающейся в нём игры, без собственного компилятора и без изменения игрового формата.

Статические игровые blockers в B представлены terrain или явными поддержанными props/footprints. Произвольные partial volumes и извлечение collider mesh не нужны первому результату. Декоративная скала может быть просто картинкой, разрушаемый камень — игровой объект с ID; compiler не решает это по внешнему виду.

## Поля, overrides и определения типов

| Свойство | Первый контракт | Приоритет |
| --- | --- | --- |
| Map ID, kind, bounds, terrain palette/brushes | Явные versioned data, границы проверяются | P0 |
| Placement ID, Type ID, owner slot/neutral, точная позиция | Сохраняются в исходнике; owner разрешается настройками матча | P0 |
| Start ID/позиция, fixture slots, skirmish reverse | Нынешняя семантика M1; свободные старты возможны | P0 |
| Видимые эффективные HP/body/weapon/locomotor | Read-only compiled view каталога, ссылка к источнику | P0 |
| Ориентация размещения | Только реально поддержанные quarter-turns; явный initializer | P1 |
| MaxHealth локального варианта | Отдельное override с флагом наследования, compile-time recipe | P1 |
| Initial HP экземпляра | Целое `1..effective MaxHealth`, по умолчанию full | P1 |
| UI label, marker display и Model binding | Editor/presentation metadata; физика не выводится из Model.Size | P1 |
| Произвольные overrides всех capabilities, runtime stat editing | Отдельные потребители; не общий Inspector внутренних ECS данных | Позже |

Выбор типа и просмотр эффективных свойств сразу полезны без writer всего YAML с наследованием. Каталоги типов остаются источником общих правил. Для первого локального variant меняется только MaxHealth: compiler строит новый immutable рецепт с сохранением остальных проверенных capabilities, получает устойчивый локальный ключ из map/variant ID и нормализованных overrides, переиспользует одинаковый вариант. Отсутствующее override остаётся «наследовать», а не копирует текущий default в сцену. Конфликт имён отклоняется.

Варианты включаются в подготовленный игровой набор и его content fingerprint; индексы типов и все references разрешаются заново согласованно. Их добавление не должно случайно оставлять старые indices в production/action references. Неизменённая карта без variants сохраняет порядок каталога и сценарные traces. Общая модель map-local definitions создаётся только в B5 вместе с этим одним потребителем.

Initial HP — состояние экземпляра, не изменение MaxHealth и не скрытая команда Damage на первом тике. После проверки геометрии initializer выполняется на owner до завершения tick-zero подготовки и публикации. Поворот также обязан использовать реальную ориентацию footprint/gameplay; поворот картинки отдельно не считается поддержкой. Нынешний map parser этих полей **не имеет**: потребуется ограниченное расширение типизированного входа и инициализации, а не молчаливое добавление YAML-ключей. [GameMap](../../../src/OpenRTS.Content/Maps/GameMap.cs), [MapMatch](../../../src/OpenRTS.Content/Maps/MapMatch.cs), [UnitDefinition](../../../src/OpenRTS.Gameplay/Definitions/UnitDefinition.cs).

## Координаты, IDs и порядок

Игровая плоскость — X/Y, Godot X/Z; `(godot.x, godot.y, godot.z)=(X,Elevation,Y)/1024`. Первая capability карты — явно `ground-plane` данной версии, Elevation игровых размещений равна нулю. У mesh, обозначенного gameplay-геометрией, высоты/перекрытия вне этого режима отклоняются. Декоративные объекты могут иметь произвольные 3D transforms, если не претендуют на игровую опору.

Map root задаёт систему координат без scale/rotation. Parent transforms размещений разрешаются относительно этого root, затем проверяются: конечные значения, допустимые bounds, scale/mirror/pitch/roll и поддержанная ориентация. Произвольное округление angle к ближайшему допустимому не выполняется. Позиция преобразуется в целую один раз по фиксированному правилу округления версии; редактор показывает итог. Привязка brush к cell не округляет точные unit placements. Для повторных открытий сохраняется целочисленное значение, а не накапливается float error. [Численная основа](../../../src/OpenRTS.Core/SimulationTypes.cs).

Каждый placement/start/именованный brush instance имеет постоянный authoring ID. Instance prefab получает свой контекст; конечный ID составляется из instance ID и локального ID, а не из пути узла. В B2 команда Duplicate addon создаёт новые IDs в одной undo-транзакции; после native Duplicate/Paste конфликты чинятся явной Repair action с undo. Reload/reparent внутри контекста/rename сохраняют IDs. Compiler выявляет дубли, но не исправляет их случайными GUID во время сборки.

Порядок размещений — отдельная сохранённая последовательность. Можно реализовать её через уникальный order key и явную команду перестановки; равные/неопределённые keys требуют ошибки или детерминированного editor repair с undo **до** компиляции. SceneTree не задаёт этот порядок. При YAML-импорте исходная последовательность сохраняется. Порядок brush stamps также семантически значим.

При запуске сохраняется нынешнее правило: стартовые силы по ordinal slot IDs и порядку записей набора, затем объекты карты. Не сортировать placements по ID ради красивого файла. Создание выдаёт receipt `authoring ID → EntityId`; ссылки разрешаются после полного успешного размещения. Это производная диагностика, не замена allocator. Построенные в матче объекты могут не иметь authoring ID. [Текущий Prepare](../../../src/OpenRTS.Content/Maps/GameMap.cs).

## Алгоритм извлечения и компиляции

**Вызов:** Validate/Compile/Play/F6, build command и export используют одни операции. Автоматические setters выполняют только локальные проверки и обновление preview; полная компиляция не запускается на каждый mouse motion.

1. **Снять revision автора.** На main thread Godot прочитать declared markers, transforms и ссылки в независимые значения. Не передавать Node/Resource/ECS refs в compiler. При поэтапном чтении большой сцены проверить, что revision не изменилась; иначе отбросить неполный снимок и предложить/запустить новый.
2. **Нормализовать.** Проверить версии, IDs, owner/type references, координаты и order. Развернуть поддержанные brushes и групповые authoring instances. Источники ошибок сохранить отдельно как map/entity/property locators. Декоративная графика не попадает в игровую геометрию.
3. **Подготовить definitions и игровую карту.** Общий Content builder принимает такой же вход от M1 importer. Скомпилировать ограниченные variants, разрешить type/terrain references, создать независимые неизменяемые данные. Не генерировать заранее route fields/AI atlas. Размеры/суммы/количества проверять до крупных выделений памяти.
4. **Проверить статические ограничения и выбранный запуск.** Сначала структура, terrain и нейтральная/fixture расстановка; затем временный `MapMatch` с конкретными выбранными start set/slots/seed/settings на одном owner. Применить те же строгие placement rules и initializers, получить hash tick0 и ID receipt, освободить временный мир. Нельзя выдать удачный запуск одного набора за совместимость всех наборов и стартов. При реальном запуске комбинация проверяется повторно.
5. **Выпустить результаты.** Записать semantic map, fingerprints, presentation bindings и source map в временную build generation. Проверить полноту, зависимости и сохранность исходной revision. Публиковать manifest последним, ссылаясь на завершённые неизменяемые файлы по digest. Старый результат остаётся доступным как явно старая версия, не как успешная новая сборка.
6. **Зарегистрировать.** Добавить/обновить запись карты в явном каталоге/manifest игры. Просмотр файловой папки сам по себе не обещает видимость карты в текущем меню: нынешний sandbox имеет настроенный список карт. Fixture получает собственный путь запуска, не маскируется под skirmish.
7. **Играть.** Хост открывает именно опубликованный digest, разрешает установленный контент и настройки, создаёт новый мир обычной фабрикой. Godot загружает связанное оформление; headless игнорирует его. Исходные markers не остаются runtime спавнерами.

Compiler может работать на worker после копирования входа. Созданный им временный мир принадлежит этому worker до Dispose; UI не получает живых refs. Cancel проверяется между фазами и в поддержанных поисках. Произвольный game callback нельзя обещать принудительно прервать; он обязан соблюдать заданный cancellation contract. Failed/cancelled результат не публикуется.

### Валидация и диагностические статусы

Нужны `uncompiled`, `dirty`, `compiling`, `failed`, `ready` и отдельный playtest lifecycle. Source revision/job generation проверяются перед применением ответа: поздний успех старой компиляции не заменяет новый результат. Edit во время compile помечает новую revision; запущенный playtest продолжает работать со старым pinned input до restart.

Diagnostic содержит код, severity, map/authoring ID, поле и описание; Godot добавляет locator узла, headless — путь файла/ID. Ошибки: неизвестный type/slot, duplicate ID/order, неподдержанная геометрия, collision/body/footprint вне границ, несовместимый вариант, stale dependency, повреждённый формат, незавершённая сборка. Игра не исправляет их тихим сдвигом юнита или сменой owner.

Структурная валидность карты и успешность конкретной конфигурации матча — разные результаты. Preview выбирает явный start set и назначения; дополнительный прогон всех разрешённых комбинаций можно запускать отдельно. Анализ баланса стартов/связности ресурсов относится к будущему расширенному compiler, не к P0.

## Identity, зависимости и совместимость

| Идентификация | Значимые данные | Что исключается |
| --- | --- | --- |
| Map gameplay digest | Версия/geometry capability, bounds/scale, terrain, IDs/references, semantic order, slots/starts, variants и initial state | Свет, материалы, NodePath, editor selection, thumbnails |
| Gameplay content fingerprint | Действующие definitions, оружие/locomotors и map-local variants; версии правил | Конкретные ресурсы Models и качество VFX |
| Scenario/bootstrap identity | Map digest + content/rules + starting set identity + выбранные slots/teams/seed/tick/settings и mechanics policy | Адрес редакторского файла, настройки окна и камеры |
| Presentation build identity | Runtime scene, bindings и ресурсы; связь с совместимой картой | Не используется как доказательство равных игровых правил |
| Provenance/source freshness | Исходные сцены/Resources и консервативно полный набор зависимостей извлечения | Не подменяет semantic digest |

Хеш считается по канонической игровой проекции с фиксированными числами/порядком, не по произвольным JSON bytes или Godot ResourceUID. Словари можно сортировать по ordinal key; порядок placements, очередей и stamps сохраняется. Широкая инвалидация каталога на первом срезе допустима, пропущенная зависимость — нет. Изменение visual-only ресурса может пересобрать presentation package, сохранив Map digest и игровой trace.

Recipe/replay хранит стабильные references/digests и параметры, а не весь файл карты в нынешнем recipe ≤16 KiB. Фабрика должна разрешить **точную** версию артефакта; новая карта под старым ID не становится совместимой. Если пакет не установлен, загрузка сообщает его identity и отказывает. Старые артефакты для доступных replay не перезаписываются; retention/удаление остаётся явной политикой игры. Старые journal/bootstrap schemas продолжают читаться своим reader. Подробности текущих ограничений — [Network reuse](network-reuse.md).

В B достаточно одной версии semantic payload. Неизвестные обязательные capabilities/поля/версии отклоняются; forward compatibility не означает молча отбрасывать игровую информацию. Обязательны верхние byte/count/depth/coordinate budgets, отсутствие trailing data и относительных выходов за package roots. Конкретные квоты compiler устанавливаются в B0 по текущим пределам и baseline; network limits не копируются без обоснования.

### Эволюция под будущие эпики

Первая версия явно плоская; не кодирует обещание «XY достаточно для любой будущей карты». Позднее новый формат сможет добавить поверхность, объёмы, переходы и соответствующие цели. Elevation не используется как SurfaceId. Нынешний world/hash не расширяется пустыми компонентами ради этой возможности.

Навигационные/ИИ-секции будут производными от semantic input и версий потребителя. Их отсутствие либо компенсируется тем же builder до матча, либо ведёт к явно выбранному совместимому режиму; они не меняют правила автоматически. Механики разрушения не запекаются в вечный terrain: prop placement остаётся снимаемым игровым объектом. [MC contracts](../map-compilation/plan.md), [3D contracts](../3d-terrain/contracts.md).

## Представление, export и optional plugin

Runtime loader при выборе новой карты открывает её полноценную entry PackedScene и закреплённый semantic artifact из manifest. Сохраняются структура, scripts и композиция исходной сцены; сборка исключает editor-only markers или переводит их в неисполняемые metadata, не создавая второй спавнер. `SpriteWorldView`, Models и effects могут оставаться одной из веток такой сцены. Декомпозиция окружения в subscenes — выбор автора, не обязательное ограничение формата. Отсутствующая entry scene — отдельная ошибка Godot-хоста; headless явно запускает только semantic projection, а не другую карту.

Один preparation path вызывается editor button, Play/F6, build CLI и export. Для внешней сборки предоставляются два понятных шага: Godot .NET extraction и обычная .NET compile/validation; из уже сохранённого независимого документа второй шаг не требует Godot. EditorExportPlugin помогает упаковке/проверке, но его callback сам по себе не покрывает F6 и обычный запуск. [Export plugin](https://docs.godotengine.org/en/stable/classes/class_editorexportplugin.html).

Plugin можно отключить/не устанавливать в runtime проекте: compiled карта продолжает играть и участвовать в replay. Без плагина редактирование исходной authoring сцены и её новая сборка не обещаются. Внешний headless не должен загружать Godot assemblies; runtime export проверяется без editor APIs и с чистым `.godot` cache.

Undo выполняется над авторскими данными: placement, ID allocation, order, brush stroke и Inspector override меняются одной транзакцией. Во время игры source и runtime существуют отдельно. Перемотка replay не является undo сцены; snapshot не восстанавливает авторские комментарии и prefab связи. [Undo manager](https://docs.godotengine.org/en/stable/classes/class_editorundoredomanager.html).

## Приоритизированный список фич

P0 — первый полезный сквозной результат; P1 — завершение этапа B для выбранных свойств и существующих хостов; P2 — расширения по реальному запросу.

| Приоритет | Фича | Критерий готовности |
| --- | --- | --- |
| P0 | Независимый документ, общий M1/Godot compiler/reader и версия | Одна семантика без двойных placement правил, headless из результата |
| P0 | Опциональный addon, root/markers, type palette и Inspector | Создание/сохранение карты без C#-правки сценария |
| P0 | Stable IDs, явный порядок, multi-select, duplicate/delete/undo | Rename/reparent/reopen не меняют игровые данные |
| P0 | Плоский terrain brush, visual layer, starts/slots | Корректные M1-совместимые данные и независимый preview |
| P0 | Sprite preview + body/footprint, source diagnostics | Ошибка показывает игровой контур и исходный объект |
| P0 | Validate/compile/run, generation и атомарный manifest | Ошибочный/stale build не становится новым playable результатом |
| P0 | Регистрация и локальный playtest | Карта запускается из редактора и как установленная карта |
| P1 | Quarter-turns, MaxHealth variant, initial HP | Эффективные definitions/initializer входят в identity и одинаковы в обоих хостах |
| P1 | Godot окружение и чистый runtime/export | Свет/погода/брифинг сохраняются без editor markers и игрового влияния |
| P1 | Network/bootstrap identity и replay из editor | Точный артефакт, пауза/шаг/seek/POV, нет второго формата повторов |
| P1 | Масштаб, clean build и второй game project | Проверены 100/1000/10000 placements и различные размеры terrain, ограничения зафиксированы |
| P2 | Сложные authoring prefabs, дополнительные source adapters, расширенная панель типов | Отдельный потребитель и общая подготовка без нового runtime |

## Этапы реализации B0–B7

### B0 — зафиксировать первый контракт и fixtures

**Выполнен:** ground-plane v1, квоты, порядок/IDs и fingerprints зафиксированы в 047. Добавлен независимый tiny fixture, сохранён исходный M1 baseline четырёх карт и 33 состояний каждой; отдельная проверка отвергает неподходящий start set. Измерения Debug — одиночные load/alloc samples, не benchmark масштабирования.

Согласовать поля первой flat-map версии, порядок/ID, единицы и quotas. Подготовить tiny fixture и skirmish с нейтральным объектом, двумя стартами и кистью; ещё один пример — хорошая карта с недопустимым выбранным стартовым набором. Зафиксировать baseline нынешнего M1→World, время/память загрузки и hash журнала. Выход: schema/examples и воспроизводимый baseline, без нового pathfinding и без 3D terrain. Если неоднозначна игровая семантика поля, исключить его из v1 до определения, не угадывать.

### B1 — независимая подготовка до UI

**Выполнен:** MapSource/MapLayer, общий MapCompiler, канонический JSON source/compiled reader/writer, атомарная запись и headless compile/run. M1 использует тот же builder и прежний MapMatch lifecycle. Здесь ещё нет сцены/manifest, Godot extraction, variants и navigation/AI caches; не считать это готовым продвинутым scene-map форматом игры.

В Content выделить общий типизированный input/builder из реального M1 loader; реализовать семантический reader/writer и map digest, исходную диагностику и exact content dependencies. Существующие M1 источники проходят тот же initialization path. Выход: исходный M1 и compiled input дают один tick0/журнал, повреждённый/неподдержанный файл отвергается, headless не зависит от Godot/Network. Это минимальная MC1; preload маршрутов не входит.

### B2 — authoring addon и извлечение

**Выполнен:** map/object/start/group C# wrappers с Inspector, палитра каталога, простые viewport gizmos, explicit IDs/контекст prefab/order, undoable add/duplicate/repair, сохранение обычных сцен. Snapshot извлекается на owner thread; общий compiler работает на worker, cancel/stale/plugin disable не заменяют artifact. Есть sample с тремя типами и двумя starts, Godot/editor checks и запуск результата headless. Native Duplicate/Paste требует явного Repair при конфликтующих IDs; автоматического переписывания exported properties внутри чужой editor action нет. На срезе B2 terrain был однородным; B3 ниже добавил кисти. Подробности B2 — 048.

Добавить root, placement/start wrappers, Inspector, IDs/order, undo и сохранение; извлечение snapshot в input B1. Выход: три готовых типа размещаются в обычном Godot viewport, сохраняются, компилируются и открываются headless. Проверить duplicate/subscene context/reparent, отмену и неактуальный ответ worker. Начальный preview может быть простым; полный editor не нужен до сквозного результата.

### B3 — terrain и качественный preview

**Выполнен:** отдельные terrain/visual и shaped кисти через TerrainLayout, один undo на drag, сохранённые strokes/quarter-turns, transient SpriteWorldView/Models и TerrainView preview. Gizmos показывают Body и sparse footprint без вывода физики из Model.Size; есть semantic ID selection и выбор источника ошибки. Публикация addon проверяет authored objects через прежние placement rules. [Контракт 049](../../decisions/049-godot-terrain-models-preview.md), [проверки](../../verification/049-godot-terrain-models-preview.md). Расширенный map bake, большой editor benchmark, entry loader/export сюда не включены.

Подключить плоские brushes/visual bindings, bounds/footprint/body gizmos, выбор по semantic ID и ошибку по полю. Использовать нынешние Models для узнаваемых объектов. Выход: редактируемая terrain-карта и независимые visual/terrain изменения, undo одного brush stroke, неправильная геометрия не проходит. Если полные preview Nodes тормозят, сравнить облегчённые markers и batches выбранных/видимых объектов; не менять Frent ради editor FPS.

### B4 — весь путь поставки и игры

**Выполнен.** Editor и CLI публикуют scene-map manifest с проекцией. Sandbox запускает bundle из меню и через явный запуск из редактора. Headless читает проекцию по тому же manifest. Для поставки добавлен проверяемый экспорт PCK. [Контракт 050](../../decisions/050-godot-scene-map-bundle.md).

Объединить Validate/Compile/Play/F6/CLI/export, generation manifests и регистрацию в карте игры. При выборе карты загрузить её полноценную entry сцену с сохранением scripts/groups/композиции и закреплённый semantic artifact. Выход: опубликованный flat-map bundle открывается из меню/fixture runner, а его игровая проекция — обычным .NET headless; editor plugin выключен, clean import/export проходит. Если автоматический запуск нельзя надёжно остановить до компиляции, использовать явный launcher с проверкой manifest и блокировать stale run, не молча играть старую карту.

### B5 — ограниченные редактируемые свойства

**Выполнен.** Inspector задаёт поворот footprint, начальные HP и именованный map-local вариант MaxHealth. Вариант наследует тип и Model, а компилятор проверяет действующий каталог. Карты с новыми свойствами используют JSON v2. [Решение 051](../../decisions/051-map-initial-properties.md).

Добавить quarter-turn и один map-local MaxHealth variant, отдельный initial HP и их UI/initializers. Проверить references, default inheritance, shared definition isolation, fingerprints и replay bootstrap identity новой карты. Выход: два одинаковых базовых юнита с разными начальными HP и один усиленный вариант дают ожидаемые состояния во всех хостах. При сложной variant-компиляции сначала закончить named map-local variant вместо произвольного override, сохранив тот же явный scope правки.

### B6 — playtest, Network и replay

Переиспользовать owner/frames/events, pause/step и ReplaySession. Обновить game-owned recipe/resolver/ScenarioHash для точного map digest и content settings, не переписывая старые журналы. Запись поддержанных offline playtests получает узкий доступ к существующей семантике журналов; неподдержанные debug commands отвергаются до записи. Выход: записанный playtest загружается из editor, seek сбрасывает VFX/selection, карта работает в прямом матче; sockets не нужны для локального preview. Snapshot checkpoint/branching не входит в обязательный B. [Подробности](network-reuse.md).

### B7 — масштаб и внешний потребитель

Проверить открытие/извлечение/компиляцию/память и idle/multi-select/undo для 100/1000/10000 placements, terrain 128²/512²/2048² при существующих лимитах. Разделить стоимость чтения, preview, validation, World.Step, кадров и hash. Ввести реальный бюджет на целевой машине и зафиксировать ограничения, не объявлять 10000 сложных actors готовыми по числу markers. Подключить addon/bundle в отдельный Godot game project, например OpenRTS.CnC, сохранив лицензионные границы и проверив отсутствие editor-зависимостей у headless. Если нагрузка выходит за бюджет, ограничить поддержанный authoring профиль и выбрать измеренную оптимизацию.

## Сквозные критерии приёмки

| Свойство | Существенный сценарий |
| --- | --- |
| Паритет | M1/compiled/Godot/headless, один журнал, workers 1/4, разные render FPS |
| Идентичность | Rename/reparent/reopen и только свет не меняют game digest; порядок/stats/collision меняют |
| Размещение | Circle/box и разреженный footprint, граница, конфликт brushes, разные start sets/owners |
| Отказы | Cancel, malformed/trailing input, duplicate ID, unsupported height/transform/version, missing exact content |
| Актуальность | Linked scene/catalog/brush/компилятор изменены, поздний ответ старой job, новый edit во время playtest |
| Recovery | Точный артефакт и recipe, неверная версия отклонена, seek/POV не сохраняют старый VFX/state |
| Lifecycle | Plugin disable, закрытие сцены, C# rebuild, stop/restart и export не оставляют owner/ресурсы |
| Опциональность | Установленная карта играет без editor addon; .NET headless не грузит Godot/Network |

Реализация всего B завершена после B7 для заявленного профиля. Независимые проверки идут через `tools/dev.sh test`, Godot authoring/editor — через `tools/dev.sh verify-authoring`; QA и измерения оформляются отдельно. Выполнение B0–B5 не означает завершения оставшихся срезов.
