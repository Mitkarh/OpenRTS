# B6. Network и replay для карт OpenRTS

**Реализовано 18 ноября 2026.** Игровые отпечатки, локальная запись и воспроизведение через существующий StarJournal описаны в [решении 052](../../decisions/052-map-match-replays.md). [Результаты B6](../../verification/052-map-match-replays.md) подтверждают прямой матч и воспроизведение для обоих форматов и M1. Ниже сохранено исходное исследование.

**Дата:** 17 ноября 2026 года. **Основа:** статическое чтение чистого OpenRTS `afb7aef5a46bb937b39c8b63269e935c2a5b91da`, включая NET4.1–NET5 и аудит 045/046. Ниже отделены существующие возможности от необходимых изменений. Новые сетевые/редакторские запуски здесь не выполнялись.

[Проект B](stage-b-design.md) · [Общий план](roadmap.md) · [Network README](../../../src/OpenRTS.Network/README.md) · [Replay ADR042](../../decisions/042-net4-replay-recovery.md) · [Snapshot/rejoin ADR043](../../decisions/043-net4-runtime-snapshot-rejoin.md).

B6 выделен из этапа редактора. Он использует стабильную локальную сборку карты, но имеет свою приёмку. Базовая работа редактора, offline playtest и запуск из headless используют общий compiler без этой зависимости. [Общий план](roadmap.md) · [Аудит редактора](authoring-audit.md).

## Что уже реализовано

| Часть | Фактическое поведение | Польза редактору и граница |
| --- | --- | --- |
| `SimulationSession` | Owner thread, команды, pause/speed, `StepOnce`, command log и события | Локальный playtest не требует TCP или lockstep coordinator |
| `FrameExchange` | Закреплённые кадры, viewer projection и копии состояния | Тот же render/inspection path, spans только внутри lease |
| `SandboxSession` | Конкретный bridge для offline, StarSession и ReplaySession | Уже есть единая форма Frames/Completion/events/input; пока это внутренний sandbox consumer |
| `ReplaySession` | Read-only playback, отдельный owner, pause/step/speed/seek, Generation | Можно открывать и анализировать журнал; не принимает новые команды и не является редакторским undo |
| `StarJournalReader` / `StarJournal` | Общая структурная проверка и применение с World hash каждого тика | Второй reader/replay engine писать не нужно; writer и часть операций сейчас internal |
| `ReplayCheckpoint` | Ограниченный префикс завершённого журнала и восстановление исполнением истории | Recovery полезен для диагностики; это не snapshot состояния K и не быстрый seek |
| `SimulationWorld.CaptureSnapshot/RestoreSnapshot` | Каноническое runtime state на завершённой границе, matching factory и проверка hash | Будущие checkpoints/ветвление, а также проверка initializers; карта этим не компилируется |
| Match descriptor + game-owned recipe | Согласование правил/контента/сценария и создание tick-zero world | Подходит для привязки точного compiled map к игре и replay после уточнения bootstrap |
| StarSession recovery transport | Chunk transfer, epochs/tokens, backpressure и rejoin | Для B не нужен: локальный editor не должен поднимать фиктивный сетевой матч |

Источники: [общая session](../../../src/OpenRTS.Core/Hosting/SimulationSession.cs), [RTS session](../../../src/OpenRTS.Gameplay/Hosting/SimulationSession.cs), [SandboxSession](../../../sandbox/Controls/SandboxSession.cs), [ReplaySession](../../../src/OpenRTS.Network/ReplaySession.cs), [reader](../../../src/OpenRTS.Network/StarJournalReader.cs), [journal](../../../src/OpenRTS.Network/StarJournal.cs), [checkpoint](../../../src/OpenRTS.Network/ReplayCheckpoint.cs), [runtime snapshot](../../../src/OpenRTS.Gameplay/Recovery/SimulationWorld.Snapshot.cs).

## Четыре разных продукта

**Compiled map** задаёт неизменный игровой вход и возможные расстановки/старты. **Bootstrap/recipe** задаёт, как начать конкретный матч с этой картой и установленным контентом. **Runtime snapshot** сохраняет уже идущий мир на K. **Replay** задаёт историю принятых команд/controls и контрольных хешей. Авторская сцена находится ещё до этой цепочки.

Snapshot требует той же tick-zero фабрики: definitions, callbacks и статическая surface/obstacle геометрия берутся из совместимого сценария, а не загружаются как CLR-объекты из файла. Stateful механика обязана иметь schema; внешняя память AI policy не становится частью World автоматически. Следовательно, замена карты снимком не решает ни независимую поставку статических данных, ни авторскую расстановку. [IRuntimeSnapshotMechanic](../../../src/OpenRTS.Gameplay/Recovery/IRuntimeSnapshotMechanic.cs), [ADR043](../../decisions/043-net4-runtime-snapshot-rejoin.md).

