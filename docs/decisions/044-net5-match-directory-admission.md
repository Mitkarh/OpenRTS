# 044. NET5 — поиск матча и допуск

Уточнение17ноября после аудита: expiry ticket ограничен текущим lease. Close скрывает игру немедленно, но удерживает Match ID до expiry выданных неиспользованных capabilities. [Контракт045](045-network-audit-hardening.md), [проверки045](../verification/045-network-audit.md).

**Дата:** 17 ноября 2026. [План](../research/networking/net5.md), [проверки](../verification/044-net5-directory.md), [NET4/rejoin](043-net4-runtime-snapshot-rejoin.md).

## Границы и зависимости

Добавлен самостоятельный `tools/OpenRTS.MatchDirectory`, ASP.NET Core10 из shared framework .NET, без новых NuGet-пакетов. Логика bounded каталога, клиент HTTP и проверка билета находятся в `OpenRTS.Network/Directory`. Core/Gameplay не получают ссылок на HTTP, аккаунты или каталог. В сервисе нет SimulationWorld, тиков, карт-контента или endpoint записи «доверенного результата».

Каталог опционален. Обычные direct/LAN игры работают с прежним внешним SHA-256 pin и без учётной сессии. Host явно выбирает публикацию и обязательный ticket admission для этого матча. Опубликованный матч не объявляется как обычная LAN-игра: обхода допуска/приватности через LAN нет. Уже принятые участники продолжают расчёт при отказе сервиса; NET4 resume token также проверяется локально. Защищённый сервер остаётся последней optional NET8/NET9.

## Identity и право входа

Первый provider — владение P-256 ключом клиента. Имя — только неподтверждённая подпись; account ID — SHA-256 SubjectPublicKeyInfo. Godot хранит ключ в памяти экземпляра клиента каталога; после перезапуска приложения это новая identity. Постоянные социальные аккаунты, восстановление ключа и OAuth не имитируются этим provider.

`POST /v1/challenges` создаёт challenge с ID/случайным nonce на30с, связанный с public key/name. `POST /v1/sessions` принимает P1363 ECDSA/SHA-256 подпись domain-separated login payload; challenge погашается один раз. Session — случайный256-bit bearer на1час. На сервере bearer/lease/invite сравниваются по hashes; исходные секреты в обычных ответах поиска не выдаются. Logout удаляет session. Клиент обновляет истёкшую session один раз, в том числе после restart сервиса с тем же signing key; уже выданный билет имеет собственный срок.

Подписывающий ключ сервиса можно сохранять `--signing-key=path`; новый файл создаётся с0600 на Unix. Без пути ключ только на время процесса. Match host получает доверенный verification key через HTTPS-каталог; HTTP допустим только на loopback. Неожиданная смена signing key отклоняется. Автоматической подмены policy уже созданного хоста нет.

Билет содержит version, nonce, account/public key, Match, role/player slot, issued/expires, match protocol, rules/content, host TLS pin и endpoints. Срок≤30с. Формат — ограниченный JSON payload + отдельная P1363 подпись P-256/SHA-256, без выбора алгоритма из входа/CLR polymorphism. Это capability допуска, не удостоверение результата матча.

## Каталог и резервирование

Хост регистрирует Match GUID, revision, protocol4, client-lockstep mode, rules/content, title/map ID, доступные player slots, connected/spectator counts, private policy и direct/relay endpoints. Сервис возвращает секрет lease; только та же account identity с lease может обновить или закрыть регистрацию. Heartbeat каждые5с продлевает TTL30с. Устаревшая revision/другая публикация с той же revision отвергается; Ready не меняет эти публичные поля.

Find возвращает до100 публичных совместимых матчей по rules/content. `Trust=peer-hosted` — вычисляемое поле. Private матч отсутствует в списке; invite97символов (Match GUID + случайный256-bit secret) передаётся через body `POST /v1/invites`, не URL. Он открывает конкретный приватный матч, но не заменяет учётную session.

Join выполняется под одним lock:

1. Проверить session, живой lease/TTL, invite при Private и фазу. Новый игрок — только Lobby; поздний spectator — Lobby/Paused по NET4 policy.
2. Найти предыдущий request ID: повтор той же account/role возвращает тот же билет. Чужой request ID/иная роль — ошибка. Истёкший/отменённый запрос не получает новое назначение.
3. Атомарно выбрать свободное место, учитывая неистёкшие reservations. Для зрителей действует общий предел8. Два конкурентных запроса последнего места дают один grant.
4. Записать reservation и подписать билет на это место/роль/account. Строки/arrays/identity/endpoint flags валидируются до публикации.
5. Хост после успешного handshake подтверждает receipt (nonce/account), а heartbeat публикует реальный состав. Confirm идемпотентен и **не переписывает** существующую host revision; подтверждённое место удерживается отдельно до более новой публикации.

