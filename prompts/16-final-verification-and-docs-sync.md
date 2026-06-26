# 16. Финальная проверка, документация и эксплуатационная готовность OpenCodeQueue

## Контекст

В репозитории реализован `OpenCodeQueue` по prompts 01-13, затем выполнен аудит prompt 14 и remediation prompt 15. Этот prompt нужен как финальная контрольная точка перед реальным unattended использованием на рабочих проектах.

Цель — проверить, что код, тесты, документация, примеры config и фактическое поведение согласованы между собой.

## Главная задача

Проведи финальную verification pass. Исправляй только небольшие несоответствия, документацию, tests/examples и очевидные дефекты. Если обнаружена крупная архитектурная проблема, не переписывай всё молча — зафиксируй её в финальном отчёте как blocker или risk.

## Проверить обязательно

### 1. CLI команды и меню

Проверь фактические команды приложения и документацию.

Должны быть согласованы:

- имя executable/команды;
- help output;
- README examples;
- project commands;
- run/status/list/diagnostics commands;
- параметры `--project`, `--once`, `--dry-run`, если они реализованы;
- русское стартовое меню;
- первый старт без config.

Запусти help/status/list в безопасном режиме, если это возможно без реального OpenCode выполнения.

### 2. Файловая модель проекта

Проверь, что документация и код используют одну модель:

```text
<projectDir>/prompts/
<projectDir>/quality/
<projectDir>/.queue/
  state.json
  events.jsonl
  runs/
  completed/ или archive/
  failed/ если предусмотрено policy
```

Если фактическая модель отличается, обнови README и examples так, чтобы пользователь не ошибся.

### 3. Config examples

Проверь примеры конфигурации:

- global app config;
- project profile;
- OpenCode managed server/external server;
- resilience settings;
- permission policy;
- CLI fallback settings;
- continuation prompt text;
- logging settings.

Проверь, что примеры не включают dangerous permissions по умолчанию.

### 4. Recovery сценарии

Сверь код, tests и docs для сценариев:

```text
power off / process killed
state file partial write
active run exists on startup
OpenCode server restarted
network timeout
Tool execution aborted
terminated
permission request
question/user clarification
repeated same interruption
CompletedPendingArchive
```

Убедись, что документация объясняет пользователю:

- почему `terminated` не считается success;
- почему runner отправляет continuation prompt;
- где посмотреть logs/manifest;
- что делать при NeedsManualIntervention;
- как вручную продолжить/сбросить active run, если нужно.

### 5. Тесты и качество кода

Запусти:

```bash
dotnet restore
dotnet build
dotnet test
```

Если доступны дополнительные checks — запусти их.

Проверь:

- tests не зависят от реального OpenCode server без явного integration profile;
- unit tests используют fakes/stubs;
- integration tests помечены отдельно или отключены по умолчанию;
- tests не портят реальные user directories;
- temp directories очищаются;
- race/concurrency tests не flaky.

### 6. Backward compatibility и миграции

Если после prompts 14-15 менялся формат state/config, проверь:

- есть migration или graceful fallback;
- старые state files не приводят к потере task prompt;
- invalid/corrupted state даёт понятную русскую ошибку;
- приложение предлагает безопасное recovery действие, а не silently resets active run.

### 7. Проверка лишних удалений

Ещё раз проверь:

```bash
git status --short
git diff --stat
git diff --name-status
```

Убедись, что не удалены случайно:

- docs;
- examples;
- tests;
- runtime-quality-examples;
- prompt files;
- config samples;
- project registry code;
- recovery/watchdog code.

Если удаление намеренное — объясни в отчёте.

## Документация, которую нужно иметь к концу

Минимально:

```text
README.md
examples/opencode-queue.json или аналог
examples/project-config.json или аналог
examples/prompts/01-example-task.md
examples/quality/01-self-check.md
examples/quality/02-architecture-risks.md
docs/troubleshooting.md или раздел Troubleshooting в README
docs/audit/14-post-implementation-audit.md
docs/audit/15-remediation-report.md
docs/audit/16-final-verification-report.md
```

Не создавай лишнюю документационную иерархию, если проект уже использует другую структуру. Главное — чтобы пользователь мог установить, настроить, запустить, восстановить и диагностировать приложение.

## Итоговый отчёт

Создай:

```text
docs/audit/16-final-verification-report.md
```

Формат:

```md
# Финальная проверка OpenCodeQueue

## Итог
- Статус: Ready / Ready with warnings / Not ready
- Готовность к unattended запуску: да/нет/условно
- Главные оставшиеся риски: ...

## Проверенные команды
...

## Проверка CLI/menu
...

## Проверка config/examples
...

## Проверка recovery
...

## Проверка permissions
...

## Проверка документации
...

## Проверка tests
...

## Проверка лишних удалений
...

## Изменения, внесённые этим prompt
...

## Что нужно сделать позже
...
```

## Ограничения

- Не меняй архитектуру без крайней необходимости.
- Не выполняй реальные prompts через OpenCode в пользовательском проекте, если это не было явно разрешено.
- Не очищай `.queue` автоматически.
- Не удаляй active run state без явного решения.
- Не включай permissive/dangerous permissions по умолчанию.

## Критерии завершения

Prompt считается выполненным, если:

- создан `docs/audit/16-final-verification-report.md`;
- README/examples соответствуют фактическому CLI;
- build/test результаты зафиксированы;
- recovery/terminated сценарии задокументированы;
- не осталось случайных старых названий MCPCoder/MCPcoder;
- пользователь может понять, как безопасно запустить приложение на одном проекте и как восстановиться после сбоя.
