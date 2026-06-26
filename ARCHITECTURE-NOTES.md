# Архитектурные решения для OpenCodeQueue

## 1. Multi-project registry

У приложения должен быть собственный registry проектов. OpenCode UI/TUI/Desktop может иметь свои проекты и сессии, но runner не должен полагаться на выбранный проект в интерфейсе. Источник истины:

```text
opencode-queue.json
  activeProjectId
  projects[]
```

Каждый project profile содержит:

- `id`;
- `displayName`;
- `projectDir`;
- `promptsDir`;
- `qualityDir` или alias `reviewsDir`;
- `stateDir`;
- OpenCode settings/overrides.

`--project <id>` временно переопределяет active project для одной команды. `project select <id>` меняет persisted `activeProjectId`.

## 2. Первый старт и меню

При запуске без аргументов приложение входит в интерактивный режим:

```text
OpenCodeQueue
Активный проект: <не выбран>

1. Выбрать проект
2. Добавить проект
3. Обнаружить проекты OpenCode
0. Выход
```

После выбора проекта меню показывает:

```text
Активный проект: Project A
Путь проекта: C:\dev\project-a
Очередь задач: prompts = 3, quality = 2
Активный run: нет

1. Запустить очередь до конца
2. Запустить одну следующую задачу
3. Продолжить/восстановить active run
4. Статус
5. Список задач и quality prompts
6. Сменить проект
7. Добавить проект
8. Doctor
0. Выход
```

Меню не содержит бизнес-логики. Оно вызывает те же use cases, что и CLI commands.

## 3. Папки целевого проекта

Каноническая структура:

```text
project/
  prompts/
    01-some-task.md
    02-next-task.md
  quality/
    01-self-check.md
    02-architecture-risks.md
  .queue/
    state.json
    events.jsonl
    runs/
    completed/
```

`quality` выбрана как основное имя папки для проверочных prompt-файлов. `reviews` можно поддержать как alias в конфиге, но внутри кода лучше использовать один термин: `Quality`.

## 4. Порядок выполнения

Для каждой task из `prompts` выполняется один workflow:

```text
task prompt -> quality/01 -> quality/02 -> ... -> quality/N
```

Все steps выполняются в одной и той же session OpenCode. Следующая task не стартует до полного успешного завершения текущего workflow.

## 5. Перемещение файлов

Task prompt из `prompts` переносится в completed archive только после успешного завершения task step и всех quality steps.

Quality prompt-файлы не перемещаются. Они переиспользуются для каждой задачи.

## 6. Сортировка numbered prompt-файлов

Prefix извлекается из начала имени файла. Поддерживаются варианты:

```text
01.md
01-task.md
01 task.md
01.task.md
0.1.md
0.1. task.md
0.0.2-refactor.md
```

Сегменты сравниваются как числа, поэтому `10` идёт после `9`, а `0.10` после `0.2`.

## 7. Восстановление после падения

State хранится project-scoped:

```text
<projectDir>/.queue/state.json
<projectDir>/.queue/runs/<runId>/manifest.json
<projectDir>/.queue/events.jsonl
```

Runner должен сохранять состояние до каждого необратимого действия:

1. Создал run manifest.
2. Создал session и записал `sessionId`.
3. Перед отправкой step записал step status `Running` и `messageId`.
4. После успеха записал step status `Completed`.
5. Перед переносом task prompt записал `CompletedPendingArchive`.
6. После переноса task prompt записал `Completed` и очистил `activeRunId`.

После рестарта runner сначала выбирает project profile, затем проверяет `activeRunId` в `.queue` этого проекта. Если active run есть, новая task не выбирается.

## 8. Почему лучше OpenCode Server API

Для восстановления нельзя полагаться только на “последнюю сессию”. Runner должен знать конкретный `sessionId`. Поэтому предпочтительный режим интеграции — OpenCode Server API: создать session, сохранить id, отправлять сообщения в `/session/:id/message`, проверять session/messages/status при recovery.

CLI adapter можно оставить как fallback, но он обязан использовать конкретный session id, если workflow уже начат.

Managed server adapter запускает `opencode serve` как дочерний процесс runner и завершает его при dispose приложения. Если runner падает во время active run, OpenCode process может быть завершён вместе с runner; восстановление опирается не на живой process, а на сохранённый `sessionId` и повторный запуск/подключение к server в том же `projectDir`.

## 9. ProjectDir и OpenCode

OpenCode должен работать в выбранном `projectDir`:

- для managed server процесс `opencode serve` запускается с `WorkingDirectory = projectDir`;
- для external server нужно проверить current path/project через API;
- для CLI fallback нужно передавать `--dir <projectDir>`;
- при mismatch запуск workflow запрещается до исправления.

## 10. State без БД

Достаточно локальных файлов:

- app config `opencode-queue.json` — registry и active project;
- project state `.queue/state.json` — active run для проекта;
- `.queue/runs/<runId>/manifest.json` — полное состояние workflow;
- `.queue/events.jsonl` — append-only журнал событий;
- snapshot copies task/quality prompts внутри run directory.

Запись config/state/manifest должна быть атомарной через temp file + replace/move.

## 11. Recovery strategy

Если приложение упало во время running step, после restart нужно:

1. Восстановить `sessionId` из manifest.
2. Попробовать проверить сообщения/status OpenCode.
3. Если нельзя доказать, что step завершён, не повторять prompt вслепую по умолчанию.
4. Default strategy: `ConservativeContinue` — отправить recovery prompt в ту же session с просьбой продолжить/дозавершить предыдущий шаг.

## 12. Console UX

Все сообщения пользователю должны быть на русском языке. Минимальные команды:

```text
menu
run
resume
status
list
validate
doctor
abort
project list
project current
project select
project add
project remove
project update
project discover
```

`status` должен ясно показывать selected project, active run, session id, текущий step и путь к логам.
