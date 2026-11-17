# 043. NET4.2/4.3 — runtime snapshot и rejoin

**Дата:** 17 ноября 2026. [Контракт043](../decisions/043-net4-runtime-snapshot-rejoin.md), [план](../research/networking/net4.md). Проверки корректности, без заявления о массовой производительности.

## Продолжение мира

`./tools/dev.sh test --filter=session`: **56Network**, **20DeclarativeContent**, **6session/lifecycle** проверок; сборка без предупреждений и ошибок. Полный Gameplay набор этим фильтром не запускается; известные baseline failures из [037](037-net1.md) этим срезом не изменяются.

Runtime snapshot создаётся на0, K и T; Restore получает новую фабрику и затем те же команды до T. Сравнивается **каждый** последующий hash; focused fixtures также сравнивают события. Для production и aviation восстановление использует другую worker-конфигурацию. Actors восстанавливаются в EntityId order, независимо от исходного packed removal.

| Fixture | T | K | Runtime bytes на K |
| --- | ---: | ---: | ---: |
| Production + construction | 320 | 12 | 27839 |
| Aviation | 420 | 185 | 24002 |
| Sensors + fog hold12 | 48 | 12 | 54246 |

Это runtime payload с envelope, не префикс команд. Снимки0/K/T одного мира могут иметь разные размеры. Старые ReplayCheckpoint checks продолжают проверять отдельный history-based формат.

Дополнительные сценарии покрывают RNG Scatter после restore, новые IDs после packed removal, груз/пассажиров, незавершённый upgrade, истечение condition grants, накопленный capture work, подготовку/завершение transformation, supply stock/cargo и продолжение разгрузки, FIFO/budget навигационных полей, зарегистрированную stateful rule с собственным reader. Повторный Capture без Step даёт одинаковые байты. Отдельный OS процесс восстанавливает файл production на K=12.

Повреждённые/truncated файлы, другой исходный мир и отмена отвергаются. Отдельно изменяется reserved-ID count с **пересчитанным SHA-256**: allocator отвергает чрезмерный count до выделения массива; частичный созданный World уже Dispose к моменту ошибки. Это проверяет reader, а не только checksum.

## Настоящие TLS-сессии

NET4.3 проверки используют отдельные owner threads, TcpClient и pinned TLS:

- Два игрока одновременно теряют связь, возвращаются с credentials, получают новую эпоху и одну паузу K; затем продолжают матч. Их полные журналы совпадают с хостом побайтно и воспроизводятся от0.
- Вернувшийся Human получает сторону, которой после disconnect управлял ИИ. Возврат согласован на границе, незавершённые действия мира продолжаются.
- Неверный token, отозванный token/epoch и повторное подключение к занятому live слоту отвергаются; хост и оставшиеся участники продолжают работу.
- Передача отвергает чрезмерные размеры, неправильные offsets и повреждённые digests.
- Поздний spectator загружает текущую границу на общей паузе и затем следует за живыми turns. Анонимное подключение во время Playing отвергается и не останавливает игру.
- Гость начинает с другой локальной recipe; host settings/фиксированные команды восстанавливаются и при повторном подключении. Журнал позднего участника снова совпадает с host.

Прежние NET1–NET3 проверки входят в тот же прогон: ownership, eight humans, WAN jitter/loss/slow clients, control revisions, потеря хоста и spectator non-voting.

## Godot

`./tools/dev.sh verify-replay --output=/tmp/openrts-net423-godot` пройден: два реальных Godot процесса записывают TLS-матч, третий открывает его через меню повторов и проверяет pause/step/speed/seek/POV/read-only/final hash. У хоста fog hold7, локальные defaults гостя/проигрывателя отличаются. Новая кнопка «Вернуться в матч» собрана в Sandbox; ручной клик по ней после физического сетевого разрыва отдельно не выполнялся. Реальный reconnect проверен TLS-тестами выше.

Артефакты: [полный прогон](043-net4/checks.log), [runtime/replay](043-net4/runtime-and-replay.log), [rejoin](043-net4/rejoin.log), [Godot record](043-net4/godot-record.log), [Godot replay](043-net4/godot-replay.log).

## Границы результата

Нагрузки10000+, физическая WAN-сеть при reconnect, огромные64MiB transfers, host migration и перенос внешней памяти AI policy не измерялись. Новый runtime save не ускоряет replay seek автоматически: индекс checkpoints остаётся будущим продолжением. Late observer bootstrap требует паузы; observer catch-up без остановки игры остаётся NET6/NET7. Защищённый сервер не входит в этот срез.
