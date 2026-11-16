# Референсные сетевые реализации RTS

**Метод и дата:** чтение кода и официальной документации, 16 ноября 2026; ручные трассы, без запуска референсов. Ниже фактические механизмы отделены от предложений для OpenRTS. GPL исходники служат только для изучения поведения.

## Версии и воспроизводимость чтения

| ID | Основа | Изученные места |
| --- | --- | --- |
| R1 | Локальный OpenRA, commit `9636b63eaee62fcb60ef2e896e7985bdf2f1ee68`; checkout без локальных изменений на момент чтения | OrderManager, NetworkConnection, ReplayConnection, серверная пересылка |
| R2 | GitHub-зеркало `0ad/0ad`, commit `61a3b9507d974084e6badb88a0826bd89a6d5b8b` | TurnManager, NetClientTurnManager, NetServerTurnManager, выбранные ветви NetServer |
| R3 | `beyond-all-reason/RecoilEngine`, master на дату чтения; Git blob GameServer.cpp `186f103bb6d0d436b67c4946cd90a31b0d2bc641` | Broadcast, CheckSync, CreateNewFrame, BindConnection; официальное Netcode overview |
| R4 | `Anuken/Mindustry`, master на дату чтения; Git blobs NetServer.java `cbc089ebbfe311a5a7a26cb2fb2f88046de998ea`, NetClient.java `ea407404eaa3fd0276d91f039b15a2bc1044ca7c` | Снимки сущностей, team filtering, hiddenSnapshot, вход clientSnapshot |

