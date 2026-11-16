# 039. NET2 WAN — проверки и измерения

**Дата:** 16 ноября 2026. [Контракт039](../decisions/039-net2-wan-pipeline.md). Артефакты: [реальный kernel netem](039-wan/netem-results.json), [модель/slow clients](039-wan/model-results.json).

## Метод и критерии до прогона

Два юнита на сетке64×64,25Гц, seed42, разные локальные player IDs. Model-профили:100тиков и одинаковые назначенные тики команд для всех D. Real netem: два отдельных TLS peer-процесса, ввод через DirectSession.Submit после тика20, Leave хоста после100тиков (200для loss1%). Оба peers сверяют финал и независимо проверяют каждый тик своих journal через offline replay.

Обязательные критерии: одинаковый результат внутри матча; replay без расхождений; bounded queues≤32frames и непроверенное продвижение≤2D+H; отсутствие потерянных/повторно применённых команд; завершение model-профиля<25с; permanent slow reader/missing checkpoint дают deadline, а не бесконечное ожидание. Целевой baseline steady rate≥22Гц (при nominal25) определён для достаточного D и чистого канала/низкого RTT; это не гарантия любой смеси loss/jitter. Для real loss-профиля требуется ненулевой `tc drops`, иначе потеря не считается проверенной.

SteadyHz измеряет тики30..90, MeanHz — весь интервал симуляции до финального тика, включая bootstrap и остановки. Handshake/terminal ACK не входят в MeanHz. P95 sealed→apply рассчитан по закрытым batches (включая пустые), до конца Step; стоимость Report/journal/publish не входит в эту latency. Submit→observed — один пользовательский ввод и первое наблюдение изменившейся позиции в опубликованном frame, не percentile и не GPU latency. Замеры небольшого мира не доказывают производительность10000объектов.

## Настоящие потери TCP под TLS

macOS/Arm64, runtime10.0.5, Linux kernel7.2.12-orbstack, образ/точная kernel версия и options записаны в JSON. `tc netem` действует на egress eth0 только test-контейнера; nominal RTT задаётся как RTT/2 delay на каждом направлении relay. Kernel jitter/loss и TCP ACK/control packets входят в статистику. Конфигурации повторяемы, конкретная последовательность случайных kernel drops не закреплена seed.

| RTT мс | Jitter мс | Loss % | D | Steady Гц | Mean Гц | Sealed→apply P95 мс | Submit→frame мс | Kernel drops |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 0 | 0 | 0 | 2 | 24.99 | 25.0 | 80.736 | 136.0 | 0 |
| 50 | 20 | 0 | 4 | 25.01 | 25.0 | 160.783 | 212.5 | 0 |
| 150 | 20 | 1 | 8 | 23.9 | 24.67 | 373.732 | 366.7 | 5 |
| 300 | 0 | 0 | 12 | 25.0 | 24.99 | 480.731 | 524.8 | 0 |
| 300 | 80 | 5 | 12 | 25.0 | 21.23 | 1280.892 | 527.7 | 13 |

Все5профилей завершились без desync, оба replay совпали. Даже при steady25Гц потери/планирование могут дать существенный startup/stall и latency tail: профиль300/80/5 имеет меньший MeanHz и длинный P95. Эти значения сохраняются в отчёте, а не скрываются warmup-окном. Наблюдавшиеся high-water marks и maxUnverifiedTicks меньше объявленных границ; точные значения — JSON. Это local VM/relay lab с двумя TCP-leg, не замер публичного маршрута/всех TCP-реализаций.

## Модель и медленные клиенты

| Профиль | Steady Гц | Wait P95 мс | Sealed→apply P95 мс | Max lead |
| --- | --- | --- | --- | --- |
| RTT0, jitter0, modeled loss0% | 25 | 0.18 | 80.78 | 1 |
| RTT50, jitter20, modeled loss0% | 25.01 | 0.16 | 160.96 | 1 |
| RTT150, jitter20, modeled loss1% | 24.99 | 0.14 | 320.75 | 1 |
| RTT300, jitter80, modeled loss0% | 25 | 0.12 | 480.84 | 2 |
| RTT300, jitter80, modeled loss5% | 25 | 0.14 | 481.05 | 2 |
| RTT50, jitter0, modeled loss0%; peer100мс на10тиках | 22.5 | 99.64 | 495.35 | 6 |
| RTT150, jitter20, modeled loss0%; 800bytes/s | 14.73 | 146.01 | 563.57 | 1 |

Все7model-профилей имеют один финальный hash при одинаковом логическом журнале, и каждый replay совпадает. Медленный расчёт временно задерживает другого игрока; после восстановления состояние сходится. При800bytes/s частота падает — это зафиксированный предел, а не попытка скрыть проблему пропуском тиков.

## Воспроизведение

```sh
./tools/dev.sh test
./tools/dev.sh verify-wan --output=/tmp/openrts-wan-model.json
# Нужен работающий Docker/OrbStack; сборка test-only image загружает iproute2.
./tools/dev.sh verify-wan --netem --output=/tmp/openrts-wan-netem
./tools/dev.sh verify-network
```

`verify-wan --netem --quick` оставляет один real profile. Runner запускает peers, ограничивает ожидания, сохраняет samples/journals/kernel stats, проверяет replay, затем удаляет только свои контейнеры. NET_ADMIN применяется к network namespace контейнера, без host firewall changes. На Linux добавляется host-gateway для связи с локальным peer.

Unit/regression набор дополнительно проверяет future reorder/duplicate, поддельный checkpoint/desync, D1/D16, потерю соединения/отмену, неподвижный reader, запрещённое продвижение без checkpoints и невозможность обойти deadline уже заполненным receive buffer. Финальный прогон: сборка решения без ошибок/предупреждений, **23 Network checks passed**. Проверка двух настоящих Godot-клиентов с новым буфером ввода прошла: ownership/production, меню без сетевой паузы, согласованный Leave, одинаковый финал и независимый skirmish replay.

После финальной guard-проверки EOF/ACK повторён real profile150/20/1 на итоговом pipeline: [артефакт](039-wan/netem-final-regression.json). Kernel зафиксировал7drops, оба peer-процесса и оба replay успешны. Полная матрица выше измеряет основной pipeline/pacing; финальные guard-правки не меняют нормальные тики.

Полный игровой suite повторно не запускался: Gameplay/Core не менялись, старые известные8падений разобраны в [037](037-net1.md). Выполнены все сетевые проверки и реальный Godot smoke; игровые golden traces не обновлялись.

## Границы доказанного

Проверены application pipeline и реальные kernel TCP-потери в указанном стенде. Не проверены cross-OS/CPU determinism, NAT traversal, длительные публичные маршруты, массовая симуляция/10000actors, GPU/display latency и долговременная статистика RTT tails. Отдельный доверенный игровой сервер/защита от map-hack не добавлялись. Системные TCP buffers и test relay имеют собственные ограничения; high-water channel metrics не являются общим memory profile процесса.
