using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MironovLab.SmsForwardBot.Properties;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace MironovLab.SmsForwardBot.Bot;

internal sealed class DiscordWebhook(
    BotSettings settings, BotStateStore state, ILogger<DiscordWebhook> logger) : IDiscordWebhook
{
    public bool MatchesSecret(string secret) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(settings.WebhookSecret));

    public async Task<bool> EnqueueAsync(IReadOnlyList<DiscordEmbed?> embeds, CancellationToken token)
    {
        var notifications = embeds.Select(Parse).OfType<DiscordNotification>().ToArray();
        if (notifications.Length == 0)
            return true;

        if (!await state.EnqueueAsync(notifications, token))
        {
            logger.LogWarning("The Telegram notification queue is full. Rejected a webhook with {NotificationCount} notifications.", notifications.Length);
            return false;
        }

        logger.LogInformation("Processed {NotificationCount} notifications from a Discord webhook.", notifications.Length);
        return true;
    }

    private DiscordNotification? Parse(DiscordEmbed? embed)
    {
        if (embed is null)
            return null;

        var category = Field(embed, "Category");
        var status = Field(embed, "Status");
        string? legacyTitle = null;
        if (category is null)
        {
            legacyTitle = embed.Title;
            if (legacyTitle?.StartsWith("[Late] ", StringComparison.Ordinal) == true)
                legacyTitle = legacyTitle[7..];
            (category, status) = ReadLegacyFields(legacyTitle, Field(embed, "To"));
        }

        var format = GetFormat(category, status);
        if (format is not { } notification)
            return ParseBridgeAlert(embed, category);

        var inbound = category == "sms_received";
        var delivery = category == "sms_delivery";
        var module = Field(embed, "Module");
        var contact = Field(embed, inbound ? "Sender" : "To");
        if (string.IsNullOrWhiteSpace(module) || string.IsNullOrWhiteSpace(contact))
            return null;

        var (heading, legacyOutcome) = notification;
        var originalTimeField = inbound ? "Originally received" : delivery ? "Originally settled" : "Originally finished";
        var timestamp = Field(embed, originalTimeField) ?? embed.Timestamp;
        var hasTime = TryParseTimestamp(timestamp, out var time);
        var message = new NotificationText(heading);
        message.AppendField(inbound ? Resources.Notifications_Sender : Resources.Notifications_Recipient, contact);
        var phone = Field(embed, "Phone");
        var line = !string.IsNullOrWhiteSpace(phone) && !string.Equals(phone, "unknown", StringComparison.OrdinalIgnoreCase)
            ? $"{module} ({phone})"
            : module;
        message.AppendField(Resources.Notifications_Line, line);

        var smsId = !inbound && Field(embed, "SMS ID")?.Trim() is { Length: > 0 } id
            ? "#" + id.TrimStart('#')
            : null;
        if (smsId is not null)
            message.AppendField(Resources.Notifications_SmsId, smsId);

        if (delivery && Field(embed, "Sent") is { Length: > 0 } sent)
        {
            var sentTimeText = TryParseTimestamp(sent, out var sentTime) ? FormatTimestamp(sentTime) : sent;
            message.AppendField(Resources.Notifications_QueuedAt, sentTimeText);
        }

        if (hasTime)
            message.AppendField(delivery ? Resources.Notifications_DeliveryRecordedAt : Resources.Notifications_Timestamp, FormatTimestamp(time));

        var parts = Field(embed, delivery ? "Parts delivered" : "Parts sent");
        if (parts is { Length: > 0 } && Field(embed, "Parts total") is { Length: > 0 } totalParts)
            parts = $"{parts}/{totalParts}";

        if (parts is { Length: > 0 })
            message.AppendField(delivery ? Resources.Notifications_DeliveredParts : Resources.Notifications_AcceptedParts, parts);

        if (Field(embed, "Error") is { Length: > 0 } error)
            message.AppendField(Resources.Notifications_FailureReason, error);

        if (inbound && embed.Description is { Length: > 0 } body)
            message.AppendParagraph(body);

        // Derive the old title from stable fields so retries match fingerprints already on disk.
        if (status == "partially_delivered" && parts is { Length: > 0 })
            legacyOutcome = $"{legacyOutcome} ({parts})";
        var fingerprintTitle = legacyTitle ?? (inbound ? $"SMS from {contact}" : $"SMS to {contact} {legacyOutcome}");
        var fingerprint = hasTime || smsId is not null
            ? Fingerprint(smsId is null ? fingerprintTitle : $"{smsId} {fingerprintTitle}")
            : null;
        // Older bot versions may already have forwarded a structured payload with a bare ID.
        var bareIdFingerprint = hasTime && smsId is not null
            ? Fingerprint($"{smsId[1..]} {fingerprintTitle}")
            : null;
        var received = inbound ? new ReceivedSms(module, hasTime ? time : DateTimeOffset.UtcNow, contact) : null;
        return new DiscordNotification(message.Build(), fingerprint, received, bareIdFingerprint);

        string Fingerprint(string title) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            title, module, contact, timestamp, embed.Description, instance = embed.Footer?.Text
        })));
    }

    private DiscordNotification? ParseBridgeAlert(DiscordEmbed embed, string? category)
    {
        // The bridge uses these title prefixes for operational alerts and recovery notices.
        if (string.IsNullOrWhiteSpace(category) || category == "sms" || category.StartsWith("sms_", StringComparison.Ordinal) ||
            embed.Title is not { } title ||
            !(title.StartsWith("Critical: ", StringComparison.Ordinal) || title.StartsWith("Recovered: ", StringComparison.Ordinal)))
            return null;

        var module = Field(embed, "Module/Line");
        var phone = Field(embed, "Phone");
        var timestamp = embed.Timestamp;
        var instance = embed.Footer?.Text;
        var hasTime = TryParseTimestamp(timestamp, out var time);

        var message = new NotificationText(Resources.Notifications_BridgeAlertTitle);
        message.AppendLine(title);
        if (!string.IsNullOrWhiteSpace(module))
            message.AppendField(Resources.Notifications_Line, module);
        if (!string.IsNullOrWhiteSpace(phone) && !string.Equals(phone, "unknown", StringComparison.OrdinalIgnoreCase))
            message.AppendField(Resources.Notifications_Phone, phone);
        if (hasTime)
            message.AppendField(Resources.Notifications_Timestamp, FormatTimestamp(time));
        if (!string.IsNullOrWhiteSpace(embed.Description))
            message.AppendParagraph(embed.Description);
        if (!string.IsNullOrWhiteSpace(instance))
            message.AppendParagraph(instance);

        var fingerprint = hasTime ? Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            category, title, module, phone, timestamp, embed.Description, instance
        }))) : null;
        return new DiscordNotification(message.Build(), fingerprint, Received: null, BareIdFingerprint: null);
    }

    private static (string Heading, string? LegacyOutcome)? GetFormat(string? category, string? status) =>
        (category, status) switch
        {
            ("sms_received", _) => (Resources.Notifications_IncomingTitle, null),
            ("sms_send", "sent") => (Resources.Notifications_AcceptedTitle, "sent"),
            ("sms_send", "failed") => (Resources.Notifications_UnsentTitle, "not sent"),
            ("sms_delivery", "delivered") => (Resources.Notifications_DeliveredTitle, "delivered"),
            ("sms_delivery", "partially_delivered") => (Resources.Notifications_PartiallyDeliveredTitle, "partially delivered"),
            ("sms_delivery", "undelivered") => (Resources.Notifications_UndeliveredTitle, "undelivered"),
            ("sms_delivery", "unknown") => (Resources.Notifications_DeliveryUnknownTitle, "delivery unknown"),
            _ => null
        };

    private static (string? Category, string? Status) ReadLegacyFields(string? title, string? recipient)
    {
        if (title is null)
            return default;

        if (title.StartsWith("SMS from ", StringComparison.Ordinal))
            return ("sms_received", null);

        if (string.IsNullOrEmpty(recipient))
            return default;

        var prefix = $"SMS to {recipient} ";
        if (!title.StartsWith(prefix, StringComparison.Ordinal))
            return default;

        return title[prefix.Length..] switch
        {
            "sent" => ("sms_send", "sent"),
            "not sent" => ("sms_send", "failed"),
            "delivery unknown" => ("sms_delivery", "unknown"),
            var outcome when outcome.StartsWith("partially delivered (", StringComparison.Ordinal) && outcome.EndsWith(')')
                => ("sms_delivery", "partially_delivered"),
            var outcome => ("sms_delivery", outcome)
        };
    }

    private static string? Field(DiscordEmbed embed, string name) =>
        embed.Fields?.FirstOrDefault(field => field?.Name == name)?.Value;

    private static bool TryParseTimestamp(string? timestamp, out DateTimeOffset time) =>
        DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out time);

    private string FormatTimestamp(DateTimeOffset time) =>
        time.ToString("d MMMM yyyy, HH:mm:ss 'UTC'zzz", settings.Language);

    private sealed class NotificationText
    {
        private readonly StringBuilder _text = new();
        private readonly List<MessageEntity> _entities = [];

        public NotificationText(string heading) => AppendBold(heading);

        public void AppendField(string label, string value)
        {
            _text.Append('\n');
            AppendBold(label);
            _text.Append(' ').Append(value);
        }

        public void AppendLine(string value) => _text.Append('\n').Append(value);

        public void AppendParagraph(string value) => _text.Append("\n\n").Append(value);

        public TelegramNotification Build() => new(_text.ToString(), _entities.ToArray());

        private void AppendBold(string text)
        {
            _entities.Add(new MessageEntity { Type = MessageEntityType.Bold, Offset = _text.Length, Length = text.Length });
            _text.Append(text);
        }
    }
}

internal sealed record DiscordNotification(TelegramNotification Message, string? Fingerprint, ReceivedSms? Received, string? BareIdFingerprint);