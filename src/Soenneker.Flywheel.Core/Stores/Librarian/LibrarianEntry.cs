using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Core.Stores.Librarian;

internal sealed record LibrarianEntry<TKey, TValue>(
    [property: JsonPropertyName("key")] TKey Key,
    [property: JsonPropertyName("value")] TValue Value,
    [property: JsonPropertyName("order")] string? Order = null,
    [property: JsonPropertyName("scheduledOrder")] string? ScheduledOrder = null,
    [property: JsonPropertyName("marker")] bool Marker = true,
    [property: JsonPropertyName("completedAt")] long? CompletedAt = null,
    [property: JsonPropertyName("active")] bool Active = false);
