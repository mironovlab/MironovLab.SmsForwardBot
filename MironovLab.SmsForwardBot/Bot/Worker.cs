using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MironovLab.SmsForwardBot.Properties;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace MironovLab.SmsForwardBot.Bot;

internal sealed class Worker(
    BotSettings settings, BotStateStore state, BridgeClient bridge, ITelegramBotClient telegram,
    ILogger<Worker> logger) : BackgroundService
{
    private const string DetailSeparator = " · ";
    private const string SelectLineButton = "line:";
    private static readonly Regex CommandName = new(@"\A\s*(/[A-Za-z0-9_]+)(?:@[A-Za-z0-9_]+)?(?=\s|\z)");
    private static readonly Regex RecipientSeparators = new(@"[\s\p{Pd}()]");
    private static readonly Regex RecipientNumber = new(@"\A\+?[0-9]{3,20}\z");
    private PendingInput? _pendingInput;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Telegram update polling started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updates = await telegram.GetUpdates(
                    offset: checked((int)state.Snapshot.UpdateOffset), timeout: 25,
                    allowedUpdates: [UpdateType.Message, UpdateType.CallbackQuery], cancellationToken: stoppingToken);
                if (updates.Length == 0)
                    await state.ClearUpdateOffsetAsync(stoppingToken);

                foreach (var update in updates.OrderBy(update => update.Id))
                {
                    if (update.Id < state.Snapshot.UpdateOffset)
                        continue;

                    // Claim the update before executing it so a restart cannot resubmit an SMS.
                    await state.AdvanceUpdateOffsetAsync(update.Id, stoppingToken);
                    if (update.Message is { Chat.Type: ChatType.Private } message &&
                        message.Chat.Id == settings.OwnerUserId && message.From?.Id == settings.OwnerUserId)
                    {
                        await HandleInteractionAsync(message.Text, stoppingToken);
                    }
                    else if (update.CallbackQuery is { Message.Chat.Type: ChatType.Private } callback &&
                        callback.Message.Chat.Id == settings.OwnerUserId && callback.From.Id == settings.OwnerUserId)
                    {
                        await telegram.AnswerCallbackQuery(callback.Id, cancellationToken: stoppingToken);
                        if (callback.Data is { } data && data.StartsWith(SelectLineButton, StringComparison.Ordinal))
                            await HandleInteractionAsync(null, stoppingToken, lineButtonData: data);
                    }
                }
            }
            catch (ApiRequestException error)
            {
                logger.LogWarning("Telegram Bot API returned error code {ErrorCode}.", error.ErrorCode);
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, error.Parameters?.RetryAfter ?? 5)), stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested &&
                error is RequestException or OperationCanceledException or IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Failed to process Telegram updates ({ErrorType}). Retrying.", error.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private static string? GetCommand(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        text = text switch
        {
            var label when label == Resources.Commands_LinesButton => "/lines",
            var label when label == Resources.Commands_SelectLineButton => "/line",
            var label when label == Resources.Commands_AutomaticLineButton => "/auto",
            var label when label == Resources.Commands_SendButton || label == Resources.Commands_SendHelpButton => "/send",
            var label when label == Resources.Commands_ReplyButton || label == Resources.Commands_ReplyHelpButton => "/reply",
            var label when label == Resources.Commands_CancelButton => "/cancel",
            _ => text
        };

        var command = CommandName.Match(text);
        return command.Success ? command.Groups[1].Value : null;
    }

    private async Task HandleInteractionAsync(string? text, CancellationToken token, string? lineButtonData = null)
    {
        var command = GetCommand(text);
        var input = _pendingInput;
        var keepInput = command == "/help";
        _pendingInput = keepInput ? input : null;
        BotResponse? reply;
        try
        {
            if (lineButtonData is not null)
                reply = await SelectLineAsync(lineButtonData, fromButton: true, token);
            else if (command is not null)
                reply = await ExecuteCommandAsync(command, input is not null, token);
            else
                reply = input is null ? null : await ContinueInputAsync(input, text, token);
        }
        catch (BridgeException error)
        {
            logger.LogWarning("Bridge WebUI request failed with HTTP {StatusCode}.", (int)error.Status);
            reply = new(error.Status == HttpStatusCode.Unauthorized
                ? Resources.Commands_BridgeLoginRejected
                : string.Format(settings.Language, Resources.Commands_BridgeHttpError, (int)error.Status));
        }
        catch (Exception error) when (!token.IsCancellationRequested &&
            error is HttpRequestException or JsonException or OperationCanceledException)
        {
            logger.LogWarning("Failed to read data from the bridge WebUI ({ErrorType}).", error.GetType().Name);
            reply = new(Resources.Commands_BridgeReadFailed);
        }

        if (reply is null)
            return;

        var parts = TelegramMessages.SplitText(reply.Text).ToArray();
        for (var i = 0; i < parts.Length; i++)
            await telegram.SendTextAsync(settings.OwnerUserId, parts[i], token, i == parts.Length - 1 ? reply.Keyboard : null);
        if (!keepInput)
            _pendingInput = reply.NextInput;
    }

    private async Task<BotResponse> ExecuteCommandAsync(string command, bool hadPendingInput, CancellationToken token) => command switch
    {
        "/start" or "/help" => new(Resources.Commands_Help, CreateMainKeyboard()),
        "/lines" => DescribeLines(await bridge.GetOverviewAsync(token)),
        "/line" => DescribeLines(await bridge.GetOverviewAsync(token), Resources.Commands_SelectLinePrompt),
        "/auto" => await EnableAutomaticLineAsync(token),
        "/send" => new(Resources.Commands_RecipientPrompt, NextInput: new PendingInput.PhoneNumber()),
        "/reply" => await StartReplyAsync(token),
        "/cancel" => new(hadPendingInput ? Resources.Commands_ActionCancelled : Resources.Commands_NoPendingAction),
        _ => new(Resources.Commands_UnknownCommand)
    };

    private async Task<BotResponse> ContinueInputAsync(PendingInput input, string? text, CancellationToken token)
    {
        switch (input)
        {
            case PendingInput.PhoneNumber:
                var recipient = NormalizeRecipient(text);
                return recipient is not null
                    ? new(string.Format(settings.Language, Resources.Commands_SendTextPrompt, recipient), NextInput: new PendingInput.SmsText(recipient))
                    : new(Resources.Commands_InvalidRecipient, NextInput: input);
            case PendingInput.SmsText sms:
                return string.IsNullOrWhiteSpace(text)
                    ? new(Resources.Commands_InvalidSmsText, NextInput: input)
                    : new(await QueueSmsAsync(sms.Recipient, text, sms.Incoming, token));
            case PendingInput.Line:
                return await SelectLineAsync(text?.Trim(), fromButton: false, token);
            default:
                throw new InvalidOperationException("Unknown input step.");
        }
    }

    private static ReplyKeyboardMarkup CreateMainKeyboard() => new(
    [
        [new KeyboardButton(Resources.Commands_SendButton), new KeyboardButton(Resources.Commands_ReplyButton)],
        [new KeyboardButton(Resources.Commands_LinesButton), new KeyboardButton(Resources.Commands_AutomaticLineButton)],
        [new KeyboardButton(Resources.Commands_SelectLineButton), new KeyboardButton(Resources.Commands_CancelButton)]
    ]) { ResizeKeyboard = true };

    private BotResponse DescribeLines(BridgeOverview overview, string? prompt = null)
    {
        var selection = state.Snapshot.SelectedLine;
        var mode = selection is not null
            ? string.Format(settings.Language, Resources.Commands_LineModeManual, selection)
            : Resources.Commands_LineModeAutomatic;
        var result = new StringBuilder(string.Format(settings.Language, Resources.Commands_LinesTitle, mode));
        if (overview.Bridge.SmsEnabled is null)
            result.Append('\n').Append(Resources.Commands_LinesSmsSupportUnknown);
        else if (overview.Bridge.SmsEnabled == false)
            result.Append('\n').Append(Resources.Commands_SmsSendingDisabled);

        if (overview.Lines.Length == 0)
            result.Append('\n').Append(Resources.Commands_NoLines);

        foreach (var line in overview.Lines)
        {
            var transport = line.Transport switch { "cs" => "GSM", "vowifi" => "VoWiFi", "volte" => "VoLTE", _ => line.Transport };
            var details = string.Join(DetailSeparator, new[] { transport, line.Msisdn, line.Carrier }.Where(value => !string.IsNullOrWhiteSpace(value)));
            result.Append('\n').Append(line.Id).Append(DetailSeparator).Append(details);
            if (line.Health == "down")
                result.Append(DetailSeparator).Append(Resources.Commands_LineDown);
            else if (line.Health == "stale")
                result.Append(DetailSeparator).Append(Resources.Commands_LineHealthUnknown);

            if (!line.SupportsSms)
                result.Append(DetailSeparator).Append(Resources.Commands_LineReceiveOnly);
        }

        var buttons = overview.Lines.Where(line => line.SupportsSms)
            .Select(line => new[] { InlineKeyboardButton.WithCallbackData(line.Id, LineButtonData(line.Id)) })
            .ToArray();
        if (prompt is not null)
            result.Append("\n\n").Append(prompt);

        return new(result.ToString(), buttons.Length > 0 ? new InlineKeyboardMarkup(buttons) : null,
            prompt is null ? null : new PendingInput.Line());
    }

    // Fixed-length callback data keeps long and non-ASCII line IDs within Telegram's 64-byte limit.
    private static string LineButtonData(string id) =>
        SelectLineButton + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(id)));

    private async Task<BotResponse> EnableAutomaticLineAsync(CancellationToken token)
    {
        await state.SelectLineAsync(null, token);
        return new(Resources.Commands_AutomaticLineSelected);
    }

    private async Task<BotResponse> SelectLineAsync(string? id, bool fromButton, CancellationToken token)
    {
        if (!fromButton && id == "auto")
            return await EnableAutomaticLineAsync(token);

        var overview = await bridge.GetOverviewAsync(token);
        var selected = overview.Lines.FirstOrDefault(line => line.SupportsSms &&
            (fromButton ? LineButtonData(line.Id) : line.Id) == id);
        if (selected is null)
            return DescribeLines(overview, Resources.Commands_InvalidLine);

        await state.SelectLineAsync(selected.Id, token);
        return new(string.Format(settings.Language, Resources.Commands_LineSelected, selected.Id));
    }

    private async Task<ReceivedSms?> LatestReceivedAsync(CancellationToken token)
    {
        var fromBridge = await bridge.GetLatestReceivedSmsAsync(token);
        var fromWebhook = state.Snapshot.LatestReceived;
        return fromWebhook is not null && (fromBridge is null || fromWebhook.ReceivedAt > fromBridge.ReceivedAt)
            ? fromWebhook
            : fromBridge;
    }

    private async Task<BotResponse> StartReplyAsync(CancellationToken token)
    {
        var incoming = await LatestReceivedAsync(token);
        if (incoming is null)
            return new(Resources.Commands_NoIncomingSmsToReply);

        var recipient = NormalizeRecipient(incoming.Sender);
        if (recipient is null)
            return new(Resources.Commands_ReplySenderNotNumber);

        return new(string.Format(settings.Language, Resources.Commands_ReplyTextPrompt, recipient),
            NextInput: new PendingInput.SmsText(recipient, incoming));
    }

    private static string? NormalizeRecipient(string? input)
    {
        if (input is null)
            return null;

        var recipient = RecipientSeparators.Replace(input, "");
        return RecipientNumber.IsMatch(recipient) ? recipient : null;
    }

    private async Task<string> QueueSmsAsync(string recipient, string text, ReceivedSms? incoming, CancellationToken token)
    {
        var overview = await bridge.GetOverviewAsync(token);
        if (overview.Bridge.SmsEnabled is null)
            return Resources.Commands_SmsSendingUnsupported;
        if (overview.Bridge.SmsEnabled == false)
            return Resources.Commands_SmsSendingDisabled;

        var lines = overview.Lines.Where(line => line.SupportsSms).ToArray();
        if (lines.Length == 0)
            return Resources.Commands_NoImsLines;

        var id = incoming is not null
            ? incoming.ModuleId
            : state.Snapshot.SelectedLine ?? (await LatestReceivedAsync(token))?.ModuleId;
        if (incoming is null && id is null && lines.Length == 1)
            id = lines[0].Id;

        if (string.IsNullOrWhiteSpace(id))
            return incoming is null ? Resources.Commands_NoIncomingSms : Resources.Commands_ReplyLineUnknown;

        if (!lines.Any(line => line.Id == id))
            return string.Format(settings.Language,
                incoming is null ? Resources.Commands_LineUnavailableForSending : Resources.Commands_ReplyLineUnavailable, id);

        try
        {
            var queued = await bridge.SendAsync(id, recipient, text, token);
            logger.LogInformation("Bridge queued SMS {SmsId} with {PartCount} parts.", queued.Id, queued.Parts);
            return string.Format(settings.Language, Resources.Commands_SmsQueued, queued.Id, queued.Parts, id);
        }
        catch (BridgeException error) when (error.Error != "wrong_line_agent" && (int)error.Status >= 500)
        {
            logger.LogWarning("Bridge returned HTTP {StatusCode} for SMS submission. The outcome is unknown.", (int)error.Status);
            return string.Format(settings.Language, Resources.Commands_SmsSubmissionHttpError, (int)error.Status);
        }
        catch (BridgeException error) when (!string.IsNullOrWhiteSpace(error.Detail))
        {
            logger.LogWarning("Bridge rejected SMS submission with HTTP {StatusCode}.", (int)error.Status);
            return string.Format(settings.Language, Resources.Commands_SmsSubmissionRejected, error.Detail);
        }
        catch (Exception error) when (!token.IsCancellationRequested &&
            error is HttpRequestException or JsonException or OperationCanceledException)
        {
            logger.LogWarning("Bridge did not confirm SMS submission ({ErrorType}).", error.GetType().Name);
            return Resources.Commands_SmsSubmissionUnconfirmed;
        }
    }

    private sealed record BotResponse(string Text, ReplyMarkup? Keyboard = null, PendingInput? NextInput = null);

    private abstract record PendingInput
    {
        public sealed record PhoneNumber : PendingInput;
        public sealed record SmsText(string Recipient, ReceivedSms? Incoming = null) : PendingInput;
        public sealed record Line : PendingInput;
    }
}
