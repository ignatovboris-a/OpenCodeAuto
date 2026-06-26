# OpenCodeQueue

`OpenCodeQueue` - консольное приложение на C# / .NET 9 для последовательного запуска Markdown-промптов в OpenCode.

Приложение берёт следующую задачу из папки `prompts`, запускает её в отдельной сессии OpenCode, затем в той же сессии выполняет все проверочные prompts из `quality` по порядку. Следующая task не начинается, пока текущая task и все её quality steps не завершены успешно.

Консольный вывод приложения - на русском языке. Текст пользовательских `.md` prompt-файлов не переводится, не нормализуется и не улучшается: runner передаёт его агенту как есть.

## Что Делает

`OpenCodeQueue` помогает запускать пачки задач для одного или нескольких проектов:

- хранит собственный registry проектов в `opencode-queue.json`;
- явно выбирает active project внутри runner;
- обнаруживает numbered Markdown prompts;
- выполняет workflow `task -> quality-01 -> quality-02 -> ...`;
- сохраняет состояние без базы данных в JSON/JSONL;
- восстанавливает активный run после падения приложения или перезапуска компьютера;
- переносит успешно выполненный task prompt в completed archive;
- оставляет quality prompts на месте и переиспользует их для каждой task.

## Multi-Project Registry

`OpenCodeQueue` поддерживает много целевых проектов. Для каждого проекта в registry задаются:

- `projectDir` - корень целевого проекта;
- `promptsDir` - папка основных task prompts;
- `qualityDir` или alias `reviewsDir` - папка проверочных prompts;
- `stateDir` - локальная папка состояния runner, обычно `.queue`.

Registry `OpenCodeQueue` является источником истины. Выбранный проект в OpenCode UI, Desktop или TUI не используется как active project для runner. Это важно, потому что UI может быть открыт в одном проекте, а очередь должна работать строго с `projectDir`, выбранным в `opencode-queue.json` или через `--project`.

Если используется уже работающий OpenCode Server, runner проверяет, что server current path соответствует выбранному `projectDir`. При несовпадении очередь автоматически не запускается и выводится русскоязычное предупреждение.

## Структура Проекта

Минимальная структура целевого проекта:

```text
project/
  prompts/
  quality/
  .queue/
```

Пример после нескольких запусков:

```text
project/
  prompts/
    02-next-task.md
  quality/
    01-self-check.md
    02-architecture-risks.md
    03-final-report.md
  .queue/
    state.json
    events.jsonl
    lock
    runs/
      20260626-120000-abcdef123/
        manifest.json
        snapshots/
    completed/
      20260626-121500_01-first-task.md
```

`prompts` содержит основные task prompts. После успешного workflow task prompt перемещается в `.queue/completed` или в `completedDir`, если он задан в project profile.

`quality` содержит проверочные prompts. Эти файлы не перемещаются, не архивируются и используются повторно для каждой task.

`promptsDir` и `qualityDir` должны существовать перед запуском очереди. Если одна из папок отсутствует или недоступна, `run` останавливается с validation error до создания run и до обращения к OpenCode.

`.queue` содержит состояние выбранного проекта. Его не нужно переносить между проектами.

## App Config

По умолчанию приложение читает `opencode-queue.json` из текущей директории. Другой путь можно передать через `--config`.

Общая форма:

```text
opencode-queue.json
  schemaVersion
  activeProjectId
  defaults
  projects[]
```

Рабочий config проекта находится в корне repository: `opencode-queue.json`.

Правила путей в текущей реализации:

- относительный `projectDir` разрешается относительно директории файла config;
- `promptsDir`, `qualityDir`, `reviewsDir`, `stateDir` и `completedDir` разрешаются относительно `projectDir`;
- абсолютные пути используются как есть;
- пути с пробелами и Unicode поддерживаются через стандартные API .NET.

`activeProjectId` выбирает проект по умолчанию. Команда с `--project <id>` временно использует указанный проект и не меняет `activeProjectId`.

`defaults` задаёт настройки OpenCode по умолчанию. Каждый проект наследует эти значения; объект `openCodeOverrides` внутри project profile может переопределить только нужные поля.

## Первый Старт И Меню

Запуск без аргументов открывает русскоязычное меню:

```bash
opencode-queue
```

Меню показывает active project, пути `promptsDir`, `qualityDir`, `stateDir`, количество задач, активный run и действия:

- запустить очередь до конца;
- запустить одну следующую задачу;
- продолжить или восстановить active run;
- показать статус;
- показать список задач и quality prompts;
- выбрать или сменить проект;
- добавить проект;
- диагностика проекта;
- настройки проекта;
- выход.

