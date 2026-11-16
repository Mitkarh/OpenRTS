# Приоритеты и реализация сетевого слоя

**Статус:** проект от16ноября2026. NET0 и headless-срез NET1 реализованы; [решение037](../../decisions/037-net1-direct-lockstep.md), [проверки](../../verification/037-net1.md). NET2–NET9 остаются будущими этапами. NET1 использует конечный диагностический сценарий и дополнительный hash barrier каждого тика; UI/realtime/LAN на разных машинах ещё не проверены. [Алгоритмы](design.md) · [референсы](references.md) · [контекст](README.md).

Приоритеты: **P0** — без этого нельзя выпускать соответствующий режим; **P1** — следующий полезный результат; **P2** — дополнительные возможности после измерений. P0 защищённого режима не обязан входить в первый LAN прототип. Этапы не являются оценками календарных сроков.

## Backlog по полезности и зависимостям

| Приоритет / ID | Возможность | Зависит от | Проверяемый результат |
| --- | --- | --- | --- |
| P0 / NET1 | Два headless клиента, прямой IP, codec/handshake, bounded framing, player identity | NET0 | Одинаковый матч при разном расписании пакетов |
| P0 / NET1 | Tick barrier, пустые batches, sequence/dedup, fixed input delay, ownership/command whitelist | NET1 codec | Доставка не меняет результат, чужие команды не исполняются |
| P0 / NET1 | Hash checkpoints, запись полного журнала, desync stop, cancel/dispose | NET1 barrier | Расхождение имеет первый известный тик и воспроизводимый артефакт |
| P0 / NET2 | Подключение Godot UI, host/join/ready/errors, сетевой Waiting, surrender/exit | NET1 | Два человека играют один skirmish, локальное меню не меняет общий мир |
| P0 / NET2 | Аутентифицированное шифрование, квоты, fuzz декодера, испытания потерь/задержек | NET1 | Допуск к WAN тестам, ограниченные ресурсы при плохом входе |
| P1 / NET3 | 3+ игроков, звезда, команды/слоты, согласованный disconnect/pause, AI policy | NET2 | Membership и контроль тика одинаковы на клиентах |
| P1 / NET4 | Полный save/restore, checkpoints, resume и догоняющий режим | NET1 + работа в Gameplay/Core | Продолжение совпадает с непрерывным запуском |
| P1 / NET4 | Replay UI, player POV, зрители с начала матча, позже mid-game join | Replay NET1; checkpoint для дешёвого mid-game | Зритель не тормозит игроков, цена истории ограничена |
| P1 / NET5 | Поиск/создание матчей, account/session/ticket, version filtering, private invites | NET2 | Каталог приводит к нужному матчу с нужной ролью |
| P1 / NET5 | NAT traversal/relay, dedicated command relay, мониторинг | NET2/3, transport spike | Прямое подключение либо понятный relay fallback |
| P0 trusted / NET6 | Headless trusted server, server-assigned command time, независимый server tick, AI | NET1 semantics; минимальный server host | Клиенты не могут прислать игровой результат как истину |
| P0 trusted / NET6 | Filtered DTO, knowledge projection, field/reference/event/map filtering, клиентская реплика | NET6 + существующие сенсоры | Вражеская скрытая истина отсутствует в wire-выдаче |
| P0 trusted / NET7 | Delta/baseline ACK, надёжные transitions/events, backpressure, защищённый rejoin | NET6 | Корректная реплика при потерях и медленном клиенте |
| P0 trusted / NET7 | Тесты утечек, ограничения запросов, abuse/flood handling, security review | NET6/7 | Выполнена заявленная модель угроз, пробелы перечислены |
| P1 / NET8 | Delayed spectators, team/player POV, post-match replay, moderation hooks | NET7 + policy control plane | Наблюдение не открывает текущий мир игрокам |
| P1 / NET8 | Durable checkpoints/journal, crash recovery, results verification | NET4 + NET6/7 | Восстановление до объявленной границы без дублей задач/платежей |
| P2 / NET9 | Matchmaking/rating, турниры, массовый broadcast, HA, host migration для P2P | Стабильные предыдущие режимы | Отдельные продуктовые требования и нагрузочные испытания |

NET5 и NET6 можно вести независимо после NET2: каталог не нужен для локального эксперимента trusted server. NET4 обязателен для точного server recovery, но filtered client rejoin может работать от нового keyframe без полного restore мира. Delayed omniscient spectators безопаснее строить поверх trusted выдачи; полный lockstep поток на клиенте не может обеспечить такую задержку.

## NET0 — основа (выполнен в этой работе)

Созданы `src/OpenRTS.Network/OpenRTS.Network.csproj` и README, подключение в `OpenRTS.sln`, направление Network→Gameplay→Core. Без транспорта, классов-заглушек, запуска сети и новых пакетов. Исследование, проект и roadmap связаны с общей архитектурой. Успешная сборка каркаса не доказывает сетевую корректность.

