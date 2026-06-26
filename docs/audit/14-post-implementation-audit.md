# Постреализационный аудит OpenCodeQueue

## Итоговая оценка
- Готовность: 78%
- Архитектурная оценка: структура `Cli/Core/Infrastructure/Tests` выдержана, бизнес-логика в основном вынесена в Core, зависимости направлены корректно.
- Главные риски: recovery отправляет continuation без явной проверки `Idle`, permission/question policy практически не реализована, CLI fallback имеет ограниченную диагностику session/status и не парсит JSON stream как поток событий.
- Можно ли запускать unattended: условно; только на тестовом/контролируемом проекте, с чистым git state и предпочтительно через Server API, не через CLI fallback.

## Проверенные команды
- dotnet restore: успешно, все проекты обновлены для восстановления.
- dotnet build: успешно, 0 warnings, 0 errors; SDK вывел информационное `NETSDK1057` о preview .NET SDK 10.0.200-preview.
- dotnet test: успешно, 115 passed, 0 failed, 0 skipped.
- дополнительные проверки: `git status --short`, `git diff --stat`, `git diff --name-status` до отчёта были пустыми; `git diff --name-status HEAD~5..HEAD` показал добавление текущей реализации и примеров без удалённых файлов.

## Соответствие prompts 01-13
| Prompt | Статус | Комментарий |
| --- | --- | --- |
| 01 | Частично/выполнено | Базовое .NET 9 solution создано: `OpenCodeQueue.sln`, `src/OpenCodeQueue.Cli`, `Core`, `Infrastructure`, `tests`. |
| 02 | Выполнено | Multi-project registry реализован через `opencode-queue.json`, `activeProjectId`, project profiles и `--project`. |
| 03 | Выполнено | Markdown discovery разделяет `prompts` и `quality`, поддерживает числовые сегменты и детерминированную сортировку. |
| 04 | Частично/выполнено | JSON state, manifests, atomic write, lock и `CompletedPendingArchive` есть; есть риски вокруг partial move и corrupted state UX. |
| 05 | Частично | Server API client реализован, sessionId сохраняется и используется; нет event stream, есть риск несовпадения реального API shape. |
| 06 | Частично | CLI fallback передаёт `--dir`, `--session`, не использует `--continue`; но JSON stream не разбирается как event stream, статус session недостоверен. |
| 07 | Выполнено | Workflow `task -> quality* -> archive` реализован, run-once/run-all/resume/status/list/doctor/abort есть. |
| 08 | Частично | `terminated` и `Tool execution aborted` классифицируются как recoverable; continuation есть, но нет явной проверки `Idle` перед continuation. |
| 09 | Не выполнено | Permission request и user questions не имеют полноценной domain/config policy; есть только marker `NEEDS_MANUAL_INTERVENTION:` и настройка `AutoRespondToRecoverableQuestions`, которая фактически не используется как policy. |
| 10 | Частично/выполнено | Основной console UX на русском. Допускаются technical identifiers; явных старых английских UX-блоков не найдено, кроме технических terms. |
| 11 | Выполнено | 115 тестов покрывают sorting, registry, state, lock, workflow, server/CLI clients, interruptions, русские сообщения. Есть пробелы по permission policy и busy/retry continuation. |
| 12 | Частично/выполнено | `README.md`, examples и architecture notes обновлены; отдельной `docs/` структуры до аудита не было. |
| 13 | Выполнено | Рабочее дерево чистое до отчёта, случайных локальных удалений не обнаружено. |

## Critical findings
Критических блокеров компиляции, тестов или очевидной потери данных не обнаружено.

