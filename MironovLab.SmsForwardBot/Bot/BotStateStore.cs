using System.Text.Json;
using System.Text.Json.Serialization;

namespace MironovLab.SmsForwardBot.Bot;

internal sealed class BotStateStore : IDisposable
{
    private const int MaxPendingMessages = 1000;
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromDays(30);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _path;
    private BotState _state;

    public BotStateStore(BotSettings settings)
    {
        _path = settings.StateFile;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _state = File.Exists(_path)
            ? JsonSerializer.Deserialize<BotState>(File.ReadAllText(_path))
                ?? throw new InvalidDataException("The bot state file does not contain a state object.")
            : new BotState();
    }

    public BotState Snapshot => Volatile.Read(ref _state);

    public async Task<bool> EnqueueAsync(DiscordNotification[] notifications, CancellationToken token)
    {
        var accepted = true;
        await UpdateAsync(state =>
        {
            var now = DateTimeOffset.UtcNow;
            var seen = state.Seen.Where(pair => now - pair.Value <= DuplicateWindow).ToDictionary();
            var pending = new List<TelegramNotification>();
            var latest = state.LatestReceived;
            foreach (var notification in notifications)
            {
                if (notification.BareIdFingerprint is { } bareId && seen.ContainsKey(bareId))
                    continue;

                if (notification.Fingerprint is { } key && !seen.TryAdd(key, now))
                    continue;

                pending.AddRange(TelegramMessages.SplitNotification(notification.Message));
                if (notification.Received is { } received && (latest is null || received.ReceivedAt >= latest.ReceivedAt))
                    latest = received;
            }

            if (pending.Count == 0)
                return state;

            if (state.Pending.Length + pending.Count > MaxPendingMessages)
            {
                accepted = false;
                return state;
            }

            return state with { LatestReceived = latest, Pending = [.. state.Pending, .. pending], Seen = seen };
        }, token);
        return accepted;
    }

    public Task RemoveFirstMessageAsync(CancellationToken token) =>
        UpdateAsync(state => state with { Pending = state.Pending[1..] }, token);

    public Task AdvanceUpdateOffsetAsync(long updateId, CancellationToken token) =>
        UpdateAsync(state => state with { UpdateOffset = updateId + 1 }, token);

    public Task ClearUpdateOffsetAsync(CancellationToken token) =>
        UpdateAsync(state => state.UpdateOffset == 0 ? state : state with { UpdateOffset = 0 }, token);

    public Task SelectLineAsync(string? line, CancellationToken token) =>
        UpdateAsync(state => state with { SelectedLine = line }, token);

    private async Task UpdateAsync(Func<BotState, BotState> update, CancellationToken token)
    {
        await _writeLock.WaitAsync(token);
        try
        {
            var next = update(_state);
            if (ReferenceEquals(next, _state))
                return;

            var temporary = _path + ".tmp";
            await using (var file = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(file, next, cancellationToken: token);
                file.Flush(flushToDisk: true);
            }

            File.Move(temporary, _path, overwrite: true);
            Volatile.Write(ref _state, next);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose() => _writeLock.Dispose();
}

internal sealed record BotState
{
    public long UpdateOffset { get; init; }
    public string? SelectedLine { get; init; }
    public ReceivedSms? LatestReceived { get; init; }
    [JsonConverter(typeof(PendingNotificationsConverter))]
    public TelegramNotification[] Pending { get; init; } = [];
    public Dictionary<string, DateTimeOffset> Seen { get; init; } = [];
}

internal sealed class PendingNotificationsConverter : JsonConverter<TelegramNotification[]>
{
    public override TelegramNotification[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("The notification queue must be an array.");

        var pending = new List<TelegramNotification>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            // Existing state files store plain text as JSON strings.
            pending.Add(reader.TokenType == JsonTokenType.String
                ? new TelegramNotification(reader.GetString()!)
                : JsonSerializer.Deserialize<TelegramNotification>(ref reader, options)
                    ?? throw new JsonException("The notification queue contains an empty entry."));
        }
        if (reader.TokenType != JsonTokenType.EndArray)
            throw new JsonException("The notification queue is incomplete.");

        return pending.ToArray();
    }

    public override void Write(Utf8JsonWriter writer, TelegramNotification[] value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var message in value)
        {
            if (message.Entities is null)
                writer.WriteStringValue(message.Text);
            else
                JsonSerializer.Serialize(writer, message, options);
        }
        writer.WriteEndArray();
    }
}