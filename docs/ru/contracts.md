# Контракты сервисов

`CloudSwitch.Contracts` содержит общие определения сообщений. Логика сервисов и хранение находятся в API, Worker и Notifications.

Оставшиеся решения и повестка встречи описаны в [обсуждениях готовности контрактов](discussions/README.md).

## Реализованные контракты

| Сообщение | Маршрут | Содержимое |
| --- | --- | --- |
| `DeploymentRequested` | API или Doctor → Deployer | Идентификаторы задания и снимок развёртывания. |
| `JobStatusChanged` | Deployer → Notifications | Предыдущее и текущее состояние, попытка, последовательность и необязательная безопасная ошибка. |
| `DeploymentHealthChanged` | Doctor → Notifications | Провайдер, переход здоровья, ID инцидента, последовательность и безопасная причина. |

Doctor и Deployer работают внутри Worker. См. [архитектуру](architecture.md).

События заданий содержат `EventId`, `CorrelationId`, `OccurredAt`, `JobId`, `DeploymentId` и `UserId`. Снимок при создании задания включает согласованные поля миграции, описанные ниже. Состояния `Pending`, `Running`, `Succeeded` и `Failed` сериализуются строками.

## Мониторинг здоровья и восстановление

`DeploymentMonitoringConfiguration` идентифицирует развёртывание, владельца, провайдера и endpoint. `HealthCheckPolicy` определяет HTTP-путь, интервал и тайм-аут в секундах, пороги последовательных сбоев и успешных проверок. Сервисы проверяют положительные значения, разрешённые HTTP(S)-адреса и перенаправления, успешность ответов приложения.

`RecoveryPolicy` по умолчанию использует `NotifyOnly`. `Automatic` требует одобренный `TargetConfigurationId`. Учётные данные хранятся вне политики.

Сервисы проверяют непустые идентификаторы, допустимые значения перечислений и положительную последовательность. Событие `Unhealthy` требует ID инцидента. C# `required` проверяет наличие поля, а проверка перечисленных инвариантов выполняется отдельно.

Doctor публикует `DeploymentHealthChanged` при сохранённых переходах между `Unknown`, `Healthy` и `Unhealthy`. Сбой подтверждается после заданного порога. Одному эпизоду отказа назначается один `IncidentId`, сохраняемый до подтверждённого восстановления и включаемый в событие восстановления. Последовательность растёт в пределах развёртывания при смене провайдера. Unknown не запускает миграцию. Неуспешная проверка приложения сама по себе не доказывает отказ всего провайдера.

- Notifications уведомляет владельца о подтверждённом сбое в обоих режимах.
- Doctor сохраняет одно задание миграции на инцидент и публикует `DeploymentRequested` в `Automatic`. В `NotifyOnly` автоматическое задание не создаётся.
- Перед действием Doctor проверяет текущую политику, активного провайдера, последовательность здоровья, инцидент и существующие задания. Устаревшее наблюдение не должно мигрировать уже восстановленное или перенесённое развёртывание.

Doctor принимает решение напрямую. Worker не использует `DeploymentHealthChanged` для создания ещё одного запроса. API и Doctor используют общие правила надёжного создания задания и запроса.

API хранит мониторинг в общей PostgreSQL и читает здоровье и прогресс напрямую. Событие наподобие `DeploymentMonitoringConfigurationChanged` предложено для обновлений конфигурации, однако его схема и доставка репликам [обсуждаются](discussions/doctor-configuration.md). Оно пока не реализовано и не входит в согласованную топологию очередей.

Реализация мониторинга запланирована в [#14](https://github.com/project-cloud-switch/CloudSwitch/issues/14).

## Контракты миграции, согласованные и реализованные в #2

Запрос и прогресс миграции определены в обсуждениях [deployment-request](discussions/deployment-request.md) и [job-progress](discussions/job-progress.md). Связанная задача [#2](https://github.com/project-cloud-switch/CloudSwitch/issues/2).

- `DeploymentRequested` — общий запрос API и Doctor с `Trigger` (`Manual` или `Automatic`), необязательным `IncidentId` для дедупликации и неизменяемым снимком `DeploymentSpecification`.
- `DeploymentSpecification` включает закреплённый образ с тегом или sha256 digest, `ContainerPort`, `ImagePullSecretReference` со ссылкой Vault, ресурсы `CpuCores` и `MemoryMb`, словарь несекретных `Env` и словарь `SecretReferences` (`vault://path#key`). Открытые учётные данные исключены из сообщений брокера и логов. Инфраструктура цели задаётся `TargetConfigurationId` со ссылкой PostgreSQL и одобренным `TerraformModule`. Для проверки и переключения используются `HealthCheck` (`HealthCheckPolicy`), `EntrypointDomain` и `TrafficRoutingProvider`.
- `JobStatusChanged` содержит строго возрастающий в пределах задания `Sequence`, `Attempt`, `PreviousStatus`, `Status`, `Phase` (`MigrationPhase` со значениями `Pending`, `Provisioning`, `TargetValidation`, `TrafficCutover`, `EntrypointVerification`, `Completed`), прямой `TargetEndpoint`, очищенные `ErrorCode` и `ErrorMessage`.
- Worker автономно создаёт инфраструктуру и выполняет проверки по снимку. Обращения к отказавшему исходному облаку при выполнении запрещены.

## Правила обмена сообщениями

Используется JSON MassTransit. ID корреляции сохраняются стабильными, ID событий сохраняются при повторной доставке. Worker дедуплицирует задания по `JobId`, получатели состояния используют `EventId` и `Sequence` задания для отбрасывания дубликатов и устаревших обновлений. Состояние и исходящие события сохраняются вместе через outbox.

Учётные данные передаются ссылками. Сервисы проверяют ввод и владение ресурсами. Несовместимые изменения схемы или идентичности сообщений согласуются. JSON-фикстуры служат тестами совместимости.

## Базовая топология RabbitMQ

| Exchange сообщения | Очередь получения |
| --- | --- |
| `CloudSwitch.Contracts.Events:DeploymentRequested` | `cloudswitch-worker-deployment-requested` |
| `CloudSwitch.Contracts.Events:JobStatusChanged` | `cloudswitch-notifications-job-status-changed` |
| `CloudSwitch.Contracts.Events:DeploymentHealthChanged` | `cloudswitch-notifications-deployment-health-changed` |

Реплики Deployer совместно используют очередь запросов. Notifications имеет отдельные очереди здоровья и состояния и не конкурирует с Deployer за запросы. Bindings создаются до публикации. Каждый сервис настраивает ограниченные повторы и очереди ошибок.

Тесты проверяют совместимость JSON, идентичность сообщений, доставку и корреляцию через MassTransit 8.5.8. Необязательные изолированные тесты PostgreSQL/RabbitMQ покрывают доставку outbox API в Worker, сохранённый прогресс, откат транзакции и fencing аренд. Локальное выполнение использует детерминированную симуляцию. Облачное создание инфраструктуры и реальные HTTP/DNS-адаптеры ожидают реализации.
