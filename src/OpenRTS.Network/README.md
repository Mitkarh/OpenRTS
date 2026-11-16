# OpenRTS.Network

NET1: прямой lockstep двух headless клиентов, .NET10 / MIT. Зависимости Network → Gameplay → Core; Godot/Content не требуются, новых NuGet пакетов нет.

`DirectMatch.Run` принимает подключённый TcpClient, player1/2, immutable MatchDescriptor, фабрику tick-zero SimulationWorld, callback ввода на будущий тик и stream журнала. Run блокирует поток-владелец до EndTick; сокет и мир освобождаются им, журналом владеет вызывающий код. Cancel и deadline прерывают сетевое ожидание. Пакеты и полный hash сверяются на каждом тике; `DesyncException.Tick` обозначает первый проверенный расходящийся тик. `MatchJournal.Replay` воспроизводит завершённый журнал с исходной фабрикой и проверяет все hashes.

Поддержаны MoveSelected/MoveGroup/AttackMove/AttackTarget/Stop/HoldPosition, recipients и queued. Stamp назначает сетевой owner, а не offline Session.Submit. Неподдержанные поля/команды отвергаются. Детали лимитов, протокола и ошибок: [решение037](../../docs/decisions/037-net1-direct-lockstep.md).

## Запуск стенда

Сначала `./tools/dev.sh build`. Из корня репозитория в двух терминалах:

```sh
dotnet run --project tests/OpenRTS.Network.Tests --no-build -- --host=127.0.0.1 --port=17771 --journal=/tmp/net1-host.log
dotnet run --project tests/OpenRTS.Network.Tests --no-build -- --join=127.0.0.1 --port=17771 --journal=/tmp/net1-join.log
```

Для LAN хост указывает свой адрес интерфейса в `--host`, второй участник — этот адрес в `--join`. Стенд создаёт два юнита разных игроков, выдаёт обоим движение на тике2, выполняет20тиков с разным количеством workers, печатает итоговый hash и автоматически проверяет offline replay. `--port=0` у host выбирает свободный порт и печатает `LISTEN port`. Самостоятельный запуск без параметров выполняет сетевые проверки; они также включены в `./tools/dev.sh test`.

NET1 — доверительная тестовая игра без TLS/auth и защиты скрытого мира, не публичный WAN режим. UI, realtime pacing, лобби, NAT, reconnect и безопасная серверная выдача принадлежат следующим этапам.

- [Исследование](../../docs/research/networking/README.md)
- [Референсы](../../docs/research/networking/references.md)
- [Целевые алгоритмы](../../docs/research/networking/design.md)
- [Приоритеты](../../docs/research/networking/roadmap.md)
- [Проверки NET1](../../docs/verification/037-net1.md)
