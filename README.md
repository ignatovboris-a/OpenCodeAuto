# OpenCodeQueue — набор задач для программирующего агента

Этот архив содержит самостоятельные Markdown-промпты для поэтапной реализации консольного приложения `OpenCodeQueue` на C# / .NET 9.

## Что изменено в этой версии

В набор задач добавлен полноценный сценарий **multi-project runner**:

- собственный registry проектов `OpenCodeQueue`;
- `activeProjectId`;
- первый старт с выбором/добавлением проекта;
- стартовое меню на русском языке;
- команды `project list/current/select/add/remove/update/discover`;
- проверка, что OpenCode работает именно с выбранным `projectDir`;
- project-scoped state в `.queue` каждого проекта.

## Как использовать

1. Создай новый пустой репозиторий для консольного приложения.
2. Скопируй папку `implementation-prompts` рядом с репозиторием или внутрь него.
3. Отправляй программирующему агенту промпты строго по порядку: `01...md`, затем `02...md`, и так далее до `12...md`.
4. После каждого шага запускай build/tests, если агент сам этого не сделал.
5. Файлы в `runtime-quality-examples` — это примеры будущих runtime quality prompts для самого `OpenCodeQueue`; они не являются задачами реализации приложения.

## Основная архитектурная идея

Runner должен быть устойчивым к падениям и к работе с несколькими проектами. Для этого:

- глобальный `opencode-queue.json` хранит список project profiles и `activeProjectId`;
- каждый project profile хранит собственные `projectDir`, `promptsDir`, `qualityDir`, `stateDir`;
- workflow state хранится в `.queue` выбранного проекта, а не в глобальном registry;
- при запуске workflow runner создаёт или восстанавливает конкретную сессию OpenCode и сохраняет `sessionId` в manifest;
- OpenCode UI/TUI/Desktop не считается источником истины для выбора проекта: runner должен явно передавать или проверять `projectDir`.

Пример структуры целевого проекта:

```text
project-a/
  prompts/
    01-add-auth.md
    02-refactor-users.md
  quality/
    01-self-check.md
    02-architecture-risks.md
  .queue/
    state.json
    events.jsonl
    runs/
    completed/
    failed/
```

Пример app config:

```text
opencode-queue.json
  activeProjectId: project-a
  projects:
    - project-a -> C:/dev/project-a
    - project-b -> /home/user/dev/project-b
```

## Порядок implementation prompts

```text
implementation-prompts/
  01-project-foundation.md
  02-configuration-and-domain-model.md
  03-project-registry-and-start-menu.md
  04-numbered-file-discovery.md
  05-state-locking-and-crash-recovery.md
  06-opencode-server-client.md
  07-cli-fallback-client.md
  08-workflow-orchestrator-and-file-transitions.md
  09-russian-console-logging-status.md
  10-tests-and-fakes.md
  11-docs-examples-packaging.md
  12-final-hardening-review.md
```

## Runtime quality examples

```text
runtime-quality-examples/
  01-self-check.md
  02-architecture-risks.md
  03-final-report.md
```

`quality` prompts не перемещаются и переиспользуются для каждой задачи. Task prompt из `prompts` переносится в completed archive только после успешного завершения task step и всех quality steps.