## High findings
- `src/OpenCodeQueue.Core/Workflow/QueueUseCases.cs:491-508`: после recoverable interruption runner сразу строит и отправляет continuation prompt. Нет явной проверки `openCodeClient.GetSessionStatusAsync(...).State == Idle` и нет ожидания выхода из `Busy`/`Retry`. Это нарушает требование не отправлять continuation, пока OpenCode session ещё busy или во встроенном retry, и может создать гонку сообщений в одной session.
- `src/OpenCodeQueue.Core/Configuration/OpenCodeSettings.cs:80` и `src/OpenCodeQueue.Core/OpenCode/OpenCodeStepResultClassifier.cs:105-113`: permission requests и user questions не различаются как отдельные состояния. Нет `PermissionRequest`, `QuestionRequest`, permission policy, allowlist/denylist или явного auto-approve механизма. Опасные действия не auto-approved, но причина в отсутствии реализации, а не в осознанной policy.
- `src/OpenCodeQueue.Infrastructure/OpenCode/OpenCodeCliClient.cs:57-84`: CLI fallback считает exit code `0` успешным и не анализирует stdout/stderr на recoverable markers при нулевом коде. Если `opencode run --format json` вернёт event stream с `terminated`/tool abort и exit code `0`, step будет ошибочно завершён как success.
- `src/OpenCodeQueue.Cli/Program.cs:20-21`: приложение запускается с `CancellationToken.None`. Ctrl+C не подключён к graceful cancellation, поэтому требование корректной обработки cancellation без порчи state выполнено только частично за счёт atomic writes/lock dispose при нормальном unwinding, но не как явная UX/cleanup логика.

## Medium findings
- `src/OpenCodeQueue.Infrastructure/OpenCode/OpenCodeCliClient.cs:151-183`: JSON output CLI парсится через `ExtractJson` как один документ или последняя JSON-строка. Это не полноценный streaming parser для `--format json`, поэтому события могут быть потеряны или неправильно классифицированы.
- `src/OpenCodeQueue.Infrastructure/OpenCode/OpenCodeCliClient.cs:50-89`: CLI adapter возвращает `Unknown` для session details/status и не может доказуемо проверить busy/retry/idle. Recovery через CLI безопаснее, чем `--continue`, но всё ещё недостаточно надёжен для unattended continuation.
- `src/OpenCodeQueue.Infrastructure/Files/FileSystemArchiver.cs:23-27`: task archive move не журналируется как отдельная двухфазная операция до `File.Move`. Если процесс упадёт после move, но до сохранения completed state, `CompletedPendingArchive` может остаться active, а source prompt уже исчезнет; повторный resume вернёт archive error и потребует ручной проверки.
- `src/OpenCodeQueue.Infrastructure/State/JsonStateStore.cs:64-75`: partial/corrupted `state.json` и `manifest.json` безопасно не игнорируются, но UX восстановления ограничен exception. Нет automatic fallback на `.bak` или последнюю валидную версию.
- `src/OpenCodeQueue.Infrastructure/State/FileRunLock.cs:21-36`: stale lock удаляется только если PID точно не существует на той же машине. После power loss это обычно достаточно, но lock от другой machine или повреждённый lock может блокировать запуск до ручного вмешательства.
- `src/OpenCodeQueue.Infrastructure/OpenCode/OpenCodeFallbackClient.cs:51-76`: fallback на CLI происходит при любом `OpenCodeClientException`, кроме project mismatch. Это удобно, но может скрыть semantic server errors и перевести workflow в менее надёжный CLI mode без явного пользовательского подтверждения.
- `src/OpenCodeQueue.Core/Workflow/QueueUseCases.cs:359-374`: при `StopOnQualityFailure = false` failed quality steps сохраняются и после цикла весь run всё равно становится `Failed`; это логично для no-archive policy, но название настройки может вводить в заблуждение.

## Low findings
- В README строка с примером `0.1. auth.md` отличается от требуемого примера `0.1 task.md`, но поддерживаемый parser покрывает оба формата.
- В консольных сообщениях встречаются technical terms `run`, `quality`, `promptsDir`, `session id`, `activeRunId`. Это допустимо по требованиям, но для полностью русскоязычного UX можно добавить пояснения.
- `docs/` отсутствовала до этого аудита; основная документация находилась в `README.md`, `ARCHITECTURE-NOTES.md` и demo examples.

## Архитектурные замечания
- Архитектура чистая: `Core` содержит domain/config/workflow/ports, `Infrastructure` содержит file state, archiver, prompt repository, OpenCode server/CLI clients, DI, discovery, `Cli` содержит parsing/menu/console presentation.
- `Core` не зависит от `Infrastructure`; DTO OpenCode API в основном локализованы в `OpenCodeServerClient`, domain модели находятся в `OpenCodeModels.cs`.
- `QueueUseCases.cs` стал крупным orchestration class на 857 строк. Это не монолитный `Program.cs`, но дальнейшее развитие recovery/permission policy лучше вынести в отдельные компоненты: session readiness gate, recovery policy, permission/question classifier.
- Глобальных static service-locator сервисов не обнаружено; DI используется через `Microsoft.Extensions.DependencyInjection`.
- `DependencyInjection` регистрирует `OpenCodeServerClient` singleton с `HttpClient Timeout = 20 minutes`; это может конфликтовать с `StepTimeoutMinutes = 90`, если один HTTP call держится дольше 20 минут.

