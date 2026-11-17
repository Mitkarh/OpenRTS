# 046. Проверки пунктов5–8 аудита NET1–NET5

**Дата:** 17ноября2026. macOS/Arm64, .NET10, Godot4.7.2.NET, Debug. [Контракт046](../decisions/046-network-directory-diagnostics.md), [план](../research/networking/roadmap.md).

## Сборка и executable tests

`./tools/dev.sh test --filter=session`: весь solution собран без warnings/errors; Network81/81, DeclarativeContent20/20, Gameplay session6/6. Фильтр session не ограничивает сетевой набор. Финальный sandbox также собран отдельно без warnings/errors после изменения native verifier.

Добавлено восемь сетевых проверок к предыдущим73:

- Реальные HTTP/TLS подключения player и spectator к2/4/8местам, с AI и closed при capacity>2. Клиенты не передают playerCount вручную: значение берётся из подписанного grant. Проверены исходный metadata count, одинаковый полный состав, итоговый отчёт, побайтное совпадение журналов и offline replay.
- Подмена count в grant отвергается host до занятия слота/погашения ticket; исходный grant после этого успешно подключается. Отдельная подмена HTTP-ответа отвергается MatchDirectoryClient при сверке с подписью. Сервис отвергает отсутствующую/недопустимую capacity, слот за её пределами, protocol4 и изменение capacity через heartbeat.
- Confirm503 сохраняет первый receipt, повторяет именно его; последующий404 отменённой резервации не блокирует следующий успешный receipt. Итог:4попытки на3receipt, корректные public reservation counts. В период отказа Confirm реальные игроки продолжают тики и затем завершают replay с тем же отчётом. Тестовый DelegatingHandler сохраняет реальные account/HTTP/TLS пути; он меняет один ответ503, а404 приходит от настоящего сервиса после Cancel.
- LatencyProbe на управляемых монотонных часах даёт37мс/12мс только для совпадающих nonce. Чужой, повторный и старый ответ игнорируются. Parser отвергает старый однобайтовый Ping, неверный kind/nonce, неканонические/повторные ID Waiting, неверный тик/время и trailing bytes.
- Настоящий peer не присылает обязательный batch: host и здоровый client показывают ID3 и следующий тик3; тик остаётся2, таймер Waiting растёт, RTT здорового соединения измеряется. После отключения нарушителя общая пауза снимает Waiting, затем матч продолжается, журналы совпадают и воспроизводятся. Прежний тест heartbeat, не продлевающего command deadline, также проходит.
- Старый protocol4 отвергается до admission; лобби и свободный слот остаются доступны.

В набор входят прежние регрессии045: malformed spectator/guest, настоящий desync, TTL/Close, журнал100001тик, runtime snapshots и rejoin. Новая диагностика не попадает в согласованную историю.

## Native: задержанное окно и barrier меню

`verify-network --net3 --spectator --slow-client-ms=1800`: три human, одна AI сторона и spectator, четыре TLS-процесса. Замедлены только UI-проверки join3 и watch; owner threads продолжают симуляцию. Host завершил меню на тике24, задержанные участники — на69; все наблюдали общую паузу69 и затем продолжили. Итоговый hash всех:

`9FA674C093DB3239AD9B9F329518309B5CA78E05A390FC39F7CAF8BB163CD1DD`.

Ожидаемый spectator должен подключиться до Start; подтверждения меню приходят от каждого участника до Pause. Проверка измеренного RTT в настоящей статусной строке обязательна. Все файлы независимо воспроизведены.

`verify-network --slow-client-ms=1800` проверяет тот же порядок перед Stop для двух окон: host menu26, guest menu71. Оба успешно завершают матч с hash `3E90067185F2E69612FF21E85CEB494F648E5C55D44D87F18146A3E5B93542A6`. Это предотвращает завершение теста до подготовки более медленного UI.

## Native: каталог, интерфейс и rejoin

`verify-directory`: отдельный HTTP service, два Godot клиента, реальная публикация/поиск/допуск/игра/replay. Проверочные записи на2/8мест видны; все четыре реальные кнопки Join/Watch отключены, показано объяснение ограничения sandbox. Поддерживаемый матч на4места запускается через каталожную кнопку. Hash:

`AE563A674639B4BB649B097B219736871419D0D61F11EC71DE999400D402EE1D`.

Отдельный visible прогон тоже успешен. Снимки браузера и матча просмотрены: строки/кнопки читаемы, в бою виден измеренный RTT6мс. Это наблюдение loopback, не latency budget WAN. Снимки остаются в `/tmp/openrts-net-rest-directory/`; ключ сервиса и игровые файлы не копируются в репозиторий.

`verify-network --reconnect`: настоящий TCP break → штатная кнопка → epoch2 → Resume → дальнейшие тики и RTT. Хост и вернувшийся клиент дали hash `061EE025AEA8B39335D29DBF839065C7C8578BB275408FED1F9798B29DF43D2F`; driver проверяет также побайтное совпадение завершённых журналов.

Replay UI отдельно проверяется через `verify-replay`: recipe, pause/step/speed, seek в обе стороны, POV, read-only input и финальный hash. Старый StarJournal, записанный до protocol5, проверяется текущим native replay; wire/claims изменены, формат истории сохранён.

## Воспроизведение и границы

После `./tools/dev.sh build` native drivers можно запускать напрямую из корня:

```sh
python3 tools/verify_network.py ../Godot_mono.app/Contents/MacOS/Godot --net3 --spectator --slow-client-ms=1800
python3 tools/verify_network.py ../Godot_mono.app/Contents/MacOS/Godot --slow-client-ms=1800
python3 tools/verify_directory.py ../Godot_mono.app/Contents/MacOS/Godot --visible
python3 tools/verify_network.py ../Godot_mono.app/Contents/MacOS/Godot --reconnect
python3 tools/verify_replay.py ../Godot_mono.app/Contents/MacOS/Godot
```

Локальные логи: `/tmp/openrts-net-rest-final-tests.log`, `/tmp/openrts-net-rest-native-build.log`; native результаты — `/tmp/openrts-net-rest-slow-ui-final/`, `slow-two/`, `directory-final/`, `rejoin-final/`, `replay-final/` с тем же префиксом; старый replay — `/tmp/openrts-net-rest-legacy-replay.log`.

Полный Gameplay набор с восемью известными ошибками повторно не запускался; он не объявляется зелёным. Физический WAN/NAT, новые kernel-netem/cross-platform/fuzz прогоны, массовая нагрузка каталога и стоимость больших боевых snapshots не проверялись. Protocol5/claims v2 требуют обновить обе стороны и каталог вместе; это не trusted mode и не серверная аналитика.
