# NET4: восстановление и воспроизведение

**Статус:** реализуется. **Текущий срез:** NET4.1 реализован — переносимый журнал, проигрыватель и восстановление через replay от0. [Контракт042](../../decisions/042-net4-replay-recovery.md), [проверки](../../verification/042-net4.md), [Roadmap](roadmap.md), [действующий lifecycle](../../decisions/041-game-shell-and-lobby.md).

SimulationWorld.Report записывает состояние для хеша, но обратного чтения нет. Runtime распределён между Core allocator/RNG, ECS-компонентами, Gameplay systems, navigation и visibility. Копирование FrameLease или native Frent storage не является сохранением.

NET4.1 использует предусмотренную roadmap промежуточную ветку: восстановить tick-zero мир из установленного контента и записанной recipe, затем применить записанные команды. Это O(K), а не O(размер состояния). Артефакт называется ReplayCheckpoint и содержит историю, а не runtime snapshot. NET4 целиком этим не закрывается.

Текущий контракт: новый журнал содержит полный MatchDescriptor и recipe; чтение ограничено размером/числом тиков, проверяет структуру, последовательности/control и обязательный финал. Проигрыватель проверяет hash каждого тика, не запускает policy ИИ и не принимает игровой ввод. Seek пересоздаёт мир на owner thread и публикует только проверенный результат. ReplayCheckpoint имеет версию, длину и SHA-256; восстановление ограничено временем/отменой и не возвращает частичный мир. Контент и правила должны совпадать; записи не устанавливают код или ресурсы.

Критерии: восстановить K, продолжить теми же командами до T и получить совпадение каждого дальнейшего hash; проверить разные workers, RNG/IDs, движение/бой/производство и сенсорные знания. Отдельно отвергать недокачанные/изменённые файлы, чужой контент, неверную последовательность, превышение квот. Проверить replay UI: пауза, шаг, скорость, seek назад/вперёд, POV и запрет команд.

Следующий срез NET4.2 — canonical runtime snapshot. Нужны собственные схемы и readers: allocator/резервированные IDs/RNG; spatial/health/orders/motion/navigation agents; combat/burst/windup/projectiles/presentation; экономика/production/task IDs/payment; construction/capture/transport/upgrades/transforms/flight; terrain layers; fog/sensors/knowledge и зарегистрированные механики. Derived caches можно перестраивать только после доказательства одинакового будущего поведения. Память AI policy находится вне мира: её восстановление/согласованный reset — отдельный контракт.

NET4.3 — resume token/connection epoch, bounded chunk transfer, catch-up и согласованное включение в barrier. Старое соединение не получает права второй активной ветки. Mid-game observers, seeking от runtime checkpoints и два одновременных rejoin проверяются после этой основы. Trusted server остаётся последним optional режимом NET8/NET9.