## Логические ошибки workflow/recovery
- Положительное: task архивируется только после task и всех quality steps; quality prompts snapshot-ятся и остаются на месте; active run блокирует выбор новой task.
- Положительное: `CompletedPendingArchive` реализован и обрабатывается в `ResumeAsync` без повторного выполнения prompt.
- Положительное: исходный prompt не переотправляется при recoverable interruption; используется continuation payload в той же `sessionId`.
- Риск: continuation не ждёт `Idle`, не различает `BuiltInRetryInProgress` на уровне workflow и не делает backoff для busy/retry server status.
- Риск: lost session трактуется как manual intervention, а не success, что правильно; но CLI adapter не умеет доказать lost/busy session.
- Риск: classifier содержит `aborted`, `cancelled`, `canceled` как recoverable markers. Это может неверно восстановить пользовательскую осознанную отмену, если OpenCode сообщает её теми же словами.
- Риск: `AutoRespondToRecoverableQuestions` присутствует в config model, но не участвует в classifier/orchestrator; вопросы пользователя не имеют отдельного flow.
- Риск: нет явного теста, что continuation не отправляется при `Busy`/`Retry`; это ключевой пробел для resilience.

## Документация
- README хорошо описывает назначение, `prompts`, `quality`, `.queue`, первый старт, registry, active project, run/resume/status/list/doctor, session id, Server API vs CLI fallback, archive, recovery и troubleshooting basics.
- На момент аудита demo examples присутствовали; позже runtime folders перенесены в корень проекта.
- Поиск старых названий проекта в Markdown не нашёл случайных остатков.
- Недостаточно документированы фактические ограничения permission requests/questions, busy/retry continuation и CLI fallback event-stream parsing.

## Проверка на лишние удаления
- До создания этого отчёта `git status --short`, `git diff --stat`, `git diff --name-status` были пустыми.
- `git diff --name-status HEAD~5..HEAD` показывает в основном добавление solution, source, tests, examples и README/ARCHITECTURE updates; удалённых файлов (`D`) в проверенном диапазоне не обнаружено.
- Prompts, quality, docs, tests и config присутствуют. Случайных удалений не выявлено.

## Рекомендованный remediation plan
1. Добавить session readiness gate: перед continuation вызывать `GetSessionStatusAsync`, ждать `Idle`, распознавать `Busy`, `Retry`, timeout и переводить повторяющийся retry в `NeedsManualIntervention`.
2. Ввести отдельный classifier/policy для `PermissionRequest`, `QuestionRequest`, `RecoverableToolAbort`, `RecoverableTransportError`, `BuiltInRetryInProgress`, `NonRecoverableError`; добавить config для permission policy с safe default `manual`.
3. Исправить CLI fallback parser: читать `--format json` как JSON lines/event stream, классифицировать semantic errors даже при exit code `0`, покрыть тестами `terminated` в stdout при success exit code.
4. Подключить graceful Ctrl+C: `Console.CancelKeyPress`, cancellation token, русское сообщение, сохранение текущего manifest state без запуска следующего prompt.
5. Усилить archive recovery: записывать pre/post archive metadata или event перед `File.Move`, чтобы resume мог распознать уже перемещённый source и завершить `CompletedPendingArchive` идемпотентно.
6. Добавить corrupted state recovery strategy: `.bak` при atomic replace или диагностическая команда repair/manual recovery.
7. Документировать фактические ограничения Server API/CLI и permission policy после реализации remediation.

## Что НЕ исправлялось в этом prompt
- Не менялась архитектура, workflow, clients, CLI commands или публичное поведение.
- Не добавлялись новые tests или production code.
- Не исправлялись recovery gaps, permission policy, Ctrl+C и CLI stream parsing.
- Изменён только файл аудита: `docs/audit/14-post-implementation-audit.md`.
