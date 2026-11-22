namespace MironovLab.SmsForwardBot.Bot;

public interface IDiscordWebhook
{
    bool MatchesSecret(string secret);

    Task<bool> EnqueueAsync(IReadOnlyList<DiscordEmbed?> embeds, CancellationToken token);
}

public sealed record DiscordPayload(DiscordEmbed?[]? Embeds);

public sealed record DiscordEmbed(string? Title, string? Description, string? Timestamp, DiscordField?[]? Fields, DiscordFooter? Footer);

public sealed record DiscordField(string? Name, string? Value);

public sealed record DiscordFooter(string? Text);
