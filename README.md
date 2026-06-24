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

## Текущий каркас решения

Solution `OpenCodeQueue.sln` содержит четыре проекта:

- `src/OpenCodeQueue.Core` — доменная модель, статусы и интерфейсы портов для registry проектов, discovery, OpenCode, prompt repository, state store, lock, archiver и console reporter.
- `src/OpenCodeQueue.Infrastructure` — минимальные адаптеры файловой системы: JSON config/registry, поиск numbered Markdown prompts, state JSON/JSONL, lock-файл, archiver и заглушка OpenCode client.
- `src/OpenCodeQueue.Cli` — composition root, простой parser команд, русскоязычный console reporter и стартовое меню.
- `tests/OpenCodeQueue.Tests` — unit tests без реального OpenCode.

## Runtime-директории

- `prompts` — папка основных task prompt-файлов. В очередь попадают `.md` файлы с числовым префиксом: `01.md`, `01-auth.md`, `0.1.md`, `0.0.2-refactor.md`.
- `quality` или `reviews` — папка проверочных prompt-файлов. Эти файлы не перемещаются и будут переиспользоваться для каждой task.
- `.queue` — project-scoped состояние runner: будущие `state.json`, `events.jsonl`, lock-файл, run manifests и архив `completed`.
- `opencode-queue.json` — глобальный registry проектов `OpenCodeQueue`; он хранит `activeProjectId` и профили проектов с `projectDir`, `promptsDir`, `qualityDir`, `stateDir`.

## Стартовое меню

Запуск без аргументов входит в интерактивное меню на русском языке. Меню показывает текущий активный проект и пункты: запуск очереди, запуск одной задачи, восстановление run, статус, список задач, выбор проекта, добавление проекта, диагностика и выход. На этом шаге workflow-команды являются безопасными заглушками.

## Минимальные команды

```text
opencode-queue --help
opencode-queue menu --config opencode-queue.json
opencode-queue run --config opencode-queue.json [--project <id>] [--once]
opencode-queue validate --config opencode-queue.json
opencode-queue project list --config opencode-queue.json
opencode-queue project current --config opencode-queue.json
opencode-queue project select <id> --config opencode-queue.json
opencode-queue project add --config opencode-queue.json
```
