using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace MironovLab.SmsForwardBot.Bot;

internal static class TelegramMessages
{
    private const int MessageLimit = 4096;

    public static Task<Message> SendPlainTextAsync(this ITelegramBotClient bot, long chatId, string text,
        CancellationToken token, ReplyMarkup? replyMarkup = null) =>
        bot.SendMessage(chatId, text, replyMarkup: replyMarkup,
            linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true }, cancellationToken: token);

    public static IEnumerable<string> SplitText(string text)
    {
        for (var offset = 0; offset < text.Length;)
        {
            var length = Math.Min(MessageLimit, text.Length - offset);
            if (offset + length < text.Length && char.IsHighSurrogate(text[offset + length - 1]))
                length--;

            yield return text.Substring(offset, length);
            offset += length;
        }
    }
}
