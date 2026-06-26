# Remediation report

## Что было исправлено
- Добавлено ожидание готовности OpenCode session перед continuation: runner перечитывает `GetSessionStatusAsync`, не отправляет continuation при `Busy`/`Retry`, ждёт `Idle` и останавливается в `NeedsManualIntervention` при невозможности дождаться безопасного статуса.
- Расширена модель outcome для OpenCode step: добавлены `RecoverableToolAbort`, `RecoverableTransportError`, `PermissionRequest`, `QuestionRequest`, `BuiltInRetryInProgress`, `NonRecoverableError` и другие уточняющие состояния.
- CLI fallback теперь анализирует stdout/stderr и JSON-line output от `--format json` даже при exit code `0`; `Tool execution aborted` / `terminated` больше не маскируются как success.
- Добавлена безопасная permission/question policy: `PermissionPolicy.Manual` по умолчанию, `AutoRespondToRecoverableQuestions` выключен по умолчанию.
- Подключён `Console.CancelKeyPress` к cancellation token, чтобы Ctrl+C приводил к управляемой отмене через существующий cancellation flow.
- Архивирование task сделано более идемпотентным: если source уже перемещён в completed и hash совпадает, resume может завершить `CompletedPendingArchive` без ручного восстановления.
- Добавлены тесты на busy/retry gate перед continuation, permission request, CLI JSON-line interruption при exit code `0`, classifier для permission/question.
- README и example config обновлены под новое поведение.

## Какие audit findings закрыты
| Finding | Статус | Файлы |
| --- | --- | --- |
| Continuation отправлялся без проверки `Idle` | Закрыто | `src/OpenCodeQueue.Core/Workflow/QueueUseCases.cs`, `tests/OpenCodeQueue.Tests/QueueUseCasesTests.cs` |
| Permission/question policy отсутствовала | Закрыто частично | `src/OpenCodeQueue.Core/Configuration/OpenCodeSettings.cs`, `src/OpenCodeQueue.Core/OpenCode/OpenCodeStepResultClassifier.cs`, `src/OpenCodeQueue.Core/Workflow/QueueUseCases.cs`, tests |
| CLI fallback считал exit code `0` success при `terminated`/`Tool execution aborted` | Закрыто | `src/OpenCodeQueue.Infrastructure/OpenCode/OpenCodeCliClient.cs`, `tests/OpenCodeQueue.Tests/OpenCodeCliClientTests.cs` |
| Ctrl+C использовал `CancellationToken.None` | Закрыто | `src/OpenCodeQueue.Cli/Program.cs` |
| CLI JSON output не разбирался line-oriented | Закрыто для диагностической классификации | `src/OpenCodeQueue.Infrastructure/OpenCode/OpenCodeCliClient.cs` |
| Partial archive move мог оставить `CompletedPendingArchive` без идемпотентного завершения | Закрыто частично | `src/OpenCodeQueue.Infrastructure/Files/FileSystemArchiver.cs` |

## Что осталось нерешённым
- Полный OpenCode permission auto-approve не реализован, потому что текущий adapter не имеет отдельного API для подтверждения конкретного permission request. При `PermissionPolicy.Manual` run безопасно останавливается; `AutoApprove` является явным opt-in, но фактическое подтверждение зависит от возможностей OpenCode/agent behavior.
- CLI adapter по-прежнему не может достоверно определить live session status; `Unknown` допускается как fallback, чтобы не ломать CLI recovery, но Server API остаётся предпочтительным для unattended.
- Corrupted state fallback через `.bak` не добавлялся; atomic write уже используется, а полноценный repair flow требует отдельной команды/UX.
- Fallback с Server API на CLI при `OpenCodeClientException` не требовал переписывания в рамках этого remediation; риск задокументирован в audit как Medium.

## Изменения в архитектуре
- Архитектурные границы сохранены: Core содержит policy/classification/workflow, Infrastructure содержит CLI/file implementations, CLI содержит запуск и cancellation wiring.
- Новых внешних зависимостей не добавлено.
- `QueueUseCases` получил session-readiness helper для continuation; отдельный сервис не выделялся, чтобы не раздувать remediation.

## Изменения в state/config формате
- В `ResilienceSettings` добавлено поле `permissionPolicy` с safe default `Manual`.
- `autoRespondToRecoverableQuestions` теперь по умолчанию `false`. Это безопасное изменение поведения: вопросы пользователя больше не считаются auto-resolvable без явного opt-in.
- State/manifest schema не ломался; существующие JSON без новых полей продолжают десериализоваться с defaults.

## Recovery/terminated поведение после исправлений
- `Tool execution aborted`, `terminated`, `process terminated`, network/timeout markers классифицируются как recoverable, а не success.
- После recoverable interruption attempt сохраняется в manifest, исходный prompt не переотправляется, task не архивируется.
- Перед continuation runner перечитывает session status и ждёт, пока session перестанет быть `Busy`/`Retry`.
- Continuation отправляется в ту же сохранённую `sessionId`.
- Повтор одинаковой signature и лимиты attempts по-прежнему останавливают run как `NeedsManualIntervention`.

## Permission/question поведение после исправлений
- Permission request классифицируется отдельно и при default policy переводит run в `NeedsManualIntervention`.
- Question request классифицируется отдельно и при `autoRespondToRecoverableQuestions = false` переводит run в `NeedsManualIntervention`.
- Dangerous/permissive режим не включён по умолчанию.

## Документация
- README обновлён: описаны ожидание `Idle`, safe default permissions/questions, CLI JSON-line classification и исправлен пример numbered prompt.
- Runtime config дополнен блоком `resilience` с `permissionPolicy: Manual` и `autoRespondToRecoverableQuestions: false`.
- Старые названия проекта в изменённых документах не добавлялись.

## Проверки
- dotnet restore: успешно.
- dotnet build: успешно, 0 warnings, 0 errors; SDK сообщил информационное `NETSDK1057` о preview .NET SDK.
- dotnet test: успешно, 120 passed, 0 failed, 0 skipped.
- дополнительные проверки: `dotnet format --verify-no-changes` успешно; `git status --short`, `git diff --stat`, `git diff --name-status` проверены.

## Риски
- Server API DTO OpenCode могут отличаться от предположений клиента; classifier имеет textual fallback, но интеграционный тест с реальным OpenCode server всё ещё нужен.
- CLI fallback остаётся менее надёжным для unattended recovery, потому что не предоставляет достоверный busy/retry/idle status.
- `PermissionPolicy.AutoApprove` не должен включаться без отдельной проверки поведения конкретной OpenCode версии и проекта.
