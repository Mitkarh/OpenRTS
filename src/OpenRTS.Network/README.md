# OpenRTS.Network

NET1/NET2, включая WAN pipeline: прямой клиентский lockstep, .NET10 / MIT. Зависимости Network → Gameplay → Core; Godot/Content не требуются, новых NuGet пакетов нет.

`DirectMatch.Run` принимает подключённый TcpClient, player1/2, immutable MatchDescriptor, фабрику tick-zero SimulationWorld, callback ввода на будущий тик и stream журнала. Run блокирует поток-владелец до EndTick либо согласованного Leave в live режиме; сокет и мир освобождаются им, журналом владеет вызывающий код. Cancel и deadline прерывают сетевое ожидание. Команды передаются pipeline; checkpoints сверяются периодически, финальный hash обязателен; `DesyncException.Tick` обозначает первый проверенный расходящийся тик. `MatchJournal.Replay` воспроизводит завершённый журнал с исходной фабрикой и проверяет все hashes.

Поддержаны обычные RTS player intents, включая способности, производство, стройку, транспорт, upgrade/transform и авиацию; дополнительные поля имеют явный wire format3 (player intent IDs/encoding сохранены из format2). Stamp назначает сетевой owner, а не offline Session.Submit. Неподдержанные поля/команды отвергаются. Детали текущего протокола: [решение038](../../docs/decisions/038-net2-live-direct-matches.md); исходный NET1 — [решение037](../../docs/decisions/037-net1-direct-lockstep.md).

## Игра через Godot (NET2)

`./tools/dev.sh run` → в настройке выбрать «Создать прямую игру» либо «Подключиться». Для LAN хост указывает адрес своего интерфейса либо `0.0.0.0`, подключающийся — реальный LAN IP хоста и тот же порт. Оба участника выбирают одинаковые карту/режим/точки/fog и буфер ввода. Хост нажимает «Начать» и передаёт показанный SHA-256 отпечаток по отдельному каналу. Второй участник вводит его и подтверждает запуск. Соединение TLS; host/player1 и join/player2 управляют только своей стороной. «Отменить подключение» освобождает listener/socket; локальное игровое меню не останавливает мир; «Покинуть матч» завершает прямую игру для обоих.

Журналы: Godot `user://network`. Wire/journal format3; старые журналы version1/2 несовместимы. `DirectSession` предоставляет bounded Submit, Frames, events, Completion и Leave на отдельном owner thread. `EndTick=0` — live, с realtime pacing. Gameplay остаётся независимым от сети. Подробности и ограничения — [решение038](../../docs/decisions/038-net2-live-direct-matches.md).

`./tools/dev.sh verify-network` — два headless Godot клиента; `./tools/dev.sh verify-network --visible --output=/tmp/openrts-network-ui` — оконный тест со скриншотами. WAN pipeline завершён и проверен: [контракт039](../../docs/decisions/039-net2-wan-pipeline.md), [измерения](../../docs/verification/039-net2-wan.md). Команды закрываются D тиков вперёд, ожидание относится к нужному тику, а не к ответу на каждый будущий batch. Checkpoints каждые8тиков по умолчанию; bounded queues по32frames и ограниченное непроверенное продвижение. Это не обещание25Гц при любых CPU/loss/jitter.

`./tools/dev.sh verify-wan --output=/tmp/wan-model.json` — задержки, jitter, модель recovery после loss, bandwidth и slow clients. `./tools/dev.sh verify-wan --netem --output=/tmp/wan-kernel` — два TLS процесса и настоящие kernel drops в одноразовом Linux-контейнере (нужен Docker/OrbStack; test-only image iproute2). Сетевые правила хоста не меняются, контейнер удаляется. `--quick` сокращает real matrix до одного профиля. Оба peer-журнала проверяются offline. `DirectSession.Statistics` даёт immutable локальные метрики; они не входят в игровой hash.

## Запуск конечного headless-стенда

Сначала `./tools/dev.sh build`. Из корня репозитория в двух терминалах:

```sh
dotnet run --project tests/OpenRTS.Network.Tests --no-build -- --host=127.0.0.1 --port=17771 --journal=/tmp/net1-host.log
dotnet run --project tests/OpenRTS.Network.Tests --no-build -- --join=127.0.0.1 --port=17771 --journal=/tmp/net1-join.log
```

Для LAN хост указывает свой адрес интерфейса в `--host`, второй участник — этот адрес в `--join`. Стенд создаёт два юнита разных игроков, выдаёт обоим движение на тике2, выполняет20тиков с разным количеством workers, печатает итоговый hash и автоматически проверяет offline replay. `--port=0` у host выбирает свободный порт и печатает `LISTEN port`. Самостоятельный запуск без параметров выполняет сетевые проверки; они также включены в `./tools/dev.sh test`.

Основная версия по умолчанию — прямой клиентский lockstep по подходу OpenRA/0 A.D./Recoil. Внешний сервер необязателен: поиск игр и роль зрителя для статистики/анализа матчей, баланса и признаков читерства, без влияния на tick barrier. Безопасный режим с доверенным сервером — последняя дополнительная опция NET8/NET9, выключенная по умолчанию и выбираемая при создании игры. Live NET2 использует pinned TLS без обязательного аккаунта; конечный NET1 diagnostic harness сохраняет plain TCP. Ближайшая работа — NET3: несколько участников и согласованный lifecycle/membership.

- [Исследование](../../docs/research/networking/README.md)
- [Референсы](../../docs/research/networking/references.md)
- [Целевые алгоритмы](../../docs/research/networking/design.md)
- [Приоритеты](../../docs/research/networking/roadmap.md)
- [Проверки NET1](../../docs/verification/037-net1.md)