## Существенное ограничение текущей идентификации

В `Sandbox.Network` descriptor получает `ContentHash` из `catalog.Summary.Fingerprint`, а `ScenarioHash` — как SHA-256 от `MatchSetupOptions.Serialize()`. В options есть Map/Mode по именам и настройки сторон/fog/скорости, но **нет digest содержимого карты**. Initial world hash дополнительно сравнивается при старте; это существующая защита совпадения исходного мира, но имя карты не обеспечивает поиск точного артефакта старого replay. [Network bootstrap](../../../sandbox/Controls/Sandbox.Network.cs), [options](../../../sandbox/Controls/MatchSetupOptions.cs), [descriptor](../../../src/OpenRTS.Network/DirectMatch.cs).

Для compiled maps требуется game-owned resolver: recipe новой версии хранит map ID **и digest**, starting set identity, content/rules и значимые simulation settings. Scenario identity строится из этих данных по определённому кодированию. При недоступном digest не выбирается «новейшая карта с таким именем». Reader прежних recipe остаётся для старых файлов со своей семантикой.

Это прежде всего изменение game bootstrap/каталога, не повод переделывать TLS/peer protocol. Текущий `MatchDescriptor` уже содержит строки ScenarioHash/ContentHash. Если достаточно их новой содержательной интерпретации, меняется версия game-owned rules/recipe; wire/journal version меняется только при реальном изменении формата. Совместимость проверяется отдельно, не предполагается по совпадению C# типов.

Recipe v3 ограничен 16 KiB; он не контейнер карты/ресурсов. Артефакт устанавливается рядом и разрешается по identity. Digest обеспечивает идентификацию/целостность, а не доверие к коду или честность результата матча. Авторские NodePath и Godot scenes в runtime snapshot/journal не записываются.

## Что переиспользовать напрямую

Первый B playtest запускается **в отдельном runtime-процессе Godot**, а не выполняет бесконечную живую симуляцию в setters `[Tool]`. Редактор передаёт выбранную build generation и настройки. В runtime используются существующие session bridge, render data и controls. Локальный запуск использует доступные паузу, шаг и перезапуск. Для B6 редактор может открыть тот же runtime в read-only replay режиме по точному артефакту. Встраивание live viewport и полной панели в editor — дальнейший consumer этапа C.

Это оставляет static authoring preview лёгким, изолирует reload C# редактора от игрового owner и не требует нового межпроцессного сетевого протокола ради первого запуска. Launcher контролирует только созданный им процесс и артефакт запуска; status/error/завершение возвращаются через небольшой host-owned результат. Нельзя выдавать закрытие окна за успешный playtest без статуса завершения.

При появлении второго потребителя минимальную общую часть `SandboxSession`/bootstrap можно вынести в Godot hosting/игровую обвязку. Не следует переносить sandbox settings и зависимость Network в Content/Core или заранее создавать общий `ISession` для всех возможных движков.

Сам editor addon может иметь дополнительный replay host модуль с зависимостью на `OpenRTS.Network`, который уже зависит только от Gameplay. Базовые authoring/compiler/reader и .NET headless такой зависимости не получают. [Network project](../../../src/OpenRTS.Network/OpenRTS.Network.csproj).

## Где требуется небольшой новый контракт

### Запись локального playtest

`SimulationSession.GetCommandLog()` возвращает принятые команды, но не полный StarJournal с per-tick hashes и controls. `StarJournal` writer internal; `Wire.Kinds` — ограниченный набор player intents, `Wire.Batch` проверяет stamp/issuer/поля и квоты. Spawn, прямое Damage, RNG Scatter и произвольные debug mutations не превращаются автоматически в поддержанный сетевой replay. [Wire](../../../src/OpenRTS.Network/Wire.cs), [StarJournal](../../../src/OpenRTS.Network/StarJournal.cs).

**Предложение B6:** ограничить записываемый playtest поддержанными игровыми intent и предоставить узкий recorder API над действующей journal-семантикой. Он получает от owner tick-zero descriptor/recipe/state/hash и затем завершённые согласованные batches/hash. Для обычного offline `Submit` потребуется явная нормализация issuer/controller/sequence; нельзя присвоить всем командам произвольные новые stamp или объявить issuer1 легальным владельцем всех сторон. Несколько локально управляемых сторон задаются игровой policy и проходят ту же проверку, что журнал.

