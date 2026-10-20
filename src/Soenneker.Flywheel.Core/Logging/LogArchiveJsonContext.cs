using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Core.Logging;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LogArchiveFile))]
[JsonSerializable(typeof(LogArchiveEntry))]
internal partial class LogArchiveJsonContext : JsonSerializerContext;
