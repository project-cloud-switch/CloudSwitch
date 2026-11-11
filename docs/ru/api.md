# Рабочий API

API поддерживает аутентификацию Keycloak, конфигурацию развёртываний и целей с проверкой владельца, политики восстановления, чтение здоровья и надёжное создание заданий миграции. Согласованные обсуждения [запроса](discussions/deployment-request.md) и [прогресса](discussions/job-progress.md) определяют сообщения. [Решения API v1](decisions/api-v1.md) описывают общую схему. Worker и Notifications реализуются отдельными сервисами.

## Локальный запуск

Требуются .NET 10 и Docker.

```sh
docker compose up -d --wait
dotnet run --project src/CloudSwitch.Api -- --migrate
dotnet run --project src/CloudSwitch.Api
```

| Сервис | Адрес | Доступ при разработке |
| --- | --- | --- |
| API | `http://localhost:5080` | Bearer-токен |
| OpenAPI | `http://localhost:5080/openapi/v1.json` | Только Development |
| Keycloak | `http://localhost:8080` | Администратор `admin` / `cloudswitch-dev` |
| Пользователь realm Keycloak | realm `cloudswitch` | `developer` / `cloudswitch-dev` |
| Управление RabbitMQ | `http://localhost:15672` | `cloudswitch` / `cloudswitch-dev` |
| PostgreSQL | `localhost:55432`, БД `cloudswitch` | `cloudswitch` / `cloudswitch-dev` |

Порты привязаны к loopback. Учётные данные в репозитории предназначены для разработки. Именованные тома сохраняют данные, `docker compose stop` останавливает сервисы с сохранением данных. Keycloak импортирует отсутствующий realm, но не перезаписывает существующий при изменении файла импорта. Определения RabbitMQ создают пользователя разработки и постоянную топологию до публикации. Стабильный hostname сохраняет идентичность узла при пересоздании.

Корневой `compose.yaml` использует конфигурацию разработки из `docker/keycloak` и `docker/rabbitmq`. Автоматизация развёртывания управляет production-конфигурацией сервисов и учётными данными. Схема и права БД принадлежат `CloudSwitch.Persistence`.

## Аутентификация

Realm содержит браузерный клиент `cloudswitch-web` с authorization code и PKCE, перенаправлениями под `http://localhost:3000/`. Локальный конфиденциальный клиент `cloudswitch-dev` поддерживает client credentials для CLI-разработки. Его тестовый секрет — `cloudswitch-dev-secret`. Сервисные учётные записи владеют своими ресурсами, браузерные пользователи имеют отдельное владение.

Получение токена доступа для разработки

```sh
curl -s http://localhost:8080/realms/cloudswitch/protocol/openid-connect/token \
  -d grant_type=client_credentials \
  -d client_id=cloudswitch-dev \
  -d client_secret=cloudswitch-dev-secret
```

Передавайте `Authorization: Bearer <access_token>`. При первом аутентифицированном запросе API сопоставляет уникальную пару `(iss, sub)` с собственным UUID пользователя. `/api/v1/me` возвращает внутренний ID, issuer, subject и необязательное имя пользователя. Проверяются issuer, audience, подпись и срок действия, исходные имена claims сохраняются. Keycloak добавляет audience `cloudswitch-api` через mapper. Новые эндпоинты требуют аутентификацию по умолчанию. API не выпускает пароли и токены и не предоставляет обхода прав для администратора.

