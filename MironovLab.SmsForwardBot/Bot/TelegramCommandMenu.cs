using MironovLab.SmsForwardBot.Properties;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;

namespace MironovLab.SmsForwardBot.Bot;

internal sealed class TelegramCommandMenu(
    BotSettings settings, ITelegramBotClient telegram, ILogger<TelegramCommandMenu> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        BotCommand[] commands =
        [
            new("send", Resources.Commands_SendButton),
            new("reply", Resources.Commands_ReplyButton),
            new("lines", Resources.Commands_LinesButton),
            new("line", Resources.Commands_SelectLineButton),
            new("auto", Resources.Commands_AutomaticLineButton),
            new("cancel", Resources.Commands_CancelButton),
            new("help", Resources.Commands_ShowKeyboard)
        ];

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await telegram.SetMyCommands(commands, BotCommandScope.Chat(settings.OwnerUserId), cancellationToken: stoppingToken);
                await telegram.SetChatMenuButton(settings.OwnerUserId, new MenuButtonCommands(), cancellationToken: stoppingToken);
                return;
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested &&
                error is RequestException or OperationCanceledException)
            {
                logger.LogWarning("Failed to register the Telegram command menu ({ErrorType}). Retrying.", error.GetType().Name);
                var delay = (error as ApiRequestException)?.Parameters?.RetryAfter ?? 15;
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, delay)), stoppingToken);
            }
        }
    }
}