Если project ещё не выбран или недоступен, первый старт предлагает выбрать проект из registry, добавить проект вручную или повторить discovery. Discovery делает best-effort поиск кандидатов, включая локальные OpenCode project storage и доступный OpenCode server, но ручной ввод пути доступен всегда.

## Имена Prompt-Файлов

В очередь попадают только `.md` файлы верхнего уровня с числовым префиксом:

```text
01.md
01-add-auth.md
0.1.md
0.1 task.md
0.0.2-refactor.md
```

Task prompts лежат в `promptsDir`. Quality prompts лежат в `qualityDir`. Файлы без числового префикса пропускаются с warning. Task-файлы, имя которых начинается с `_`, игнорируются.

## Numeric Sorting

Сортировка идёт по числовым сегментам префикса, разделённым точками.

Пример порядка:

```text
0.0.2-refactor.md
0.1.md
0.1. auth.md
01-example.md
02-next.md
10-large-change.md
```

Сегменты сравниваются как числа, поэтому `2` идёт раньше `10`, а `1.2` раньше `1.10`. Если числовые ключи равны (`01` и `1`), порядок стабилизируется по имени файла и полному пути.

## Workflow Run

Для каждой task runner создаёт или восстанавливает одну конкретную сессию OpenCode и выполняет в ней цепочку:

```text
task prompt
quality prompt 01
quality prompt 02
quality prompt 03
...
```

Новая task не выбирается, пока active run не завершён, не восстановлен, не aborted или не требует ручного вмешательства.

Перед отправкой каждого prompt runner делает snapshot файла в `.queue/runs/<runId>/snapshots/`. Snapshot нужен, чтобы recovery и audit не зависели от изменения исходного файла во время run.

Перед созданием run runner выполняет preflight выбранного OpenCode adapter. Если внешний OpenCode Server открыт для другого `projectDir` или OpenCode недоступен, новая task не выбирается, prompt не отправляется и active run не создаётся.

## Resilience И Continuation

`terminated`, `Tool execution aborted`, `process terminated`, `connection reset`, `request timeout`, `idle timeout`, `network error`, `ECONNRESET`, `ETIMEDOUT` и похожие признаки не считаются успешным завершением шага. Runner классифицирует их как recoverable interruption, сохраняет состояние текущего logical step, перечитывает status OpenCode session и отправляет continuation prompt в ту же `sessionId` только после того, как session не находится в `busy` или встроенном `retry`.

После recoverable interruption исходный task или quality prompt не отправляется повторно. Continuation prompt просит OpenCode продолжить с последнего фактически выполненного действия, проверить `git diff`, состояние файлов и результаты команд. Quality prompts запускаются только после успешного task step, а task prompt переносится в archive/completed только после успешного завершения task и всех quality prompts.

Пример настройки в `defaults` или `openCodeOverrides`:

```jsonc
{
  "resilience": {
    "enabled": true,
    "stepTimeoutMinutes": 90,
    "idleTimeoutMinutes": 20,
    "maxContinuationAttemptsPerStep": 5,
    "maxTransportRetriesPerAttempt": 3,
    "retryDelaySeconds": 15,
    "retryBackoffMultiplier": 2.0,
    "stopAfterSameSignatureRepeats": 3,
    "detectTerminatedText": true,
    "recoverOnToolExecutionAborted": true,
    "autoRespondToRecoverableQuestions": false,
    "permissionPolicy": "Manual",
    "continuationPrompt": null
  }
}
```

Лимиты нужны для защиты от бесконечного цикла одной и той же ошибки. Если превышен лимит continuation attempts, transport retries или повторов одинаковой signature, run переходит в `NeedsManualIntervention`, очередь останавливается, а task prompt остаётся в `prompts`.

Permission requests и уточняющие вопросы обрабатываются отдельно от технических прерываний. По умолчанию permission request переводит run в `NeedsManualIntervention`; dangerous/permissive режим не включается автоматически. Вопросы пользователя также останавливают run, если `autoRespondToRecoverableQuestions` явно не включён и continuation prompt не может безопасно продолжить работу в рамках исходного задания.

## Recovery После Crash

Состояние хранится без БД:

- `.queue/state.json` - active run и последний завершённый run;
- `.queue/events.jsonl` - журнал событий;
- `.queue/runs/<runId>/manifest.json` - manifest конкретного run;
- `.queue/runs/<runId>/snapshots/` - копии task и quality prompts для run;
- `.queue/lock` - lock одного runner на проект.

После crash используйте:

```bash
opencode-queue status --config opencode-queue.json
opencode-queue resume --config opencode-queue.json
```

