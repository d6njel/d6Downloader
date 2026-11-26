using System.Text.Json;
using System.Text.Json.Serialization;

namespace D6Downloader.Remote;

/// <summary>Klartext im Umschlag. Ein Typ für alle Nachrichten, ungenutzte Felder fehlen.</summary>
public sealed class RemoteMessage
{
    /// <summary>Typ, siehe <see cref="MessageType"/>.</summary>
    public string? T { get; set; }
    public string? Id { get; set; }
    /// <summary>Unix-Zeit des Absenders in Millisekunden.</summary>
    public long Ts { get; set; }

    public string? Name { get; set; }
    public string? Text { get; set; }
    public string? Src { get; set; }
    public int? Part { get; set; }
    public int? Parts { get; set; }
    public string? Grp { get; set; }
    public string? Pc { get; set; }
    public string? Mode { get; set; }
    public string? Ref { get; set; }
    public string? State { get; set; }
    public int? N { get; set; }
    public int? Skipped { get; set; }
    public string? Err { get; set; }
    public string? Ver { get; set; }

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, RemoteJsonContext.Default.RemoteMessage);

    /// <summary>Null bei allem, was keine brauchbare Nachricht ist.</summary>
    public static RemoteMessage? Parse(byte[] json)
    {
        try
        {
            var message = JsonSerializer.Deserialize(json, RemoteJsonContext.Default.RemoteMessage);
            return message is { T: { Length: > 0 }, Id: { Length: > 0 and <= 64 } } ? message : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public static class MessageType
{
    public const string Hello = "hello", Links = "links", Ping = "ping", Bye = "bye";
    public const string HelloAck = "hello-ack", Receipt = "receipt", Pong = "pong";
}

public static class ReceiptState
{
    public const string PendingConfirmation = "pending-confirmation", Queued = "queued", Rejected = "rejected", Partial = "partial";
}

public static class ReceiptError
{
    public const string NoLink = "no-link", AllDuplicate = "all-duplicate", BlockedTarget = "blocked-target",
        TooLarge = "too-large", RateLimited = "rate-limited",
        /// <summary>Alles durch die Regeln „Automatisch abwählen“ aussortiert.</summary>
        Filtered = "filtered";
}

public static class ReceiveMode
{
    public const string Dialog = "dialog", Auto = "auto";
}

/// <summary>Was mit einer Link-Nachricht geschah, daraus wird die Quittung.</summary>
public sealed record RemoteOutcome(string State, int N, int Skipped = 0, string? Err = null);

[JsonSerializable(typeof(RemoteMessage))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class RemoteJsonContext : JsonSerializerContext
{
}
