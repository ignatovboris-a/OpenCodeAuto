using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.OpenCode;

namespace OpenCodeQueue.Infrastructure.OpenCode;

internal static class OpenCodePrompt
{
    public const string CliAttachmentInstruction = "Выполни инструкции из прикреплённого Markdown-файла.";

    public const string ServerAttachmentInstruction = "Прикреплённый Markdown-файл является основным prompt. Выполни его содержимое без перевода, переписывания или нормализации.";

    public static PromptTransport ResolveTransport(PromptPayload payload)
    {
        return payload.Transport == PromptTransport.Auto
            ? payload.Content.Length <= payload.MaxInlinePromptChars ? PromptTransport.Inline : PromptTransport.FileAttachment
            : payload.Transport;
    }

}