Recorder hook вызывается после фиксации тика, не читает World с Godot thread и не навязывает Network dependency общей session. При неподдержанной команде запись отклоняется **до применения**, либо запуск заранее объявлен незаписываемым diagnostic mode. Нельзя записать неполный журнал и показать его как воспроизводимый. Существующие readers/Apply остаются общими; новый универсальный authoring-command wire format не нужен.

Если такой hook нельзя безопасно открыть без крупной переделки, B6 сначала переиспользует запись настоящего поддержанного runtime матча и открытие полученного журнала; экспорт offline log не считается завершённым, пока не закрыты metadata/hash и intent ограничения. Объём recorder уточняется по этому единственному потребителю.

### Снимок по запросу UI

Прямые `CaptureSnapshot`/`RestoreSnapshot` предназначены для owner thread. Существующие session interfaces не дают UI право брать живой World. Для checkpoint/branching понадобится явный bounded request, обработка между завершёнными тиками, ответ байтами/status и отмена, затем публикация новой generation. Сам snapshot API можно переиспользовать; такого editor request API сейчас нет.

В B обязательны restart и обычный replay seek, уже реализуемый от нуля. Ускорение seek runtime checkpoints, ветвление с новым вводом и восстановление внешней AI policy — отдельная работа C или Network, не скрытая зависимость compiler. Snapshot unsupported для stateful backend не должен запрещать обычный авторский compile или совместимый replay recovery.

## Как сбрасывать представление после seek и restart

Ручная трасса текущего пути: `ReplaySession.Seek` ставит цель → owner пересоздаёт matching tick-zero world → применяет историю и проверяет hashes до K → очищает очередь исторических событий → увеличивает Generation → публикует конечный frame. `Sandbox.Replay` при смене generation/POV сбрасывает selection, placement modes, wrecks, VFX, radar и temporal fog. [ReplaySession.Run](../../../src/OpenRTS.Network/ReplaySession.cs), [ResetReplayPresentation](../../../sandbox/Controls/Sandbox.Replay.cs).

Редакторский runtime должен использовать ту же семантику. Для C добавляются сброс активных camera/briefing cues и восстановление длительных состояний по целевому frame. Требуется согласованная привязка generation к опубликованному состоянию и приёму событий: нельзя применить старое событие к новому миру. Точная расширенная форма этого envelope — задача C, не доказанная возможность одного нынешнего счётчика.

Владение lease остаётся прежним: читающий копирует необходимые сведения до Dispose. Diagnostics в editor отправляют только нужную проекцию выбранных IDs; нельзя сохранять span из кадра до следующего UI callback. Runtime EntityId привязывается к authoring ID через receipt карты/запуска, а не через порядок rendered MultiMesh instances.

## Что не переносить в редактор

- TLS/account proofs, directory tickets, rejoin tokens/epochs и tick barrier не нужны для компиляции и локального playtest.
- StarProtocol frames не становятся форматом карты; network quotas не назначают автоматически бюджеты JSON/geometry compiler.
- RuntimeArchive с закрытыми snapshot layouts не становится универсальным serializer authoring data.
- ReplaySession остаётся read-only. Для нового ввода после K нужен отдельный branch contract с новым журналом/identity, а не вызов Submit на replay.
- Полный локальный editor debug может читать весь тестовый мир. Player POV и online режим сохраняют viewer/event filtering; публичная diagnostics не раскрывает скрытые entities и контакты.

## Приёмка самостоятельного этапа B6

1. Скомпилировать карту и начать playtest с точным recipe; журнал воспроизводится в editor-launched runtime и обычном headless по тем же hashes.
2. Пересобрать карту под прежним display ID, сохранив старый digest: старый replay открывает старый пакет либо объясняет отсутствие; не воспроизводится на новой карте молча.
3. Изменить только presentation scene: игровой журнал совместим. Изменить HP variant, порядок размещений, terrain или механику: несовместимость определяется по gameplay identity.
4. Pause/Step/Seek/POV/Restart не повторяют исторический взрыв и не оставляют phantom view; длительное состояние восстанавливается на K.
5. Проверить unsupported intent, отмену, повреждённый/незавершённый журнал, отсутствующий контент и превышение квот. Переполнение recorder не публикует «успешный» replay.
6. Запустить compiled map без Network assembly в базовом headless; отдельно проверить прямой матч и старые поддержанные replay schemas после обновления game-owned resolver.

Эти сценарии предложены к реализации; они не заменяют существующие Network проверки и не подтверждены новым запуском в рамках данного проектирования.
