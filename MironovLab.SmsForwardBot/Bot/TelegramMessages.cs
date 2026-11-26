using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace MironovLab.SmsForwardBot.Bot;

internal static class TelegramMessages
{
    private const int MessageLimit = 4096;

    public static Task<Message> SendTextAsync(this ITelegramBotClient bot, long chatId, string text,
        CancellationToken token, ReplyMarkup? replyMarkup = null, MessageEntity[]? entities = null) =>
        bot.SendMessage(chatId, text, replyMarkup: replyMarkup, entities: entities,
            linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true }, cancellationToken: token);

    public static IEnumerable<TelegramNotification> SplitNotification(TelegramNotification notification)
    {
        var offset = 0;
        foreach (var part in SplitText(notification.Text))
        {
            var partEnd = offset + part.Length;
            var entities = new List<MessageEntity>();
            foreach (var entity in notification.Entities ?? [])
            {
                var start = Math.Max(entity.Offset, offset);
                var end = Math.Min(entity.Offset + entity.Length, partEnd);
                if (start >= end)
                    continue;

                var length = part.AsSpan(start - offset, end - start).TrimEnd().Length;
                if (length > 0)
                    entities.Add(new MessageEntity { Type = entity.Type, Offset = start - offset, Length = length });
            }

            yield return new TelegramNotification(part, entities.Count == 0 ? null : entities.ToArray());
            offset = partEnd;
        }
    }

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

internal sealed record TelegramNotification(string Text, MessageEntity[]? Entities = null);