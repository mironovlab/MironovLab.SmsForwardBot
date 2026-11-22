using MironovLab.SmsForwardBot.Properties;
using Telegram.Bot;

namespace MironovLab.SmsForwardBot.Bot;

internal static class SmsForwardBotServiceCollectionExtensions
{
    private const string TelegramHttpClientKey = "telegram";

    public static IServiceCollection AddSmsForwardBot(
        this IServiceCollection services, IConfiguration configuration, string contentRoot)
    {
        services.AddLogging();
        var settings = BotSettings.Load(configuration, contentRoot);
        Resources.Culture = settings.Language;
        services.AddSingleton(settings);
        services.AddSingleton<BotStateStore>();
        services.AddSingleton<BridgeClient>();
        services.AddKeyedSingleton<HttpClient>(TelegramHttpClientKey, (_, _) =>
            new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            {
                Timeout = TimeSpan.FromSeconds(40)
            });
        services.AddSingleton<ITelegramBotClient>(CreateTelegramBotClient);
        services.AddSingleton<IDiscordWebhook, DiscordWebhook>();
        services.AddHostedService<Worker>();
        services.AddHostedService<DiscordForwarder>();
        return services;
    }

    private static TelegramBotClient CreateTelegramBotClient(IServiceProvider services)
    {
        var settings = services.GetRequiredService<BotSettings>();
        var options = new TelegramBotClientOptions(settings.BotToken, settings.TelegramApiUrl.GetLeftPart(UriPartial.Authority))
        {
            RetryCount = 0
        };
        var httpClient = services.GetRequiredKeyedService<HttpClient>(TelegramHttpClientKey);
        var client = new TelegramBotClient(options, httpClient);

        // The SDK uses the origin only. Keep the path of a Bot API server behind a reverse proxy.
        if (settings.TelegramApiUrl.AbsolutePath != "/")
        {
            client.OnMakingApiRequest += (_, args, _) =>
            {
                var request = args.HttpRequestMessage!;
                request.RequestUri = new Uri(settings.TelegramApiUrl, "." + request.RequestUri!.PathAndQuery);
                return ValueTask.CompletedTask;
            };
        }

        return client;
    }
}
