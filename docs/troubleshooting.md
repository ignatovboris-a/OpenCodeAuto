# Troubleshooting OpenCodeQueue

## Где Смотреть Состояние

- `.queue/state.json` - active run и последний завершённый run.
- `.queue/events.jsonl` - хронология событий runner.
- `.queue/runs/<runId>/manifest.json` - manifest конкретного run и step-by-step статус.
- `.queue/runs/<runId>/snapshots/` - snapshot task и quality prompts.
- `.queue/runs/<runId>/attempts/` - stdout/stderr diagnostic logs CLI adapter, если использовался CLI fallback.

## После Power Off Или Kill Process

1. Не очищайте `.queue` автоматически.
2. Выполните `opencode-queue status --config opencode-queue.json`.
3. Проверьте `.queue/runs/<runId>/manifest.json` и `.queue/events.jsonl`.
4. Если active run безопасно продолжать, выполните `opencode-queue resume --config opencode-queue.json`.
5. Если нужно сознательно остановить run без удаления данных, выполните `opencode-queue abort --config opencode-queue.json` и подтвердите действие.

## Почему terminated Не Success

`terminated`, `Tool execution aborted`, `process terminated`, timeout и network reset означают, что OpenCode step мог выполнить только часть работы. Runner не считает это success, не выбирает следующую task и не архивирует prompt. Вместо повторной отправки исходного prompt runner сохраняет текущий logical step и пытается продолжить через continuation prompt в той же `sessionId`.

## Зачем Continuation Prompt

Continuation prompt нужен, чтобы не начинать задачу заново и не продублировать изменения. Он просит OpenCode продолжить исходную задачу в той же session, сначала проверить состояние файлов, `git diff` и результаты команд. Перед continuation runner ждёт, пока session перестанет быть `Busy` или `Retry`.

## NeedsManualIntervention

`NeedsManualIntervention` означает, что автоматическое восстановление стало небезопасным. Типичные причины: permission request при `permissionPolicy: Manual`, уточняющий вопрос пользователя, повтор одной и той же interruption signature, потерянная session, corrupted state/manifest или незавершённый running step без `sessionId`.

Что делать:

1. Откройте `.queue/runs/<runId>/manifest.json`.
2. Проверьте последний step, `sessionId`, snapshots и attempts logs.
3. Вручную решите проблему в OpenCode или проекте.
4. Запустите `opencode-queue resume --config opencode-queue.json`, если продолжение безопасно.
5. Если продолжать нельзя, используйте `opencode-queue abort --config opencode-queue.json`.

## Partial State Write Или Corrupted JSON

State и manifest пишутся атомарно, но при повреждении диска или ручном редактировании JSON приложение остановится с русским сообщением о повреждённом файле. Runner не должен silently reset active run. Сохраните копию повреждённого файла, проверьте snapshots и manifest, затем восстановите JSON вручную или осознанно выполните `abort` после восстановления читаемого state.

## CompletedPendingArchive

`CompletedPendingArchive` означает, что task и quality steps завершены, но перенос task prompt в completed archive ещё не подтверждён. Проверьте SHA/source prompt и `.queue/completed/`. Если source уже перемещён и hash совпадает, `resume` может завершить archive recovery. Если hash изменился, нужна ручная проверка, чтобы не потерять prompt.

## Permission Requests И Questions

По умолчанию `permissionPolicy` равен `Manual`, а `autoRespondToRecoverableQuestions` выключен. Dangerous/permissive permissions не включаются автоматически. При permission request или уточняющем вопросе runner останавливает run для ручного решения.

## OpenCode Server Restart Или Network Timeout

Для unattended recovery предпочтителен Server mode: runner хранит `sessionId` и проверяет project path. После рестарта server выполните `status`, затем `resume`. Если session больше недоступна, run перейдёт в `NeedsManualIntervention`. CLI fallback менее надёжен для live status и должен использоваться осторожно.
