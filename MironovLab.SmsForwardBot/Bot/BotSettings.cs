using System.Globalization;
using System.Text.RegularExpressions;

namespace MironovLab.SmsForwardBot.Bot;

internal sealed record BotSettings(
    string BotToken,
    long OwnerUserId,
    Uri TelegramApiUrl,
    Uri WebUiUrl,
    string WebUiPassword,
    string WebhookSecret,
    string StateFile,
    CultureInfo Language)
{
    public static BotSettings Load(IConfiguration config, string contentRoot)
    {
        var token = Required(config, "Telegram:BotToken");
        if (!Regex.IsMatch(token, @"\A[0-9]+:[A-Za-z0-9_-]+\z"))
            throw new InvalidOperationException("Telegram:BotToken must contain a valid BotFather token.");

        if (!long.TryParse(config["Telegram:OwnerUserId"], out var userId) || userId <= 0)
            throw new InvalidOperationException("Telegram:OwnerUserId must be a positive numeric Telegram user ID.");

        var secret = Required(config, "DiscordWebhook:Secret");
        if (!Regex.IsMatch(secret, @"\A[A-Za-z0-9_-]{32,128}\z"))
            throw new InvalidOperationException("DiscordWebhook:Secret must contain 32 to 128 characters from A-Z, a-z, 0-9, _ or -.");

        var language = config["Language"]?.Trim().ToLowerInvariant() ?? "ru";
        if (language is not ("en" or "ru"))
            throw new InvalidOperationException("Language must be 'en' or 'ru' in appsettings.json.");

        return new BotSettings(token, userId, HttpUrl(config, "Telegram:ApiBaseUrl"),
            HttpUrl(config, "Bridge:WebUiUrl"), Required(config, "Bridge:WebUiPassword"), secret,
            Path.GetFullPath(Required(config, "StateFile"), contentRoot), CultureInfo.GetCultureInfo(language));
    }

    private static string Required(IConfiguration config, string key) =>
        config[key] is { } value && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Set {key} in appsettings.json.");

    private static Uri HttpUrl(IConfiguration config, string key)
    {
        var value = Required(config, key);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            throw new InvalidOperationException($"{key} must be an HTTP(S) URL without credentials, a query, or a fragment.");
        }

        return new Uri(uri.AbsoluteUri.TrimEnd('/') + '/');
    }
}
