# 045. Регрессии аудита NET1–NET5, пункты1–4

**Дата:** 17ноября2026. macOS/Arm64, .NET10, Godot4.7.2.NET, Debug. [Контракт045](../decisions/045-network-audit-hardening.md), [план и остаток аудита](../research/networking/roadmap.md).

## Автоматические проверки

`./tools/dev.sh test --filter=session`:

- Весь solution собран:0warnings/0errors.
- Network:73/73, включая10новых регрессий; фильтр session не ограничивает сетевой набор.
- DeclarativeContent:20/20.
- Gameplay session:6/6, без ошибок. Полный Gameplay набор повторно не запускался; восемь известных ошибок из аудита/verification037 не объявляются исправленными.

Новые проверки используют настоящие TLS-соединения и raw peer, который обходит локальные запреты корректного StarSession API:

1. Spectator Ready, guest Stop, поддельный отправитель чата, невалидный UTF-8, превышенный frame length, превышение частоты чата и command sequence gap. Хост не завершается. Удаление spectator не ставит игру на паузу; удаление human согласованно ставит остальных на паузу, затем они продолжают. Корректный Move хоста при ошибке sequence сохраняется. Завершённые журналы здоровых участников совпадают побайтно и воспроизводятся.
2. Действительный checkpoint с неверным hash по-прежнему завершает хост с DesyncException на тике4; ошибка не маскируется отключением игрока.
3. Ticket на29-й секунде ограничен expiry lease на30-й; на31-й можно повторно зарегистрировать тот же Match ID, старый билет уже отвергается. Явный Close немедленно убирает публичную запись, запрещает Join/heartbeat/private invite и не позволяет повторно использовать ID до expiry выданных неиспользованных билетов. После expiry регистрация и новый Join успешны.
4. Полноценная запись100001тика при40Гц с настоящими world hashes: default Inspect, Replay и ReplayCheckpoint Capture/Restore проходят и дают тот же SimulationReport. Явные MaximumTicks=100000 и недостаточный MaximumBytes отвергают файл. Это ускоренное выполнение, а не час ожидания в real time; большой боевой мир не измерялся.

## Godot: разрыв и настоящая кнопка возврата

`./tools/dev.sh verify-network --reconnect --output=/tmp/openrts-net-fixes-reconnect-final` (исполняемый Python driver запускался после общей сборки):

- Два Godot процесса, TCP byte relay перед guest и обычный pinned TLS. После готовности/команд/локального меню relay разрывает оба направления действующего соединения, сохраняя listener для возврата.
- Guest получает настоящий EOF. Штатный обработчик создаёт экран ошибки после удаления MatchSetupScreen; проверка вызывает Pressed настоящей кнопки «Вернуться в матч», без reflection или отдельной фабрики тестовой сессии.
- Хост фиксирует потерю human, возвращающийся клиент восстанавливает paused boundary, получает epoch2, затем после Resume оба продолжают тики.
- Оба завершают матч и самостоятельно воспроизводят свои файлы. Финальный hash: `EE0DC287EE9719BFBF104DF1BE0D10B0FBA1E41CF505BE2B388D188191DDEC57`.
- Файлы хоста и вернувшегося guest совпали побайтно:11288bytes. Проверка равенства включена в driver.

Ожидаемый EOF присутствует в Godot error log до маркеров `OPENRTS_NETWORK_RECONNECT_BUTTON` и `OPENRTS_NETWORK_REJOIN:2`; это инъекция отказа, а не необработанный провал стенда. Ручной клик мышью/визуальная оценка этого экрана не выполнялись.

## Штатные native сценарии

- `verify-network --net3 --spectator`: три human, четвёртая AI сторона, spectator; ownership, production, локальные меню, общая Pause/Resume, Stop и offline replay. Четыре процесса дали hash `ABDA45CA9B61B48092C1B5927BF046B9E31E9307AEB7C8DB1AEFD8BBE1B05A57`. Один успешный изолированный прогон не закрывает гонку стенда из пункта8 аудита.
- `verify-directory`: реальный HTTP service и два Godot клиента, публикация/каталог/ключевая session/ticket/TLS, продолжение и replay. Hash `AE563A674639B4BB649B097B219736871419D0D61F11EC71DE999400D402EE1D`.
- `verify-replay`: embedded recipe, pause/step/speed, seek в обе стороны, player POV, read-only commands и финальный hash — успешно.

Локальные артефакты запуска: `/tmp/openrts-net-fixes-tests.log`, `/tmp/openrts-net-fixes-reconnect-final/`, `/tmp/openrts-net-fixes-net3/`, `/tmp/openrts-net-fixes-directory/`, `/tmp/openrts-net-fixes-replay/`. Signing keys и игровые журналы из user:// не добавлялись в репозиторий.

## Пределы

Физический WAN/NAT, разные ОС/CPU, публичный HTTPS deployment, длительная игра в real time и стоимость больших snapshots не проверялись заново. Квоты64MiB, transfer/restore15с и in-memory lifetime каталога сохранены. Пункты5–8 аудита, автоматический NAT/relay backend и trusted mode в этот пакет не входят.
