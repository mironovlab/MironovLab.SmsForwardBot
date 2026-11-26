using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MironovLab.SmsForwardBot.Bot;

internal sealed class BridgeClient(BotSettings settings) : IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        BaseAddress = settings.WebUiUrl,
        Timeout = TimeSpan.FromSeconds(20)
    };
    private bool _signedIn;

    public async Task<BridgeOverview> GetOverviewAsync(CancellationToken token)
    {
        using var response = await RequestAsync(HttpMethod.Get, "api/overview", null, token);
        await EnsureSuccessAsync(response, token);
        return await ReadAsync<BridgeOverview>(response, token);
    }

    public async Task<ReceivedSms?> GetLatestReceivedSmsAsync(CancellationToken token)
    {
        using var response = await RequestAsync(HttpMethod.Get, "api/sms?page=1&per_page=1", null, token);
        await EnsureSuccessAsync(response, token);
        var page = await ReadAsync<BridgeInbox>(response, token);
        return page.Items.FirstOrDefault();
    }

    public async Task<QueuedSms> SendAsync(string line, string to, string text, CancellationToken token)
    {
        var path = $"api/lines/{Uri.EscapeDataString(line)}/sms";
        using var response = await RequestAsync(HttpMethod.Post, path, new { to, text }, token);
        await EnsureSuccessAsync(response, token);
        if (response.StatusCode != HttpStatusCode.Accepted)
            throw new JsonException("Expected the bridge WebUI to confirm that the SMS was queued.");

        var queued = await ReadAsync<QueuedSms>(response, token);
        if (queued.Id <= 0 || queued.Parts <= 0)
            throw new JsonException("The bridge WebUI confirmation did not contain a valid outbox ID and part count.");

        return queued;
    }

    private async Task<HttpResponseMessage> RequestAsync(HttpMethod method, string path, object? body, CancellationToken token)
    {
        if (!_signedIn)
            await LoginAsync(token);

        var response = await SendRequestAsync(method, path, body, token);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        // WebUI rejects expired sessions before invoking the SMS handler.
        response.Dispose();
        await LoginAsync(token);
        return await SendRequestAsync(method, path, body, token);
    }

    private async Task LoginAsync(CancellationToken token)
    {
        _signedIn = false;
        using var response = await SendRequestAsync(HttpMethod.Post, "api/login", new { password = settings.WebUiPassword }, token);
        await EnsureSuccessAsync(response, token);
        if (response.StatusCode != HttpStatusCode.NoContent)
            throw new JsonException("Expected the bridge WebUI to confirm sign-in.");

        _signedIn = true;
    }

    private async Task<HttpResponseMessage> SendRequestAsync(HttpMethod method, string path, object? body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
            request.Content = JsonContent.Create(body);

        return await _http.SendAsync(request, token);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.IsSuccessStatusCode)
            return;

        BridgeError? error = null;
        JsonException? parseError = null;
        if (response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            try
            {
                error = await response.Content.ReadFromJsonAsync<BridgeError>(token);
            }
            catch (JsonException exception)
            {
                // Preserve the HTTP status when a proxy returns a different error format.
                parseError = exception;
            }
        }

        throw new BridgeException(response.StatusCode, error?.Error, error?.Detail, parseError);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken token) =>
        await response.Content.ReadFromJsonAsync<T>(token) ?? throw new JsonException("The bridge WebUI returned an empty response.");

    public void Dispose() => _http.Dispose();

    private sealed record BridgeInbox(ReceivedSms[] Items);
    private sealed record BridgeError(string? Error, string? Detail);
}

internal sealed class BridgeException(HttpStatusCode status, string? error, string? detail, Exception? innerException)
    : Exception(null, innerException)
{
    public HttpStatusCode Status { get; } = status;
    public string? Error { get; } = error;
    public string? Detail { get; } = detail;
}

internal sealed record BridgeOverview(BridgeInfo Bridge, BridgeLine[] Lines);
internal sealed record BridgeInfo([property: JsonPropertyName("sms_enabled")] bool? SmsEnabled);
internal sealed record BridgeLine(string Id, string Transport, string? Msisdn, string? Carrier, string? Health)
{
    public bool SupportsSms => Transport is "vowifi" or "volte";
}

internal sealed record QueuedSms(long Id, int Parts);
internal sealed record ReceivedSms(
    [property: JsonPropertyName("module_id")] string ModuleId,
    [property: JsonPropertyName("received_at")] DateTimeOffset ReceivedAt,
    [property: JsonPropertyName("sender")] string? Sender = null);