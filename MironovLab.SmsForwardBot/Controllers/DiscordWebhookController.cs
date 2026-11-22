using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using MironovLab.SmsForwardBot.Bot;

namespace MironovLab.SmsForwardBot.Controllers;

[ApiController]
[Route("webhooks/discord")]
public sealed class DiscordWebhookController(
    IDiscordWebhook webhook, IConfiguration configuration, ILogger<DiscordWebhookController> logger) : ControllerBase
{
    internal const int MaxRequestBodySize = 64 * 1024;

    [HttpPost("{secret}")]
    public async Task<IActionResult> ReceiveAsync([FromRoute] string secret, CancellationToken token)
    {
        if (!webhook.MatchesSecret(secret))
        {
            logger.LogDebug("Rejected a Discord webhook request with an invalid secret.");
            return NotFound();
        }

        if (!Request.HasJsonContentType())
            return StatusCode(StatusCodes.Status415UnsupportedMediaType);

        if (logger.IsEnabled(LogLevel.Information) && configuration.GetValue<bool>("DiscordWebhook:LogRawBody"))
            await LogRequestBodyAsync(token);

        DiscordPayload? payload;
        try
        {
            payload = await Request.ReadFromJsonAsync<DiscordPayload>(token);
        }
        catch (JsonException)
        {
            logger.LogWarning("Rejected a Discord webhook request with invalid JSON.");
            return BadRequest();
        }

        if (payload?.Embeds is not { } embeds)
        {
            logger.LogWarning("Rejected a Discord webhook request without an embeds array.");
            return BadRequest();
        }

        if (!await webhook.EnqueueAsync(embeds, token))
            return StatusCode(StatusCodes.Status503ServiceUnavailable);

        return NoContent();
    }

    private async Task LogRequestBodyAsync(CancellationToken token)
    {
        Request.EnableBuffering(bufferThreshold: MaxRequestBodySize);
        var position = Request.Body.Position;
        var encoding = Request.GetTypedHeaders().ContentType?.Encoding ?? Encoding.UTF8;

        using var reader = new StreamReader(Request.Body, encoding, leaveOpen: true);
        var rawBody = await reader.ReadToEndAsync(token);
        Request.Body.Position = position;

        logger.LogInformation("Raw Discord webhook body: {WebhookBody}", rawBody);
    }
}
