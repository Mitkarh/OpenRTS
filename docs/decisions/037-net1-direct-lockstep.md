# 037. NET1: прямой lockstep двух клиентов

**Дата:** 16 ноября 2026. **Статус:** реализовано для headless LAN/IP прототипа. [Исследование](../research/networking/README.md) · [проект](../research/networking/design.md) · [проверка](../verification/037-net1.md).

## Граница

OpenRTS.Network → Gameplay → Core; новых зависимостей и обратных ссылок нет. DirectMatch.Run — синхронный owner-loop: он владеет переданным TcpClient и созданным фабрикой миром, освобождает оба при успехе/ошибке/отмене. Вызывать на выделенном потоке; async I/O не исполняет Step и не получает ECS refs. Потребитель передаёт immutable MatchDescriptor, фабрику tick-zero мира, callback будущего ввода и свой выходной stream журнала. После успешной проверки хеша callback afterTick может копировать данные мира для публикации. Stream журнала остаётся собственностью потребителя.

NET1 — конечный диагностический матч с EndTick, без realtime pacing/UI. Сетевые команды не проходят через offline Session.Submit, поэтому не получают локальные issuer1/next-tick stamps. Движок Gameplay не изменён.

## Протокол1

TCP/NoDelay, little-endian Int32 length + payload. Максимум256KiB на frame, hello/hash дополнительно ограничены4KiB; строки ограничены1024 UTF-8 bytes. В отличие от предварительного проекта, batch передаётся одним ограниченным frame, без chunking: выбранного подмножества intents достаточно для10000 recipients. Нулевые/отрицательные/слишком большие длины отвергаются до allocation. Partial reads/EOF/cancel обрабатываются явно. Deadline10с по умолчанию на обмен, configurable до60с, один deadline на чтение и запись вместе. До64 повторных frames на обмен; затем ошибка, без бесконечного duplicate flood.

Hello согласует точную версию1, противоположные player slots1/2, host-assigned Guid match, descriptor и SHA-256 исходного состояния. Descriptor содержит rules/build ID, content identity, scenario/map/settings identity, seed, tick rate, D1..16 и EndTick1..1000000. Значения identity вычисляет/задаёт game host; Network не загружает Content. Ready связывает hash descriptor с match ID; получение Ready обоих peers служит стартовым барьером. Редактирование лобби, capability negotiation и reconnect epochs оставлены NET2/NET3/NET4; в NET1 одно соединение на весь матч, без reconnect.

Player intents имеют собственные wire IDs1..6: MoveSelected, MoveGroup, AttackMove, AttackTarget, Stop, HoldPosition. Передаются Entity, Position с Elevation, TargetEntity, Recipients и Queued. Остальные поля WorldCommand должны иметь default: codec отклоняет их, а не теряет молча. ≤256команд, ≤16384recipients/command и ≤32768 суммарно/batch. IDs recipients строго возрастают и ненулевые. Issuer назначает соединение, Tick — закрываемый batch, Sequence — положительный непрерывный счётчик. На тике0 включается RestrictCommandsToPlayers; чужой приказ получает обычный CommandDenied на согласованном тике.

После завершённого n каждый peer закрывает n+D; тики1..D−1 согласованно пустые. Пакеты будущего окна сохраняются в TickInbox, одинаковый повтор идемпотентен, конфликтующий повтор/чужой match/player/просроченный пакет/sequence gap — ошибка. История дедупликации ограничена D+2 тиками. TCP канал обменивается закрываемыми пакетами последовательно; TickInbox отдельно проверен с переупорядочиванием будущих пакетов. Каждый Step получает canonical issuer/sequence order, независимо от порядка доставки. Отсутствие batch оставляет Tick неизменным. После Step peers сравнивают полный hash того же тика и только затем публикуют результат. Следующий тик не исполняется до подтверждения этого hash. Это более строгий диагностический barrier, чем будущая периодическая проверка: дополнительный RTT и стоимость полного Report измерять отдельно в NET2.

## Журнал и отказы

MatchJournal — потоковый бинарный формат1 с bounded records: descriptor + начальный hash, два batch records и hash на каждый исполненный тик, success footer. Квота64MiB, без хранения полной истории в памяти. При runtime ошибке записывается diagnostic record с типом/причиной (если stream доступен). Handshake ошибки происходят до создания журнала и возвращаются вызывающему коду. При desync исключение содержит первый **проверенный** расходящийся тик и оба hashes; локальный журнал сохраняет применённые команды. Failure/footer отличают завершённый replay от обрыва. Replay требует исходного descriptor/фабрики и сверяет каждый тик; это не snapshot/restore и не межверсионная совместимость.

Исключение, EOF, timeout или cancel прекращают матч, без самовольного drop/AI takeover. Отмена внутри Step сохраняет действующий контракт незавершённого мира; публикации такого состояния нет.

## Ограничения

Без TLS/auth/account/NAT, защиты от злонамеренного участника и map-hack. Seed/мир/журнал полные у обоих peers. Только2игрока, конечный headless сценарий, фиксированный delay; gameplay enum/хеши не менялись. Ввод производства/способностей/upgrade и Godot UI не предлагается. Loopback испытан на этой машине; две физические машины и cross-OS determinism не проверены. Дальнейшие шаги — NET2 по [roadmap](../research/networking/roadmap.md).