В production `Authentication__Authority` указывает HTTPS realm, а `Authentication__Audience` — audience API. `Authentication__RequireHttpsMetadata` можно отключить только в Development. Production-клиенты создаются отдельно от локального импорта. См. [валидацию JWT ASP.NET](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-10.0) и [контейнеры Keycloak](https://www.keycloak.org/server/containers).

## Эндпоинты

Все маршруты `/api/v1` требуют аутентификации. Чужие ресурсы возвращают 404. Списки принимают `offset` и `limit`, по умолчанию 50, максимум 100.

| Метод | Маршрут | Поведение |
| --- | --- | --- |
| GET | `/api/v1/me` | Получить или создать учётную запись приложения. |
| GET | `/api/v1/modules` | Получить модули, одобренные оператором. |
| POST / GET | `/api/v1/deployments` | Зарегистрировать или получить свои развёртывания. |
| GET / PUT / DELETE | `/api/v1/deployments/{id}` | Прочитать, заменить или мягко удалить конфигурацию. |
| GET | `/api/v1/deployments/{id}/health` | Текущее здоровье и активное окружение. Unknown до актуального наблюдения Doctor. |
| POST / GET | `/api/v1/deployments/{id}/targets` | Создать или получить резервные конфигурации. |
| GET / PUT / DELETE | `/api/v1/deployments/{id}/targets/{targetId}` | Прочитать, заменить или мягко удалить цель. |
| POST | `/api/v1/deployments/{id}/jobs` | Надёжно создать ручную миграцию. |
| GET | `/api/v1/jobs?deploymentId={id}` | Получить свои задания с необязательным фильтром по развёртыванию. |
| GET | `/api/v1/jobs/{id}` | Прочитать сохранённый прогресс и снимок запроса. |
| GET | `/api/v1/jobs/{id}/events?afterSequence=0` | Прочитать упорядоченные события, также принимает `limit`. |
| GET | `/health/live` | Анонимная проверка работоспособности процесса. |
| GET | `/health/ready` | Анонимная проверка БД, схемы и брокера. При сбое 503. |

Ответы GET, создания и обновления развёртывания или цели включают `ETag: "<revision>"`. PUT и DELETE требуют это значение в `If-Match`. Отсутствие заголовка возвращает 428, устаревшая ревизия — 412. При активном задании конфигурация не меняется, возвращается 409. Мягкое удаление сохраняет историю и инфраструктуру источника. Перед удалением цели нужно убрать ссылку на неё из политики восстановления.

Ручной запрос требует один заголовок `Idempotency-Key` длиной 1–128 символов. Первый принятый запрос возвращает 202 и `Location`. Повтор с теми же развёртыванием, ключом и целью возвращает 200 и исходное задание, в том числе после изменений конфигурации. Другая цель с тем же ключом возвращает 409. Другое активное задание также возвращает 409. Необязательный `X-Correlation-ID` должен быть непустым UUID. Его сохранённое значение возвращается клиенту и передаётся в сообщения.

Ошибки используют Problem Details с ID трассировки. Прикладные ошибки включают стабильный `code`. Клиентам запрещено записывать состояние выполнения и наблюдения здоровья.

Ответы ошибок имеют `application/problem+json`, в том числе при запросе другого формата через `Accept`. Временная недоступность БД возвращает 503 с `code` равным `database_unavailable`. Неожиданный сбой возвращает 500 с `internal_error`, диагностика записывается в серверные логи. OpenAPI описывает обязательные заголовки, аутентификацию и ответы ошибок операций.

Ограничения частоты используют фиксированные минутные окна. Каждая аутентифицированная пара `(iss, sub)` имеет бюджет 120 запросов, каждый анонимный IP соединения — 30. Попытки создания заданий дополнительно расходуют отдельный бюджет 5 запросов на пользователя для всех развёртываний. Обновление токена сохраняет бюджет идентичности. Отклонение возвращает 429, `Retry-After` в секундах и Problem Details с `code: rate_limit_exceeded`. Проверки работоспособности обходят лимиты.

Положительные лимиты настраиваются через `RateLimiting__AuthenticatedRequestsPerMinute`, `RateLimiting__AnonymousRequestsPerMinute` и `RateLimiting__JobSubmissionsPerMinute`. Счётчики принадлежат процессу API и сбрасываются при перезапуске. Запросы через один прокси разделяют бюджет его анонимного IP. Общий шлюз может применять бюджеты для нескольких экземпляров API. Используется [middleware ограничения частоты ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0).

## Параметры приложения и цели

POST `/api/v1/deployments`

```json
{
  "name": "demo",
  "definition": {
    "environment": "development",
    "sourceProvider": "yandex",
    "sourceEndpoint": "https://app.example.test",
    "application": {
      "image": "nginx@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
      "port": 80,
      "cpuMillicores": 500,
      "memoryMegabytes": 256,
      "settings": {"LOG_LEVEL": "info"},
      "secretReferences": {"API_KEY": "vault://demo#api-key"}
    },
    "healthCheck": {
      "path": "/health",
      "intervalSeconds": 10,
      "timeoutSeconds": 3,
      "failureThreshold": 3,
      "recoveryThreshold": 2
    },
    "traffic": {"driver": "simulation", "recordName": "app.example.test"}
  }
}
```

Образ принимает явный тег или полный SHA-256 digest. Перед облачным выполнением замените тестовый digest реальной ссылкой. Необязательный `application.imagePullSecretReference` содержит ссылку Vault на доступ к реестру. Мониторинг включён по умолчанию, восстановление использует NotifyOnly. HTTP(S)-адрес источника не содержит учётных данных, query или fragment и должен соответствовать `Endpoints:AllowedHosts`. Локальные примеры разрешают `localhost`, `127.0.0.1` и `app.example.test`. Doctor/Deployer должны проверять разрешённые адреса после DNS-разрешения и перенаправлений при выполнении.

Путь здоровья локален для приложения. Интервал 1–3600 секунд, тайм-аут 1–60 секунд и не больше интервала, пороги 1–100. CPU 100–64000 millicores, память 64–262144 МБ. Драйверы трафика `simulation` и `cloudflare-dns`, последний требует ссылку на зону и ссылку на секрет доступа. Эти параметры описывают будущее выполнение, изменения инфраструктуры через сам API не выполняются.

POST `/api/v1/deployments/{id}/targets`

```json
{
  "provider": "local",
  "moduleId": "local-stateless-v1",
  "inputs": {"region": "local"},
  "secretReferences": {"cloud": "vault://demo#cloud"}
}
```

API определяет источник и ревизию модуля по списку оператора `Terraform:Modules`. Произвольные HCL, источники модулей и необъявленные параметры запрещены. Production должен одобрять реальные модули с неизменяемыми ревизиями. Локальная запись резервирует идентичность модуля симуляции для разработки Worker. Учётные данные хранятся в хранилище секретов и передаются ссылками `vault://path#key`. Настройки и параметры модулей не предназначены для значений секретов. Ссылки разрешаются при выполнении.

Для Automatic выполните PUT развёртывания с ETag и задайте `definition.recovery` равным `{"mode":"Automatic","targetConfigurationId":"<target UUID>"}`. Цель должна принадлежать этому развёртыванию и оставаться одобренной. Для ручного запроса передайте `{"targetConfigurationId":"<target UUID>"}` и `Idempotency-Key`.

## Хранение и развёртывание

`CloudSwitch.Persistence` владеет EF-моделью, миграциями и общим сервисом создания заданий API/Doctor. Задания и неизменяемые запросы фиксируются с PostgreSQL outbox MassTransit. Доставляются только зафиксированные сообщения, при недоступности брокера выполняются повторы. Прямой запрос может сохраняться при отказе брокера, пока readiness возвращает 503.

Индексы обеспечивают одну активную миграцию Pending/Running, уникальные ручные ключи и автоматические инциденты. Транзакционные advisory-блокировки сериализуют изменение конфигурации и создание заданий одного развёртывания. Автоматические запросы повторно проверяют политику, мониторинг, активный инцидент и последовательность, ревизии конфигурации и окружения в транзакции. Unknown не запускает восстановление.

Примените схему один раз с подключением владельца миграций.

```sh
dotnet run --project src/CloudSwitch.Api -- --migrate
```

В production задайте `ConnectionStrings__Postgres` с правами владельца миграций для этого процесса. Затем подготовка БД применяет `src/CloudSwitch.Persistence/DatabasePermissions.sql` с правами создания ролей и выдачи разрешений. Файл определяет NOLOGIN-роли API/Worker, которые выдаются отдельно созданным runtime-пользователям. API не обновляет выполнение, Worker не обновляет конфигурацию приложения и неизменяемые снимки. Локальный Compose использует владельца БД для удобства разработки. Обычный запуск не применяет миграции.

Создание миграций

```sh
dotnet tool restore
dotnet ef migrations add <Name> --project src/CloudSwitch.Persistence --startup-project src/CloudSwitch.Persistence
```

Фабрика design-time читает `ConnectionStrings__Postgres` либо использует локальное подключение. Runtime-настройки включают authority/audience, подключение PostgreSQL, `RabbitMq__Host`, `RabbitMq__Port`, `RabbitMq__VirtualHost`, `RabbitMq__Username`, `RabbitMq__Password`, разрешённые хосты и определения модулей. Используйте стандартные провайдеры конфигурации и секретов .NET. Управляющий контур, доступ к образам, секретам и управлению трафиком размещаются вне области отказа источника.

## Проверка и оставшиеся сервисы

Точные команды Release-сборки, фильтры unit/integration, проверка модели EF и проверка результатов интеграционных тестов для pull request и `main` описаны в [документации CI](../ci.md).

```sh
dotnet test CloudSwitch.sln
```

JWT-тесты, тесты хоста и контрактов по умолчанию обходятся без внешних сервисов. Тесты PostgreSQL пропускаются, если `CLOUDSWITCH_TEST_POSTGRES` не задаёт административное подключение разработки. Они создают случайную БД, применяют миграции, проверяют владение, ревизии, конкурентные запросы, откат outbox, автоматическое восстановление и роли, затем удаляют только свою тестовую БД. Их брокер намеренно недоступен для проверки надёжности сохранения. Пример запуска

```sh
CLOUDSWITCH_TEST_POSTGRES='Host=localhost;Port=55432;Database=cloudswitch;Username=cloudswitch;Password=cloudswitch-dev' dotnet test CloudSwitch.sln
```

Worker получает запросы из согласованной очереди RabbitMQ, захватывает и продлевает аренды PostgreSQL, сохраняет фазы, последовательность и контрольные точки, фиксирует историю состояния и исходящие события через MassTransit EF bus outbox. Целевое окружение активируется после проверенного завершения. Владельцы с истёкшей арендой не могут записывать прогресс. Прерванные переключения завершаются ошибкой с неопределённым результатом для ручной сверки.

Запустите `dotnet run --project src/CloudSwitch.Worker` для явной симуляции Development в изолированной локальной БД. Она принимает одобренную цель `local-stateless-v1:local-simulation-v1`. Создание инфраструктуры, проверка здоровья цели и трафика используют детерминированные адаптеры. Реальные Terraform, разрешение Vault, HTTP/DNS-адаптеры, мониторинг Doctor и Notifications ожидают реализации. Здоровье без наблюдений остаётся Unknown.

Тесты PostgreSQL также проверяют fencing Worker, сохранение последовательности после перезапуска, откат прогресса и outbox, чтение прогресса API. Для брокерного теста вместе с подключением PostgreSQL задайте `CLOUDSWITCH_TEST_RABBITMQ` равным `amqp://cloudswitch:cloudswitch-dev@localhost:5672`. Тест создаёт и удаляет случайный virtual host. Management API по умолчанию `http://localhost:15672`, переопределяется через `CLOUDSWITCH_TEST_RABBITMQ_MANAGEMENT`. Тест запускает Worker перед публикацией и ожидает проверенного завершения задания через API.
