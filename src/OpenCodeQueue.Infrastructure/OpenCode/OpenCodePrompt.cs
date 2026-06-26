using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.OpenCode;

namespace OpenCodeQueue.Infrastructure.OpenCode;

internal static class OpenCodePrompt
{
    public const string CliAttachmentInstruction = "Выполни инструкции из прикреплённого Markdown-файла.";

    public const string ServerAttachmentInstruction = "Прикреплённый Markdown-файл является основным prompt. Выполни его содержимое без перевода, переписывания или нормализации.";

    public const string RecoveryInstruction = "Консервативное восстановление OpenCodeQueue: проверь предыдущий незавершённый шаг в этой session. Если исходный prompt уже выполнен, кратко сообщи результат и не повторяй опасные действия. Если выполнение не начиналось или прервалось, продолжи его с учётом уже видимого контекста session.";

    public static PromptTransport ResolveTransport(PromptPayload payload)
    {
        return payload.Transport == PromptTransport.Auto
            ? payload.Content.Length <= payload.MaxInlinePromptChars ? PromptTransport.Inline : PromptTransport.FileAttachment
            : payload.Transport;
    }

    public static PromptPayload RecoveryPayload(string messageId, string sourcePath, string runId, string stepId)
    {
        return new PromptPayload
        {
            MessageId = messageId,
            SourcePath = sourcePath,
            Transport = PromptTransport.Inline,
            RunId = runId,
            StepId = stepId,
            Content = RecoveryInstruction
        };
    }
}
