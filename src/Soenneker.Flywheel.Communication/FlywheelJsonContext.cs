using System.Collections.Generic;
// Explicit enum metadata keeps generated contracts compatible with numeric persisted values.
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Flywheel.Communication.Responses;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json.Serialization;
using System.Text.Json;
using System;

namespace Soenneker.Flywheel.Communication;

/// <summary>Generated JSON contracts shared by Flywheel HTTP and live dashboard messages.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, ReadCommentHandling = JsonCommentHandling.Skip, UseStringEnumConverter = true)]
[JsonSerializable(typeof(LiveBoard))]
[JsonSerializable(typeof(LiveJob))]
[JsonSerializable(typeof(LiveLogs))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(DateTimeOffset?))]
[JsonSerializable(typeof(List<JobView>))]
[JsonSerializable(typeof(IReadOnlyList<ServerView>))]
[JsonSerializable(typeof(List<LogEntry>))]
[JsonSerializable(typeof(Csrf))]
[JsonSerializable(typeof(EnqueueRequest))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(DashboardUser))]
[JsonSerializable(typeof(HistoryOptions))]
[JsonSerializable(typeof(JobView))]
[JsonSerializable(typeof(List<JobHistoryPoint>))]
[JsonSerializable(typeof(IReadOnlyList<JobHistoryPoint>))]
[JsonSerializable(typeof(List<ServerView>))]
[JsonSerializable(typeof(ScheduleView))]
[JsonSerializable(typeof(SearchResult))]
[JsonSerializable(typeof(ServerView))]
[JsonSerializable(typeof(StartedJob))]
public partial class FlywheelJsonContext : JsonSerializerContext
{
    /// <summary>Gets generated metadata with the Flywheel wire-format converters.</summary>
    public static JsonTypeInfo<T> Get<T>() =>
        (JsonTypeInfo<T>)MetadataOptionsHolder.Value.GetTypeInfo(typeof(T));

    /// <summary>Adds generated Flywheel contracts and numeric enum converters to serializer options.</summary>
    public static void Configure(JsonSerializerOptions options)
    {
        options.Converters.Add(new JobPriorityMetadataConverter());
        options.Converters.Add(new JobStateMetadataConverter());
        options.TypeInfoResolverChain.Insert(0, new MetadataResolver());
    }

    private static class MetadataOptionsHolder
    {
        internal static readonly JsonSerializerOptions Value = CreateMetadataOptions();
    }

    private static JsonSerializerOptions CreateMetadataOptions()
    {
        var options = new JsonSerializerOptions(Default.Options) { TypeInfoResolver = new MetadataResolver() };
        options.Converters.Add(new JobPriorityMetadataConverter());
        options.Converters.Add(new JobStateMetadataConverter());
        options.MakeReadOnly();
        return options;
    }

    private sealed class MetadataResolver : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            if (type == typeof(Soenneker.Flywheel.Communication.Enums.JobPriority))
                return JsonMetadataServices.CreateValueInfo<Soenneker.Flywheel.Communication.Enums.JobPriority>(options, new JobPriorityMetadataConverter());
            if (type == typeof(Soenneker.Flywheel.Communication.Enums.JobState))
                return JsonMetadataServices.CreateValueInfo<Soenneker.Flywheel.Communication.Enums.JobState>(options, new JobStateMetadataConverter());
            return ((IJsonTypeInfoResolver)Default).GetTypeInfo(type, options);
        }
    }

}

internal sealed class JobPriorityMetadataConverter : JsonConverter<Soenneker.Flywheel.Communication.Enums.JobPriority>
{
    public override Soenneker.Flywheel.Communication.Enums.JobPriority Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int rawValue) && Soenneker.Flywheel.Communication.Enums.JobPriority.TryFromValue(rawValue, out var value) ? value : throw new JsonException("Unknown JobPriority value.");

    public override void Write(Utf8JsonWriter writer, Soenneker.Flywheel.Communication.Enums.JobPriority value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value.Value);
}

internal sealed class JobStateMetadataConverter : JsonConverter<Soenneker.Flywheel.Communication.Enums.JobState>
{
    public override Soenneker.Flywheel.Communication.Enums.JobState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int rawValue) && Soenneker.Flywheel.Communication.Enums.JobState.TryFromValue(rawValue, out var value) ? value : throw new JsonException("Unknown JobState value.");

    public override void Write(Utf8JsonWriter writer, Soenneker.Flywheel.Communication.Enums.JobState value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value.Value);
}