Явная отмена прекращает запрос и его повторы. Уже выданный подписанный билет нельзя мгновенно отозвать на автономном хосте: место остаётся в карантине до expiry≤30с. Иначе каталог мог бы выдать второй действительный capability на тот же slot. Это осознанное уточнение раннего плана «освободить по отказу» для offline verification; немедленное освобождение потребовало бы подтверждённого отзыва от хоста. Used/expired/cancelled request IDs сохраняются ещё2мин для идемпотентности.

## TLS admission и endpoints

StarProtocol поднят с3 до4; live peers нужно обновлять вместе. Journal formats не менялись. Hello теперь содержит bounded optional ticket, отдельно от NET4 resume credential.

- Клиент пробует direct, затем явно зарегистрированный relay, до5с на endpoint. В обоих случаях сохраняется **тот же** host certificate pin; успешный TCP connect не отменяет TLS проверку. Неудача показывает endpoint и причину, не запускает локальную ветку мира.
- Host TLS/Hello tasks выполняются вне simulation owner и ограничены прежними8 handshakes. При ticket хост сначала проверяет подпись и выдаёт новый случайный challenge этого TLS-соединения. Клиент подписывает domain-separated ticket/challenge своим account key. Копирование одного bearer ticket недостаточно для владения местом.
- Owner повторно проверяет expiry, Match/slot/role/pin/rules/content, свободный live slot и одноразовый nonce. Только затем меняет membership/Ready. Неизвестный/просроченный билет, чужой ключ/роль/матч и повтор nonce отвергаются с явной причиной. Кэш использованных nonce ограничен4096 и очищается по expiry.
- Admission receipts уходят в отдельную очередь публикации. HTTP/heartbeat/Confirm не входят в TickInbox/barrier. Rejoin использует уже выданный NET4 Match/Player/Epoch token; старый Join ticket повторно не используется.

Relay здесь — готовый TCP forwarding endpoint, настроенный оператором и ведущий к этому хосту. Проверено переключение на настоящий TCP byte relay. Ни provider, ни reverse tunnel, ни проброс порта/UPnP/ICE/STUN автоматически не создаются. Если маршрут до хоста отсутствует и relay не настроен, пользователь получает конкретный отказ. Выбор внешнего NAT/relay backend остаётся отдельным эксплуатационным решением.

## UI и эксплуатация

Multiplayer содержит URL каталога/имя, кнопку поиска, публикацию/Private, внешний адрес и optional relay; прямые игры остаются отдельным входом. Каталожные rows показывают совместимые матчи и роль. Invite вводится скрыто; host может явно показать/скопировать собственный invite. Настройки мира по-прежнему задаёт Host, финальная recipe/hash согласуется обычным Ready/Settings flow.

```sh
./tools/dev.sh match-directory --urls=http://127.0.0.1:17773 --signing-key=/tmp/openrts-directory-key.pem
./tools/dev.sh verify-directory --visible --output=/tmp/openrts-directory-ui
```

Для удалённого сервиса нужен HTTPS с доверенным сертификатом (обычная Kestrel certificate configuration). Неаутентифицированные forwarding headers не принимаются за источник IP/HTTPS. Сам сервис отклоняет non-loopback plaintext HTTP; клиент также запрещает такой URL. Редиректы HTTP-клиента отключены. Signing key следует сохранять при обычном restart.

Ограничения: request body16KiB, response клиенту≤1MiB;512матчей/4на account,4096sessions/challenges,256request IDs на match; API120запросов/IP/мин и200/с суммарно, bounded table4096IP,128соединений. HTTP deadlines5с включают чтение тела. Publisher останавливается с отменой и best-effort Close≤2с; TTL убирает потерянную запись. Секреты не пишутся в URL, journal, ToString или HTTP logs приложения.

Состояние каталога в памяти: restart теряет sessions/leases/reservations, не игровые миры. С тем же signing key client может заново войти и зарегистрировать host; durable сохранения резерваций и гарантий единственного незавершённого capability **через restart каталога** здесь нет. Хост всё равно не допускает двух активных peers одного slot. Rate limits не заменяют anti-Sybil, moderation или DDoS-инфраструктуру. UTC часы host/service должны быть согласованы. Внешние account providers, recovery ключей, автоматический NAT traversal, публичное развёртывание и массовая нагрузка не объявляются выполненными.

Технические основания: [ECDsa/P1363](https://learn.microsoft.com/en-gb/dotnet/api/system.security.cryptography.ecdsa.signdata?view=net-10.0), [Minimal APIs](https://learn.microsoft.com/en-gb/aspnet/core/fundamentals/minimal-apis?view=aspnetcore-10.0). Здесь используются явные DTO/BCL crypto, без внешнего IAM/БД/relay SDK.
