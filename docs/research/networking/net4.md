# NET4: восстановление и воспроизведение

**Статус:** NET4.1–4.3 реализованы в пределах ниже. [История/replay042](../../decisions/042-net4-replay-recovery.md), [runtime/rejoin043](../../decisions/043-net4-runtime-snapshot-rejoin.md), [проверки043](../../verification/043-net4-runtime-rejoin.md), [Roadmap](roadmap.md).

NET4.1: переносимый StarJournalv3 с descriptor/recipe, проигрыватель из главного меню, пауза/шаг/скорость/POV/seek и ReplayCheckpoint. Последний сохраняет **историю** и восстанавливает мир от0 за O(K). Этот совместимый fallback остаётся отдельно от runtime snapshot.

NET4.2: `SimulationWorld.CaptureSnapshot/RestoreSnapshot` — собственный версионированный payload состояния на K. Core allocator/RNG/резервы, ECS actors, orders/motion/navigation, combat/projectiles, economy/production/payments, construction/capture/transport/supply/upgrades/transforms/flight, terrain и fog/sensor knowledge имеют схемы. Native storage/handles не сохраняются. Зарегистрированная stateful механика обязана реализовать `IRuntimeSnapshotMechanic`; без reader операция завершается явной ошибкой. Фабрика и hash исходного сценария связывают файл с установленным контентом/конфигурацией.

Проверенная гипотеза: восстановить K, продолжить **теми же командами** до T и получить каждый дальнейший hash и события. Реальные fixtures production320, aviation420, sensors48 плюс focused RNG/IDs/packed removal, capture, cargo/upgrades/condition expiry, transformations, supply, навигационный FIFO/budget и custom rule. Новый процесс восстанавливает runtime файл независимо от исходного. Это не доказательство полноты будущих механик или скорости10000+.

NET4.3: pinned TLS Hello с token Match/Player/Epoch → общая пауза K → runtime snapshot + bounded journal prefix chunks → проверка recipe/структуры/digests/state hash → ACK → controls, накопленные за время передачи → новая эпоха и согласованный Human на K → bootstrap пустых inputs до K+D → явный Resume. Старый transport закрыт, второй live/восстанавливающийся слот отвергается. Сохраняется общий полный журнал. Два одновременных rejoin и поздний зритель проверены настоящими TLS-сессиями.

Зритель подключается после Start только на уже установленной паузе и не входит в tick barrier. Запрос во время Playing не останавливает игру. В sandbox есть «Вернуться в матч» после разрыва; секрет хранится в памяти. Память хостовых AI planners при возврате клиента остаётся на исходном хосте; воспроизведение не запускает AI policy повторно.

Остаются отдельными продолжениями: catch-up наблюдателей без общей паузы (NET6/NET7), дисковый индекс runtime checkpoints для replay seeks, перенос/сохранение внешних AI policies, durable host restart/migration. Текущий replay seek по-прежнему воспроизводит от0. Host loss завершает текущие клиенты. Trusted authoritative server остаётся последним optional NET8/NET9.
