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
                        await HandleInteractionAsync(message.Text, stoppingToken, sentAt: message.Date);
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
                var log = TelegramBotClientWithDiagnostics.GetLogDetails(error, settings.BotToken);
                logger.LogWarning(log.Error, "Telegram Bot API returned error code {ErrorCode}.{TelegramHttpResponse}",
                    error.ErrorCode, log.Response);
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, error.Parameters?.RetryAfter ?? 5)), stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested &&
                error is RequestException or OperationCanceledException or IOException or UnauthorizedAccessException)
            {
                var log = TelegramBotClientWithDiagnostics.GetLogDetails(error, settings.BotToken);
                logger.LogWarning(log.Error, "Failed to process Telegram updates. Retrying.{TelegramHttpResponse}", log.Response);
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

    private async Task HandleInteractionAsync(string? text, CancellationToken token,
        string? lineButtonData = null, DateTime? sentAt = null)
    {
        var command = GetCommand(text);
        var input = _pendingInput;
        var activeReply = input is PendingInput.SmsText { IsReply: true } sms ? sms : null;
        var keepInput = command == "/help" ||
            (activeReply is not null && (lineButtonData is not null || command is not (null or "/cancel")));
        _pendingInput = keepInput ? input : null;
        BotResponse? reply;
        try
        {
            if (activeReply is not null && command == "/reply")
                reply = PromptForReplyText(activeReply);
            else if (activeReply is not null && command is ("/send" or "/line"))
                reply = new(WithSender(Resources.Commands_CompletePendingReply, activeReply.Line));
            else if (lineButtonData is not null)
                reply = await SelectLineAsync(lineButtonData, fromButton: true, token);
            else if (command is not null)
                reply = await ExecuteCommandAsync(command, input is not null, sentAt, token);
            else
                reply = input is null ? null : await ContinueInputAsync(input, text, token);
        }
        catch (BridgeException error)
        {
            reply = new(DescribeBridgeError(error), NextInput: activeReply);
        }
        catch (Exception error) when (!token.IsCancellationRequested &&
            error is HttpRequestException or JsonException or OperationCanceledException)
        {
            logger.LogWarning(error, "Failed to read data from the bridge WebUI.");
            reply = new(Resources.Commands_BridgeReadFailed, NextInput: activeReply);
        }

        if (reply is null)
            return;

        var parts = TelegramMessages.SplitText(reply.Text).ToArray();
        for (var i = 0; i < parts.Length; i++)
            await telegram.SendTextAsync(settings.OwnerUserId, parts[i], token, i == parts.Length - 1 ? reply.Keyboard : null);
        if (!keepInput)
            _pendingInput = reply.NextInput;
    }

    private async Task<BotResponse> ExecuteCommandAsync(string command, bool hadPendingInput, DateTime? sentAt,
        CancellationToken token) => command switch
    {
        "/start" or "/help" => new(Resources.Commands_Help, CreateMainKeyboard()),
        "/lines" => DescribeLines(await bridge.GetOverviewAsync(token)),
        "/line" => DescribeLines(await bridge.GetOverviewAsync(token), Resources.Commands_SelectLinePrompt),
        "/auto" => await EnableAutomaticLineAsync(token),
        "/send" => await StartSmsAsync(isReply: false, token),
        "/reply" => await StartSmsAsync(isReply: true, token, receivedBefore: EndOfMessageSecond(sentAt)),
        "/cancel" => new(hadPendingInput ? Resources.Commands_ActionCancelled : Resources.Commands_NoPendingAction),
        _ => new(Resources.Commands_UnknownCommand)
    };

    private async Task<BotResponse> ContinueInputAsync(PendingInput input, string? text, CancellationToken token)
    {
        switch (input)
        {
            case PendingInput.PhoneNumber phone:
                var recipient = NormalizeRecipient(text);
                return recipient is not null
                    ? new(WithSender(string.Format(settings.Language, Resources.Commands_SendTextPrompt, recipient), phone.Line),
                        NextInput: new PendingInput.SmsText(recipient, phone.Line, IsReply: false))
                    : new(WithSender(Resources.Commands_InvalidRecipient, phone.Line), NextInput: input);
            case PendingInput.SmsText sms:
                return string.IsNullOrWhiteSpace(text)
                    ? new(WithSender(Resources.Commands_InvalidSmsText, sms.Line), NextInput: input)
                    : await SubmitSmsAsync(sms, text, token);
            case PendingInput.LineSelection:
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
            prompt is null ? null : new PendingInput.LineSelection());
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

    // Telegram supplies the message date with one-second precision.
    private static DateTimeOffset? EndOfMessageSecond(DateTime? sentAt) => sentAt is { } date
        ? new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc)).AddSeconds(1)
        : null;

    private async Task<ReceivedSms?> LatestReceivedAsync(CancellationToken token, DateTimeOffset? receivedBefore = null)
    {
        var fromWebhook = state.Snapshot.LatestReceived;
        var fromBridge = await bridge.GetLatestReceivedSmsAsync(token, receivedBefore);
        var latestWebhook = state.Snapshot.LatestReceived;
        if (receivedBefore is null || latestWebhook?.ReceivedAt < receivedBefore)
            fromWebhook = latestWebhook;
        else if (fromWebhook?.ReceivedAt >= receivedBefore)
            fromWebhook = null;

        return fromWebhook is not null && (fromBridge is null || fromWebhook.ReceivedAt > fromBridge.ReceivedAt)
            ? fromWebhook
            : fromBridge;
    }

    private async Task<BotResponse> StartSmsAsync(bool isReply, CancellationToken token, DateTimeOffset? receivedBefore = null)
    {
        var lineId = state.Snapshot.SelectedLine;
        string? recipient = null;
        if (isReply)
        {
            var incoming = await LatestReceivedAsync(token, receivedBefore);
            if (incoming is null)
                return new(Resources.Commands_NoIncomingSmsToReply);

            recipient = NormalizeRecipient(incoming.Sender);
            if (recipient is null)
                return new(Resources.Commands_ReplySenderNotNumber);

            lineId = incoming.ModuleId;
        }

        var overview = await bridge.GetOverviewAsync(token);
        if (GetSmsAvailabilityError(overview) is { } error)
            return new(error);

        var lines = overview.Lines.Where(line => line.SupportsSms).ToArray();
        if (!isReply && lineId is null)
        {
            lineId = (await LatestReceivedAsync(token))?.ModuleId;
            if (lineId is null && lines.Length == 1)
                lineId = lines[0].Id;
        }

        if (string.IsNullOrWhiteSpace(lineId))
            return new(isReply ? Resources.Commands_ReplyLineUnknown : Resources.Commands_NoIncomingSms);

        var line = lines.FirstOrDefault(line => line.Id == lineId);
        if (line is null)
            return new(LineUnavailable(lineId, isReply));

        return recipient is null
            ? new(WithSender(Resources.Commands_RecipientPrompt, line), NextInput: new PendingInput.PhoneNumber(line))
            : PromptForReplyText(new PendingInput.SmsText(recipient, line, IsReply: true));
    }

    private BotResponse PromptForReplyText(PendingInput.SmsText sms) =>
        new(WithSender(string.Format(settings.Language, Resources.Commands_ReplyTextPrompt, sms.Recipient), sms.Line),
            NextInput: sms);

    private static string? NormalizeRecipient(string? input)
    {
        if (input is null)
            return null;

        var recipient = RecipientSeparators.Replace(input, "");
        return RecipientNumber.IsMatch(recipient) ? recipient : null;
    }

    private static string? GetSmsAvailabilityError(BridgeOverview overview) => overview.Bridge.SmsEnabled switch
    {
        null => Resources.Commands_SmsSendingUnsupported,
        false => Resources.Commands_SmsSendingDisabled,
        true when !overview.Lines.Any(line => line.SupportsSms) => Resources.Commands_NoImsLines,
        _ => null
    };

    private string LineUnavailable(string id, bool isReply) => string.Format(settings.Language,
        isReply ? Resources.Commands_ReplyLineUnavailable : Resources.Commands_LineUnavailableForSending, id);

    private static string? SenderNumber(BridgeLine line) => string.IsNullOrWhiteSpace(line.Msisdn) ? null : line.Msisdn.Trim();

    private string WithSender(string text, BridgeLine line) => text + "\n\n" + string.Format(settings.Language,
        Resources.Commands_SendingFrom, line.Id, SenderNumber(line) ?? Resources.Commands_SenderNumberUnknown);

    private async Task<BotResponse> SubmitSmsAsync(PendingInput.SmsText sms, string text, CancellationToken token)
    {
        var overview = await bridge.GetOverviewAsync(token);
        var pendingReply = sms.IsReply ? sms : null;
        if (GetSmsAvailabilityError(overview) is { } error)
            return new(WithSender(error, sms.Line), NextInput: pendingReply);

        var line = overview.Lines.FirstOrDefault(line => line.SupportsSms && line.Id == sms.Line.Id);
        if (line is null)
            return new(WithSender(LineUnavailable(sms.Line.Id, sms.IsReply), sms.Line), NextInput: pendingReply);

        if (SenderNumber(line) != SenderNumber(sms.Line))
            return sms.IsReply
                ? new(WithSender(Resources.Commands_ReplySenderNumberChanged, sms.Line), NextInput: sms)
                : new(WithSender(Resources.Commands_SenderNumberChanged, line), NextInput: sms with { Line = line });

        var result = await QueueSmsAsync(line.Id, sms.Recipient, text, token);
        return new(WithSender(result, line));
    }

    private async Task<string> QueueSmsAsync(string lineId, string recipient, string text, CancellationToken token)
    {
        try
        {
            var queued = await bridge.SendAsync(lineId, recipient, text, token);
            logger.LogInformation("Bridge queued SMS {SmsId} with {PartCount} parts.", queued.Id, queued.Parts);
            return string.Format(settings.Language, Resources.Commands_SmsQueued, queued.Id, queued.Parts);
        }
        catch (BridgeException error) when (error.Error != "wrong_line_agent" && (int)error.Status >= 500)
        {
            logger.LogWarning(error, "Bridge returned HTTP {StatusCode} for SMS submission. The outcome is unknown.", (int)error.Status);
            return string.Format(settings.Language, Resources.Commands_SmsSubmissionHttpError, (int)error.Status);
        }
        catch (BridgeException error) when (!string.IsNullOrWhiteSpace(error.Detail))
        {
            logger.LogWarning(error, "Bridge rejected SMS submission with HTTP {StatusCode}.", (int)error.Status);
            return string.Format(settings.Language, Resources.Commands_SmsSubmissionRejected, error.Detail);
        }
        catch (BridgeException error)
        {
            return DescribeBridgeError(error);
        }
        catch (Exception error) when (!token.IsCancellationRequested &&
            error is HttpRequestException or JsonException or OperationCanceledException)
        {
            logger.LogWarning(error, "Bridge did not confirm SMS submission.");
            return Resources.Commands_SmsSubmissionUnconfirmed;
        }
    }

    private string DescribeBridgeError(BridgeException error)
    {
        logger.LogWarning(error, "Bridge WebUI request failed with HTTP {StatusCode}.", (int)error.Status);
        return error.Status == HttpStatusCode.Unauthorized
            ? Resources.Commands_BridgeLoginRejected
            : string.Format(settings.Language, Resources.Commands_BridgeHttpError, (int)error.Status);
    }

    private sealed record BotResponse(string Text, ReplyMarkup? Keyboard = null, PendingInput? NextInput = null);

    private abstract record PendingInput
    {
        public sealed record PhoneNumber(BridgeLine Line) : PendingInput;
        public sealed record SmsText(string Recipient, BridgeLine Line, bool IsReply) : PendingInput;
        public sealed record LineSelection : PendingInput;
    }
}
