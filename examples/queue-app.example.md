# Example Config

`opencode-queue.json` - основной пример config для реального старта. `queue-app.example.json` - короткий demo alias. Оба файла являются валидным JSON без комментариев; можно скопировать любой из них в рабочий `opencode-queue.json` и заменить пути на реальные.

`project-config.json` показывает один project profile как snippet для вставки в массив `projects`.

Поля:

- `schemaVersion` - версия схемы config.
- `activeProjectId` - проект по умолчанию для команд без `--project`.
- `defaults` - настройки OpenCode по умолчанию для всех проектов.
- `projects` - registry проектов `OpenCodeQueue`.
- `projectDir` - корень целевого проекта.
- `promptsDir` - папка task prompts.
- `qualityDir` - папка quality prompts.
- `stateDir` - папка состояния runner.
- `completedDir` - archive для успешно завершённых task prompts, обычно `.queue/completed`.
- `openCodeOverrides` - настройки OpenCode для конкретного проекта поверх `defaults`.
- `consoleVerbosity` - уровень CLI logging в консоль; runtime logs всегда находятся в `.queue/events.jsonl`, manifest и attempts logs.
- `permissionPolicy` - безопасное значение по умолчанию `Manual`; dangerous auto-approve не включается автоматически.

Правила путей:

- относительный `projectDir` разрешается относительно директории config-файла;
- `promptsDir`, `qualityDir`, `reviewsDir`, `stateDir` и `completedDir` разрешаются относительно `projectDir`;
- абсолютные пути используются как есть.

Каждый project profile наследует `defaults`. Если внутри проекта задан `openCodeOverrides`, он переопределяет только указанные поля, остальные значения берутся из `defaults`.

Пример: если config лежит в `/home/user/queue/opencode-queue.json`, а проект содержит `"projectDir": "../dev/app"`, то `projectDir` будет `/home/user/dev/app`. Если в этом project profile указано `"promptsDir": "prompts"`, runner будет читать `/home/user/dev/app/prompts`.

Для Windows JSON-пути можно писать как `C:/dev/project-a`, чтобы не экранировать обратные слэши.