R2 — конкретная доступная ревизия зеркала, а не утверждение о последней версии 0 A.D. Для R3/R4 закреплены содержимые файлов Git blob IDs; ссылки master могут измениться, commit всего репозитория не зафиксирован. Перед реализацией заимствуемой идеи перепроверить соответствующие функции. R1/R2/R3 — GPL-референсы, [лицензия Mindustry](https://github.com/Anuken/Mindustry/blob/master/LICENSE) — GPLv3; библиотек из них в проекте нет.

## R1. OpenRA: пакеты приказов, сетевой барьер и replay

**Источники:** [OrderManager](https://github.com/OpenRA/OpenRA/blob/9636b63eaee62fcb60ef2e896e7985bdf2f1ee68/OpenRA.Game/Network/OrderManager.cs#L197), [Connection](https://github.com/OpenRA/OpenRA/blob/9636b63eaee62fcb60ef2e896e7985bdf2f1ee68/OpenRA.Game/Network/Connection.cs#L105), [ReplayConnection](https://github.com/OpenRA/OpenRA/blob/9636b63eaee62fcb60ef2e896e7985bdf2f1ee68/OpenRA.Game/Network/ReplayConnection.cs#L106), [Server](https://github.com/OpenRA/OpenRA/blob/9636b63eaee62fcb60ef2e896e7985bdf2f1ee68/OpenRA.Game/Server/Server.cs#L853).

**Назначение:** все клиенты применяют одинаковые приказы на одном сетевом кадре; представление продолжает принимать вход во время ожидания.

Путь обычного кадра:

1. NetworkConnection читает length/client/payload из TCP и складывает данные в concurrent очередь. Receive на стороне потребителя разбирает их и передаёт OrderManager.
2. TryTick на границе NetFrameInterval проверяет очереди участников. SendOrders закрывает локальные приказы один раз на NetFrameNumber, включая пустой пакет.
3. Продвижение разрешено после наличия пакета каждого ожидаемого участника. ProcessOrders проверяет совпадение номера кадра, применяет приказы и сравнивает sync hashes.
4. Разрыв представлен маркером кадра: игровая реакция на отключение происходит в согласованной точке. UI состояния соединения может обновиться раньше.
5. ReplayConnection подаёт записанные пакеты и sync-проверки обратно в тот же OrderManager.

**Ручная трасса:** A прислал пустой пакет кадра F, B ещё не прислал → F не выполняется. Приходит B:F → пакет обрабатывается и номер увеличивается. Пустой пакет означает готовность, а не отсутствие связи.

**Оценка:** полезны разделение network receive/world owner, явные пустые пакеты и replay потока. NetFrameInterval позволяет менять частоту batches отдельно от симуляции, ценой задержки ввода. Наличие Server здесь не доказывает авторитетную симуляцию или сокрытие состояния. Политики модов, lobby/auth и все ограничения декодера не аудировались. Для OpenRTS начать с batch на каждый тик и измерить укрупнение позднее; не переносить реализацию или зависимость от порядка контейнера.

## R2. 0 A.D.: закрытие turn, проверка identity и rejoin

**Источники:** [NetClientTurnManager](https://github.com/0ad/0ad/blob/61a3b9507d974084e6badb88a0826bd89a6d5b8b/source/network/NetClientTurnManager.cpp), [TurnManager](https://github.com/0ad/0ad/blob/61a3b9507d974084e6badb88a0826bd89a6d5b8b/source/simulation2/system/TurnManager.cpp), [NetServerTurnManager](https://github.com/0ad/0ad/blob/61a3b9507d974084e6badb88a0826bd89a6d5b8b/source/network/NetServerTurnManager.cpp), [NetServer](https://github.com/0ad/0ad/blob/61a3b9507d974084e6badb88a0826bd89a6d5b8b/source/network/NetServer.cpp).

**Команды и время:** PostCommand адресует приказ в currentTurn+commandDelay. NotifyFinishedOwnCommands посылает EndCommandBatch. Сервер проверяет последовательное увеличение readyTurn; CheckClientsReady публикует следующий готовый turn. TurnManager ждёт готовности, собирает команды в общем порядке, записывает replay и обновляет симуляцию. Длительность turn передаётся отдельно; её конкретное значение в OpenRTS не переносим.

**Проверка расхождения:** клиенты посылают hash после update. Сервер хранит hashes по turn/client, ждёт завершения соответствующего turn у клиентов, сравнивает их и сообщает SyncError. Выбранный исходный hash — диагностический baseline, а не доказательство честности клиента. Наблюдателям разрешено отставание в пределах отдельной настройки.

**Identity и восстановление:** OnSimulationCommand сверяет заявленного player с назначением соединения, когда cheats отключены, затем ретранслирует и сохраняет команды. Для rejoin сервер запрашивает сериализованный мир у существующего клиента, передаёт его новому и досылает команды/EndCommandBatch после сохранённого turn. В прочитанной ветви есть предупреждение о замораживании матча, если возвращающийся клиент долго догоняет.

**Оценка для OpenRTS:** разделить готовность входа и подтверждение уже рассчитанного тика; сразу связывать соединение с игроком. Rejoin на основе клиентского snapshot допустим лишь в доверительной игре. В защищённом режиме snapshot создаёт сервер. До полного save/restore OpenRTS этот алгоритм не реализуем; диагностический hash существующего мира его не заменяет. ENet виден в NetServer, надёжность/шифрование его конфигурации здесь не исследованы.

## R3. Recoil / Spring: компактный поток и цена догоняющего replay

**Источники:** официальное [Netcode overview](https://recoilengine.org/articles/netcode-overview/), [GameServer.cpp](https://github.com/beyond-all-reason/RecoilEngine/blob/master/rts/Net/GameServer.cpp) с blob ID из реестра.

Документация описывает передачу входов вместо позиций/HP и полную симуляцию на клиентах. Отмечены низкий трафик, трудность межплатформенной детерминированности, возможность map-hack и невозможность использовать неполное сохранение для точного продолжения lockstep.

**Путь восстановления по коду:** Broadcast отправляет packet участникам и сохраняет packetCache при разрешённом reconnect/spec join либо до старта. BindConnection выдаёт настройки/номер игрока и проходит cache, отправляя прежние пакеты. Следовательно, цена подключения зависит от накопленной истории; передача истории ещё не означает мгновенное попадание на текущий тик.

**Порядок и диагностика:** CreateNewFrame формирует серверное продвижение, CheckSync сравнивает client checksums. При локальном клиенте baseline берётся у него; без него выбирается наиболее частый checksum. Такой выбор полезен для поиска desync, но не создаёт доверенную истину. Низкоуровневый transport и все правила подавления lag не аудировались.

**Для OpenRTS:** применять replay от начального состояния как первый механизм воспроизведения; ограничивать историю/скорость catch-up и явно показывать догоняющий режим. Для длинных матчей нужны проверенные контрольные точки. Dedicated relay можно сделать раньше авторитетного сервера, сохранив честное обозначение режима без защиты скрытого мира.

## R4. Mindustry: снимки по видимости и границы доверия

**Источники:** [NetServer.java](https://github.com/Anuken/Mindustry/blob/master/core/src/mindustry/core/NetServer.java#L1179), [NetClient.java](https://github.com/Anuken/Mindustry/blob/master/core/src/mindustry/core/NetClient.java#L559); blob IDs в реестре. Это RTS-гибрид, не точный аналог управления армией OpenRTS.

**Построение выдачи:** sync периодически вызывает writeStateSnapshot. При fog сущности сериализуются по командам: writeEntitySnapshotsTeam перебирает Groups.sync, проверяет isSyncHidden(team), видимые сущности пишет в пакеты. Скрытые ID собираются отдельно и отправляются HiddenSnapshotCallPacket. Клиент применяет entitySnapshot, а hiddenSnapshot вызывает handleSyncHidden у известных сущностей. При выключенном fog общий поток сериализуется для всех. Обычные entity snapshots помечены unreliable.

**Граница доверия:** clientSnapshot принимает координаты, движения и ввод игрока; проверяет устаревший snapshotID и нечисловые значения. Проверка позиции дополнительно зависит от strict/headless. Это не пример безусловного серверного расчёта всех действий.

**Оценка для OpenRTS:** полезны отдельная выдача знаний, группировка одинаковой выдачи и явное прекращение обновлений после скрытия. Список всех скрытых ID нельзя перенести как безопасный протокол: он сам раскрывает существование объектов. Клиентские координаты также не подходят как истина RTS. Политики остальных RPC, initial world download, block/state snapshots и всех типов сущностей здесь не проверялись; вывод о полной защите Mindustry не делается.

## Сравнение и перенос идей

| Свойство | OpenRA | 0 A.D. | Recoil | Mindustry | Направление OpenRTS |
| --- | --- | --- | --- | --- | --- |
| Основная синхронизация изученного пути | Приказы/сетевые кадры | Приказы/turn barrier | Входы/серверные кадры | Снимки сущностей | Сначала команды, позже знания |
| Где полный мир | Клиенты | Клиенты | Клиенты | Серверная выдача, границы остальных путей не аудированы | По умолчанию клиенты; optional NET6 сервер-зритель; optional NET8+ доверенный сервер |
| Урок для восстановления | Общий поток replay | Snapshot + хвост команд | Cache + догоняющее воспроизведение | Поток состояния | Полный save отдельно от viewer snapshot |
| Главный риск переноса | Считать relay доверенным миром | Довериться клиентскому snapshot | Неограниченный catch-up/cache | Раскрывать скрытые ID и принимать позиции | Явная модель угроз и тесты wire-выдачи |

**Наш вывод, а не свойство референсов:** единый код семантики приказов полезен обоим режимам, но общий поток полных команд противника несовместим с требованием скрыть его состояние. Поэтому transport/lobby/версии и команда намерения могут переиспользоваться; lockstep scheduler и серверный knowledge replicator будут разными исполнителями. Передача скрытых вычислений недоверенному клиенту не входит в базовый защищённый режим.
