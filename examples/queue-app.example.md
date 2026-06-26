# Example Config

`queue-app.example.json` - валидный JSON без комментариев. Его можно скопировать в `opencode-queue.json` и заменить пути на реальные.

Поля:

- `schemaVersion` - версия схемы config.
- `activeProjectId` - проект по умолчанию для команд без `--project`.
- `defaults` - настройки OpenCode по умолчанию для всех проектов.
- `projects` - registry проектов `OpenCodeQueue`.
- `projectDir` - корень целевого проекта.
- `promptsDir` - папка task prompts.
- `qualityDir` - папка quality prompts.
- `stateDir` - папка состояния runner.

Правила путей:

- относительный `projectDir` разрешается относительно директории config-файла;
- `promptsDir`, `qualityDir`, `reviewsDir`, `stateDir` и `completedDir` разрешаются относительно `projectDir`;
- абсолютные пути используются как есть.

Каждый project profile наследует `defaults`. Если внутри проекта задан `openCodeOverrides`, он переопределяет только указанные поля, остальные значения берутся из `defaults`.

Пример: если config лежит в `/home/user/queue/opencode-queue.json`, а проект содержит `"projectDir": "../dev/app"`, то `projectDir` будет `/home/user/dev/app`. Если в этом project profile указано `"promptsDir": "prompts"`, runner будет читать `/home/user/dev/app/prompts`.

Для Windows JSON-пути можно писать как `C:/dev/project-a`, чтобы не экранировать обратные слэши.