## NET1 — минимальный прямой матч (реализован headless-срез)

**Гипотеза:** текущих fixed Step, canonical command ordering и программного создания мира достаточно для двух синхронных клиентов без изменения игровых правил.

**Объём:** TCP loopback/LAN,2peers, фиксированный match descriptor и delay, ограниченный whitelist обычных RTS приказов, честная identity player1/2, barrier, stop-on-disconnect/desync. Минимальный отдельный исполняемый Network test harness добавить при появлении логики; зарегистрировать в `tools/dev.sh test`. UI и подключение из интернета ещё не критерии NET1.

**Проверки:** одинаковый seed/сценарий и журнал дают одинаковый hash на каждом контрольном тике у двух процессов и offline replay; разный FPS/worker count не влияет. Прогнать пустые тики, simultaneous commands, большие EntitySet, split/coalesced TCP reads, дубли, конфликтующий дубль, future/past tick, пропуск sequence, неверные версии/карта/seed, запрещённый issuer0 и команды чужим объектам. На сетевой паузе Tick не меняется, Dispose не ждёт отсутствующего пакета бесконечно.

**Готово, когда:** автоматический процессный сценарий и LAN запуск воспроизводимы, все входные buffers ограничены, mismatch останавливает матч с журналом. Начальный whitelist может не покрывать все способности; UI следующего этапа не предлагает неподдержанные приказы. При расхождении прежде локализовать игровой determinism/codec; state sync не используется для маскировки ошибки.

## NET2 — игра для двух людей и проверка WAN

**Гипотеза:** фиксированного input delay достаточно для выбранного RTT профиля; TCP head-of-line пока приемлем.

**Объём:** Godot host/join IP/port, ready/start, отображение connection/wait/ping, разрешённые команды skirmish, корректный выход и локальные меню. Общий pause/скорость требуют протокольного решения; текущие offline playback controls не дают менять скорость одного участника. Обеспечить TLS identity, лимиты/дедлайны handshake и журналов до внешних тестов.

**Проверки:** два приложения на разных машинах, подключение/неверный адрес/таймаут/закрытие во время ожидания; policy владельцев, transform/transport/production и stale target; fuzz decoder без world mutation. Профили стенда: RTT0/50/150/300мс, jitter0/20/80мс, потери0/1/5%, краткий разрыв и slow consumer. Это тестовые точки, не обещание играбельности при300мс/5%.

**Критерий:** пользователь может завершить тестовый skirmish, задержка input→apply измерена, ресурсы ограничены. По p95/p99 stall и командам определить допустимые delay/RTT. При плохом результате сравнить увеличение D и готовый transport с независимыми каналами; не писать custom reliability до сравнения.

## NET3 — несколько игроков и жизненный цикл

**Объём:** host-star, lobby slots/teams, смена настроек с отменой Ready, game phase, agreed pause/drop/surrender и AI input policy. Отдельный Chat канал не участвует в simulation hash; текст/частота ограничены. Сначала потеря хоста завершает матч, автоматического host migration нет.

**Критерий:** одинаковый journal/control history у всех при позднем вводе, отключении, одновременном ready/config change. Проверить асимметричный разрыв: только хост принимает membership-решение; оторванный клиент не начинает самостоятельную подтверждённую ветвь. При неспособности сохранить agreement завершать матч явно.

## NET4 — сохранение, восстановление, replay и первые зрители

**Гипотеза:** полный canonical checkpoint позволяет продолжить симуляцию без изменения дальнейшего hash. Это отдельная значимая работа над Gameplay/Core, не сериализация ECS памяти.

**Объём:** версионированный snapshot/restore и журнал, restart test, reconnect tokens/epochs, bounded transfer и catch-up. Сначала replay от0 и зритель с начала матча; mid-game join/checkpoint seeking после доказательства restore. Lockstep spectators получают полный мир и не относятся к защищённому соревновательному режиму.

**Критерий:** save на тикеK, restore и доигрывание доT совпадают с непрерывным запуском на каждом дальнейшем checkpoint. Покрыть RNG, выданные IDs, burst/windup, очереди с progressive payments, partial flight/boarding, navigation local path/yield, construction и сенсорные знания. Проверить недокачанный/повреждённый snapshot, долгий catch-up и два одновременных rejoin. Если равенство нарушено, exact restore считается незавершённым; оставить replay от0 с лимитом длительности.

## NET5 — сервис доступа к матчам

**Объём:** регистрация/TTL матча, протокол/версия/режим/слоты, private invites, account session и короткоживущий role-bound ticket, direct/relay endpoints. Не хранить доверенный результат лишь на основании заявлений P2P хоста; P2P матч в каталоге обозначен как доверительный.

