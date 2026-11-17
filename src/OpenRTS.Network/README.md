# OpenRTS.Network

NET1–NET4.1, включая WAN pipeline и multiplayer lifecycle: прямой клиентский lockstep, .NET10 / MIT. Зависимости Network → Gameplay → Core; Godot/Content не требуются, новых NuGet пакетов нет.

`DirectMatch.Run` принимает подключённый TcpClient, player1/2, immutable MatchDescriptor, фабрику tick-zero SimulationWorld, callback ввода на будущий тик и stream журнала. Run блокирует поток-владелец до EndTick либо согласованного Leave в live режиме; сокет и мир освобождаются им, журналом владеет вызывающий код. Cancel и deadline прерывают сетевое ожидание. Команды передаются pipeline; checkpoints сверяются периодически, финальный hash обязателен; `DesyncException.Tick` обозначает первый проверенный расходящийся тик. `MatchJournal.Replay` воспроизводит завершённый журнал с исходной фабрикой и проверяет все hashes.

Поддержаны обычные RTS player intents, включая способности, производство, стройку, транспорт, upgrade/transform и авиацию; дополнительные поля имеют явный wire format3 (player intent IDs/encoding сохранены из format2). Stamp назначает сетевой owner, а не offline Session.Submit. Неподдержанные поля/команды отвергаются. Детали текущего протокола: [решение038](../../docs/decisions/038-net2-live-direct-matches.md); исходный NET1 — [решение037](../../docs/decisions/037-net1-direct-lockstep.md).

## Игра через Godot (NET3)

`./tools/dev.sh run` → «Создать прямую игру» либо «Подключиться». Хост задаёт адрес своего интерфейса/`0.0.0.0`, гость — реальный IP хоста и тот же порт. Host выбирает карту, режим, стартовые точки, fog, альянсы и D; гости и зрители получают их автоматически. После открытия соединения хост передаёт SHA-256 pin по отдельному каналу; гость вводит его и выбирает свой свободный слот2..N. В лобби каждый нажимает «Готов», хост — «Запустить матч». Изменение состава/контроллера снимает готовность всех. Host может менять карту и параметры в лобби; Ready всех снимается.

`StarSession` обслуживает2–8сторон (sandbox до4), full-world client simulation, pinned TLS и клиент-хост как координатор. В supply доступны начальные AI slots и takeover отключённых сторон на общей паузе. Только хост формирует команды ИИ, клиенты применяют общий журнал. В меню доступны чат и общая Pause/Resume; локальное открытие меню не ставит мир на паузу. Leave гостя переводит его слот в None и ставит оставшихся на общую паузу; можно продолжить без нового ввода этой стороны или включить ИИ. Leave/Stop хоста завершает матч для всех. Потеря хоста прерывает игру. Уже принятые приказы выбывшей стороны не отменяются и армия не уничтожается.

Журналы находятся в Godot `user://network`. Новый `StarJournal.Replay` v3 проверяет команды, control revisions и каждый world hash; журналы оставшихся peers побайтно одинаковы. Он не совместим со старым `MatchJournal` v3. Вход/rejoin в идущую игру, snapshots, host migration, изменяемая дипломатия/shared vision и surrender с поражением пока отсутствуют. Зрители подключаются до Start и не входят в барьер тика. [Контракт040](../../docs/decisions/040-net3-multiplayer-lifecycle.md), [проверки](../../docs/verification/040-net3.md).

`./tools/dev.sh verify-network` — два реальных headless Godot процесса. `./tools/dev.sh verify-network --net3 --spectator` — четыре процесса (три игрока и зритель), четвёртая сторона под ИИ, Ready/Start, команды/production, локальное меню, общая пауза, Stop и replay. `--visible --output=/tmp/openrts-network-ui` сохраняет оконные скриншоты. `dotnet run --project tests/OpenRTS.Network.Tests -- --net3` — отдельные многопользовательские проверки, включая8human и асимметричный WAN.

NET2 двухсторонний pipeline сохранён в DirectMatch/DirectSession для диагностических потребителей. [Контракт039](../../docs/decisions/039-net2-wan-pipeline.md), [измерения](../../docs/verification/039-net2-wan.md): D тиков вперёд, checkpoints каждые H, очереди по32frames и bounded verification lag. Звезда NET3 переиспользует транспортные границы, но отправляет итоговые записи всех сторон и проверяет hash каждого тика на клиентах; численные WAN-результаты DirectSession не являются её benchmark.