Если в `state.json` есть `activeRunId`, новый `run` не выберет следующую task. Нужно выполнить `resume`, проверить статус или сознательно сделать `abort`.

Если OpenCode недоступен во время `resume`, состояние active run сохраняется, новая task не выбирается, а пользователю выводится русскоязычная ошибка. Для незавершённого running step без `sessionId` автоматическое восстановление останавливается в `NeedsManualIntervention`, чтобы не повторить prompt в новой session.

При `resume` runner ищет `activeRunId`, загружает manifest, проверяет сохранённый `sessionId` и продолжает незавершённый step через continuation/recovery в той же сессии. Если OpenCode больше не может найти или продолжить сессию, run переводится в `NeedsManualIntervention`.

## Почему Session Id В Manifest

`sessionId` сохраняется в `.queue/runs/<runId>/manifest.json`, потому что восстановление должно продолжать именно ту OpenCode session, где уже выполнялась task. Без этого runner рискует повторить prompt в новой session или продолжить не тот контекст.

Server API режим предпочтителен: он позволяет создать сессию, сохранить её id, отправлять сообщения в конкретную сессию и после рестарта продолжить именно её.

CLI fallback менее надёжен. Он допустим, но не должен быть единственным recovery-механизмом. `--continue` без конкретного session id не используется как recovery, потому что может продолжить не ту сессию. Если session id известен, CLI fallback работает только с конкретной session.

## Completed Archive

Task prompt переносится в completed archive только после успешного выполнения task step и всех quality steps.

По умолчанию archive находится здесь:

```text
project/.queue/completed/
```

Имя архивного файла получает timestamp:

```text
20260626-121500_01-example-task.md
```

Перед переносом проверяется SHA-256 исходного task prompt. Если файл изменился после snapshot, архивирование останавливается, run остаётся в `CompletedPendingArchive`, а файл нужно проверить вручную.

## Ошибка Step

Если task step или quality step завершился ошибкой, run получает статус `Failed`, а новая task не выбирается. Что делать:

1. Посмотреть статус: `opencode-queue status --config opencode-queue.json`.
2. Проверить `.queue/runs/<runId>/manifest.json` и `.queue/events.jsonl`.
3. Исправить проблему в проекте, OpenCode или prompt-файле.
4. Запустить `opencode-queue resume --config opencode-queue.json`.

Если автоматическое восстановление небезопасно, run может перейти в `NeedsManualIntervention`. В этом случае проверьте manifest вручную. Команда `abort` переводит active run в `Aborted` без удаления данных и без автоматического архивирования task prompt.

## Status И Logs

Основные команды:

```bash
opencode-queue status --config opencode-queue.json
opencode-queue list --config opencode-queue.json
opencode-queue doctor --config opencode-queue.json
```

`status` показывает количество task prompts, quality prompts и active run. `list` показывает найденные prompt-файлы в порядке выполнения. `doctor` запускает validation, проверяет state/lock и доступность OpenCode для выбранного `projectDir`.

Runtime-данные находятся в `.queue`. Смотрите `events.jsonl` для хронологии и `runs/<runId>/manifest.json` для детального состояния step-by-step. CLI adapter сохраняет stdout/stderr отдельных попыток в `runs/<runId>/attempts/`, а manifest хранит ссылки на эти diagnostic logs.

Полный prompt text не пишется в command log как command-line argument. В CLI adapter режим `Auto` использует attachment transport, чтобы prompt text не попадал в command line; явный `Inline` остаётся ручным opt-in. Snapshot prompt осознанно хранится в `.queue/runs/<runId>/snapshots/`. `serverPassword` не сохраняется в manifest в исходном виде: snapshot настроек OpenCode в manifest содержит redacted-значение.

## Server API И CLI Fallback

Рекомендуемый режим - `Server`:

- managed server запускается в `projectDir`;
- runner может проверить project path;
- session id явно сохраняется и используется для recovery;
- проще понять состояние конкретной сессии.

CLI fallback полезен как запасной режим, но имеет ограничения:

- runner обязан передавать `--dir <projectDir>`;
- recovery безопасен только при известном `sessionId`;
- нельзя полагаться на неявное `--continue`;
- stdout/stderr классифицируются, поэтому ненулевой exit code с `Tool execution aborted` считается recoverable interruption, а не немедленным fatal failure;
- JSON-line/event output от `--format json` анализируется построчно; `terminated` или `Tool execution aborted` не считаются success даже при exit code `0`;
- диагностика статуса беднее, чем через Server API.

Для полноценного recovery предпочтителен OpenCode Server API с сохранённым `sessionId`. CLI fallback ограничен, если конкретную сессию нельзя надёжно восстановить.

## CLI Examples

