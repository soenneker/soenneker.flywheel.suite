using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Soenneker.Json.OptionsCollection;
namespace Soenneker.Flywheel.Core.Services;
/// <summary>Serializes job payloads using the configured JSON contracts.</summary>
public sealed class JobPayloadJson
{
    private readonly JsonSerializerOptions _options;
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Default job payload serialization uses reflection. Supply a generated JsonSerializerContext.")]
    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Default job payload serialization uses reflection. Supply a generated JsonSerializerContext.")]
    public JobPayloadJson() => _options = JsonOptionsCollection.WebOptions;
    public JobPayloadJson(JsonSerializerContext context) => _options = (context ?? throw new ArgumentNullException(nameof(context))).Options;
    /// <summary>Serializes the runtime payload type using its configured contract.</summary>
    public string Serialize<T>(T payload) => JsonSerializer.Serialize(payload, _options.GetTypeInfo(payload?.GetType() ?? typeof(T)));
    /// <summary>Deserializes a payload using the handler's configured contract.</summary>
    public T? Deserialize<T>(string payload) => JsonSerializer.Deserialize(payload, (JsonTypeInfo<T>)_options.GetTypeInfo(typeof(T)));
}