`./tools/dev.sh verify-wan --output=/tmp/wan-model.json` — задержки, jitter, модель recovery после loss, bandwidth и slow clients. `./tools/dev.sh verify-wan --netem --output=/tmp/wan-kernel` — два TLS процесса и настоящие kernel drops в одноразовом Linux-контейнере (нужен Docker/OrbStack; test-only image iproute2). Сетевые правила хоста не меняются, контейнер удаляется. `--quick` сокращает real matrix до одного профиля. Оба peer-журнала проверяются offline. `DirectSession.Statistics` даёт immutable локальные метрики; они не входят в игровой hash.

## NET4.1: повторы и восстановление через историю

Новые StarJournalv3 содержат полный MatchDescriptor и game-owned recipe. `StarJournal.ReadInfo` читает пролог, `Inspect` проверяет структуру целиком, `Replay` проверяет World hash каждого тика. Старый StarJournalv2 читается через Replay только с явно переданными descriptor/factory. Новые файлы sandbox открываются из главного меню → Повторы: Play/Pause, шаг, скорость, seek и POV; управление игровыми сторонами запрещено.

`ReplayCheckpoint.Capture(journal, tick, output)` сохраняет ограниченный префикс завершённого журнала, `Restore(input, factory)` возвращает RecoveredReplay с живым World. Caller владеет Dispose и продолжает работу на том же потоке. Это история O(K), не runtime snapshot; AI policy/соединения не восстанавливаются. По умолчанию64MiB/100000тиков/15с catch-up. [Контракт042](../../docs/decisions/042-net4-replay-recovery.md), [проверки](../../docs/verification/042-net4.md).

`./tools/dev.sh verify-replay --visible --output=/tmp/openrts-replay` записывает настоящий TLS-матч и проверяет его проигрывание через Godot UI. `dotnet run --project tests/OpenRTS.Network.Tests -- --net4` проверяет recovery/перезапуск/повреждения. Runtime schemas и сетевой reconnect остаются NET4.2/4.3.

## Запуск конечного headless-стенда

Сначала `./tools/dev.sh build`. Из корня репозитория в двух терминалах:

```sh
dotnet run --project tests/OpenRTS.Network.Tests --no-build -- --host=127.0.0.1 --port=17771 --journal=/tmp/net1-host.log
dotnet run --project tests/OpenRTS.Network.Tests --no-build -- --join=127.0.0.1 --port=17771 --journal=/tmp/net1-join.log
```

Для LAN хост указывает свой адрес интерфейса в `--host`, второй участник — этот адрес в `--join`. Стенд создаёт два юнита разных игроков, выдаёт обоим движение на тике2, выполняет20тиков с разным количеством workers, печатает итоговый hash и автоматически проверяет offline replay. `--port=0` у host выбирает свободный порт и печатает `LISTEN port`. Самостоятельный запуск без параметров выполняет сетевые проверки; они также включены в `./tools/dev.sh test`.

Основная версия по умолчанию — прямой клиентский lockstep по подходу OpenRA/0 A.D./Recoil. Внешний сервер необязателен: поиск игр и роль зрителя для статистики/анализа матчей, баланса и признаков читерства, без влияния на tick barrier. Безопасный режим с доверенным сервером — последняя дополнительная опция NET8/NET9, выключенная по умолчанию и выбираемая при создании игры. Live NET2 использует pinned TLS без обязательного аккаунта; конечный NET1 diagnostic harness сохраняет plain TCP. NET4.1 реализован; следующие срезы — canonical snapshot/restore и reconnect NET4.2/4.3. Дипломатия в ходе матча и игровое поражение требуют дополнительных правил Gameplay. Фиксированные альянсы и лобби описаны в [041](../../docs/decisions/041-game-shell-and-lobby.md).

- [Исследование](../../docs/research/networking/README.md)
- [Референсы](../../docs/research/networking/references.md)
- [Целевые алгоритмы](../../docs/research/networking/design.md)
- [Приоритеты](../../docs/research/networking/roadmap.md)
- [Проверки NET1](../../docs/verification/037-net1.md)