**Проверки:** stale lobby/heartbeat, двойной ticket, ticket другой роли/матча, истёкший credential, недоступный endpoint и NAT fallback, rate limits. Credentials в логах отсутствуют. UPnP/port mapping не включать незаметно; выбранный NAT механизм и relay provider требуют отдельного решения по зависимости/эксплуатации.

**Критерий:** игрок находит совместимый матч, получает проверяемую роль и либо подключается, либо видит конкретную сетевую ошибку. Matchmaker/rating и массовая инфраструктура отложены до реального спроса. Relay не называется защитой от map-hack.

## NET6 — первый доверенный сервер

**Гипотеза:** серверная стоимость полной симуляции и фильтрованной выдачи приемлема в целевом профиле; знания можно проецировать без скрытой истины.

**Объём:** отдельный headless процесс, проверка player intents и серверное назначение времени, полная серверная симуляция, новый knowledge DTO и клиентский read model без полного мира. Сначала надёжные keyframes на уменьшенном сценарии; baseline/delta и большой масштаб — NET7. Небольшой прототип допускает низкую эффективность трафика, но не обход permission filtering.

**Проверки:** модифицированный клиент присылает фальшивые HP/позиции/owner/admin и guessed target IDs; эти значения не меняют истину. Два мира с одинаковыми разрешёнными наблюдениями и различными скрытыми армиями дают одинаковый нормализованный прикладной поток. Проверить initial map, hidden buildings/footprints, projectiles/events/sounds, radar anonymity, Previous, production/economy и все ссылки DTO.

**Критерий:** завершён маленький server-authoritative матч, скрытая истина не нужна клиентскому renderer/UI, выдача проходит тесты утечек. При дорогой симуляции сначала измерить фазы и ограничить профиль/оптимизировать сервер. Передача скрытого мира клиенту не считается успешным продолжением этой гипотезы.

## NET7 — масштаб и устойчивость защищённой выдачи

**Объём:** versioned keyframe/delta, ACK baselines, prioritization по разрешённым знаниям, надёжные lifecycle/events, quantization без изменения игрового результата, limited compression, reconnect с новым filtered keyframe, congestion/backpressure. Сравнить TCP/TLS и подходящий готовый transport на тех же сценариях; лицензии и desktop support проверить до подключения.

**Критерий:** packet loss/reorder/slow consumer не оставляют живые данные уже скрытого объекта и не создают события дважды; отсутствие baseline вызывает ограниченный resync. Установленные по baseline бюджеты CPU/uplink/memory выполняются для конкретного профиля. Не обобщать10000 стоящих объектов на10000 движущихся/стреляющих снарядами.

## NET8–NET9 — наблюдение, эксплуатация и дополнительные функции

NET8: серверная spectator delay и роли POV, отдельный broadcast budget; игрокам не выдаются текущие omniscient данные. Replay policies и модерирование не смешиваются с игровым ECS. Durable checkpoint/journal/результаты нужны для восстановления trusted server; измерить RPO/RTO и тестировать crash между записью журнала, Step и отправкой ack.

NET9: рейтинги, автоматический подбор, турниры, масштабируемый broadcast, server reservation, HA, optional P2P host migration. Каждая возможность получает отдельный короткий дизайн и критерии. Rollback, распределённая доверенная симуляция и клиентский расчёт скрытых взаимодействий не являются обязательной частью пути к рабочему защищённому серверу.

## Сквозная матрица измерений

| Область | Нагрузка и метрики | Когда |
| --- | --- | --- |
| Детерминизм | Два процесса; разные jobs/расписание/FPS, затем Windows/Linux/macOS и доступные CPU; первый расходящийся тик | NET1; cross-platform до обещания поддержки |
| Задержка | UI input→sealed→received→applied→display, p50/p95/p99, stalls и delay ticks | NET1/2 |
| Команды | Обычный ввод и burst, выделение1/1000/10000 объектов, simultaneous issuers, bytes/tick | NET1/3 |
| Симуляция | 100/1000/10000 объектов; стоящие, движение, плотный бой, производство, авиация и сенсоры отдельно | NET1 baseline; NET6 server |
| Репликация | Доли видимости, появление большой армии, разные viewers/камеры; projection/codec/compression CPU, bytes/s, queue age, allocations | NET6/7 |
| Восстановление | Размер checkpoint, capture/restore time, catch-up ticks/s, рост хвоста, replay duration и disk quota | NET4/8 |
| Утечки | Нормализованное сравнение выдачи скрытых вариантов, field/reference inventory, packet size/timing analysis | NET6/7 |

Фиксировать build/OS/CPU/GC, tick rate, worker count, длительность прогрева/измерения и конфигурацию сети. Считать отдельно World.Step, Report/hash, snapshot, проекцию viewer, codec, socket queues и rendering. Сначала снять baseline; затем установить численные бюджеты для продолжения этапа. Число сущностей само по себе не является критерием производительности.