```bash
opencode-queue
opencode-queue project add --config opencode-queue.json
opencode-queue project list --config opencode-queue.json
opencode-queue project select project-a --config opencode-queue.json
opencode-queue list --config opencode-queue.json
opencode-queue status --config opencode-queue.json
opencode-queue doctor --config opencode-queue.json
opencode-queue run --config opencode-queue.json --once
opencode-queue run --config opencode-queue.json
opencode-queue resume --config opencode-queue.json
opencode-queue run --config opencode-queue.json --project project-b --once
```

Дополнительные команды:

```bash
opencode-queue validate --config opencode-queue.json
opencode-queue abort --config opencode-queue.json
opencode-queue project current --config opencode-queue.json
opencode-queue project discover --config opencode-queue.json
opencode-queue project update project-a --config opencode-queue.json
opencode-queue project remove project-a --config opencode-queue.json
```

## Windows Быстрый Старт

```powershell
dotnet restore
dotnet build
dotnet test
dotnet run --project src/OpenCodeQueue.Cli -- --help
dotnet run --project src/OpenCodeQueue.Cli -- project add --config opencode-queue.json
dotnet run --project src/OpenCodeQueue.Cli -- doctor --config opencode-queue.json
dotnet run --project src/OpenCodeQueue.Cli -- run --config opencode-queue.json --once
```

После publish можно запускать бинарь `opencode-queue.exe`, если вы переименовали output или добавили папку publish в `PATH`.

## Linux Быстрый Старт

```bash
dotnet restore
dotnet build
dotnet test
dotnet run --project src/OpenCodeQueue.Cli -- --help
dotnet run --project src/OpenCodeQueue.Cli -- project add --config opencode-queue.json
dotnet run --project src/OpenCodeQueue.Cli -- doctor --config opencode-queue.json
dotnet run --project src/OpenCodeQueue.Cli -- run --config opencode-queue.json --once
```

После publish можно запускать файл `OpenCodeQueue.Cli` из publish-директории или создать shell alias `opencode-queue`.

## Build И Publish

```bash
dotnet restore
dotnet build
dotnet test
dotnet publish src/OpenCodeQueue.Cli -c Release -r win-x64 --self-contained false
dotnet publish src/OpenCodeQueue.Cli -c Release -r linux-x64 --self-contained false
```

Результат publish находится в:

```text
src/OpenCodeQueue.Cli/bin/Release/net9.0/<runtime>/publish/
```

`--self-contained false` означает, что на целевой машине должен быть установлен совместимый .NET Runtime.

## Single-File Publish

Если нужен один исполняемый файл:

```bash
dotnet publish src/OpenCodeQueue.Cli -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
dotnet publish src/OpenCodeQueue.Cli -c Release -r linux-x64 --self-contained false -p:PublishSingleFile=true
```

Trade-offs:

- удобнее копировать один файл;
- startup может быть немного медленнее;
- диагностика publish output менее прозрачна;
- при `--self-contained false` .NET Runtime всё равно нужен на целевой машине.

Для полностью автономного бинаря можно использовать `--self-contained true`, но размер publish output будет значительно больше.

## Рабочая Структура Этого Репозитория

В корне репозитория используются рабочие файлы очереди:

```text
opencode-queue.json
prompts/
quality/
  01-проверка_реализации_без_совместимости.md
  02_проблемы_архитектуры.md
```

Демонстрационная папка удалена. Runtime config `opencode-queue.json` смотрит на корневые `prompts/` и `quality/`.

Подробные recovery-инструкции есть в `docs/troubleshooting.md`.

## Security Notes

- Unattended OpenCode может менять файлы выбранного проекта.
- Всегда проверяйте active project перед запуском очереди: `opencode-queue project current --config opencode-queue.json`.
- Не храните секреты в prompt files, `.queue/events.jsonl`, manifest и logs.
- Не включайте опасные auto-approve permissions без осознанного решения.
- Не запускайте runner одновременно в одном проекте из нескольких терминалов.
- Перед первым unattended run лучше сделать clean `git status` и commit или backup.

## Практический Checklist

1. Установить .NET 9 SDK и OpenCode.
2. Создать или выбрать целевой проект.
3. Добавить `prompts/`, `quality/`, `.queue/` в проект.
4. Создать `opencode-queue.json` или выполнить `opencode-queue project add --config opencode-queue.json`.
5. Проверить active project: `opencode-queue project current --config opencode-queue.json`.
6. Проверить prompts: `opencode-queue list --config opencode-queue.json`.
7. Запустить диагностику: `opencode-queue doctor --config opencode-queue.json`.
8. Сделать первый безопасный запуск: `opencode-queue run --config opencode-queue.json --once`.
