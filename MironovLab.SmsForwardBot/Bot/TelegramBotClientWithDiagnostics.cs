using System.Net;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests.Abstractions;

namespace MironovLab.SmsForwardBot.Bot;

internal sealed class TelegramBotClientWithDiagnostics : TelegramBotClient
{
    private const string ResponseDetailsKey = "TelegramHttpResponse";
    private const int MaxResponseFieldLength = 4096;
    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie", "Set-Cookie2"
    };

    // Polling and notification delivery share the client, so keep each response with its own call.
    private readonly AsyncLocal<ResponseCapture?> _currentResponse = new();

    public TelegramBotClientWithDiagnostics(TelegramBotClientOptions options, HttpClient httpClient)
        : base(options, httpClient)
    {
        OnApiResponseReceived += CaptureErrorResponseAsync;
    }

    public override async Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request,
        CancellationToken cancellationToken = default)
    {
        var previous = _currentResponse.Value;
        var capture = new ResponseCapture();
        _currentResponse.Value = capture;
        try
        {
            return await base.SendRequest(request, cancellationToken);
        }
        catch (RequestException error) when (capture.Details is not null)
        {
            error.Data[ResponseDetailsKey] = capture.Details;
            throw;
        }
        finally
        {
            _currentResponse.Value = previous;
        }
    }

    public static (Exception Error, string Response) GetLogDetails(Exception error, string botToken)
    {
        if (error is not RequestException)
            return (error, string.Empty);

        // The API's description can echo the bot token in addition to the raw response fields.
        var exceptionText = error.ToString();
        var redactedText = RedactToken(exceptionText, botToken);
        return (redactedText == exceptionText ? error : new RedactedException(redactedText),
            error.Data[ResponseDetailsKey] as string ?? string.Empty);
    }

    private async ValueTask CaptureErrorResponseAsync(ITelegramBotClient _, ApiResponseEventArgs args,
        CancellationToken cancellationToken)
    {
        var response = args.ResponseMessage;
        if (_currentResponse.Value is not { } capture || response.StatusCode == HttpStatusCode.OK)
            return;

        var headers = string.Join("; ", response.Headers.NonValidated.Concat(response.Content.Headers.NonValidated)
            .Concat(response.TrailingHeaders.NonValidated)
            .Select(header => $"{header.Key}: {(SensitiveHeaders.Contains(header.Key) ? "[redacted]" : header.Value.ToString())}"));

        string body;
        try
        {
            // The SDK buffers the response before raising this event and then reads the same content.
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception error) when (error is InvalidOperationException or HttpRequestException or IOException)
        {
            body = $"[unavailable: {error.GetType().Name}]";
        }

        capture.Details = $" HTTP {(int)response.StatusCode} ({args.ApiRequestEventArgs.Request.MethodName}). " +
            $"Response headers: [{Limit(RedactToken(headers, Token))}]. Response body: {Limit(RedactToken(body, Token))}";
    }

    private static string RedactToken(string text, string token) =>
        text.Replace(token, "[redacted]", StringComparison.Ordinal)
            .Replace(Uri.EscapeDataString(token), "[redacted]", StringComparison.OrdinalIgnoreCase);

    private static string Limit(string text) => text.Length <= MaxResponseFieldLength
        ? text
        : text[..MaxResponseFieldLength] + " [truncated]";

    private sealed class ResponseCapture
    {
        public string? Details { get; set; }
    }

    private sealed class RedactedException(string text) : Exception
    {
        public override string ToString() => text;
    }
}
