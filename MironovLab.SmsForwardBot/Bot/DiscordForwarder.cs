using Telegram.Bot;
using Telegram.Bot.Exceptions;

namespace MironovLab.SmsForwardBot.Bot;

internal sealed class DiscordForwarder(
    BotSettings settings, BotStateStore state, ITelegramBotClient telegram, ILogger<DiscordForwarder> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Telegram notification forwarding started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var message = state.Snapshot.Pending.FirstOrDefault();
            if (message is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                continue;
            }

            try
            {
                await telegram.SendTextAsync(settings.OwnerUserId, message.Text, stoppingToken, entities: message.Entities);
                logger.LogInformation("Forwarded a queued notification to Telegram.");
            }
            catch (ApiRequestException error)
            {
                var log = TelegramBotClientWithDiagnostics.GetLogDetails(error, settings.BotToken);
                logger.LogWarning(log.Error, "Telegram rejected a queued notification with API error {ErrorCode}. Retrying.{TelegramHttpResponse}",
                    error.ErrorCode, log.Response);
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, error.Parameters?.RetryAfter ?? 5)), stoppingToken);
                continue;
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested &&
                error is RequestException or OperationCanceledException)
            {
                var log = TelegramBotClientWithDiagnostics.GetLogDetails(error, settings.BotToken);
                logger.LogWarning(log.Error, "Failed to forward a queued notification to Telegram. Retrying.{TelegramHttpResponse}", log.Response);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await state.RemoveFirstMessageAsync(stoppingToken);
                    break;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    logger.LogError(error, "Failed to update the Telegram notification queue on disk. Retrying.");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
        }
    }
}