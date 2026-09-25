// Enum-value converters in referenced assemblies are file-local; explicit metadata below handles them.
using Soenneker.Cron.Parser;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Logging.Dtos;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Flywheel.Communication.Responses;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json.Serialization;
using System.Text.Json;
using System;

namespace Soenneker.Flywheel.Core.Stores.Librarian;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, ReadCommentHandling = JsonCommentHandling.Skip, UseStringEnumConverter = true)]
[JsonSerializable(typeof(DebounceEntry))]
[JsonSerializable(typeof(DispatchCandidate))]
[JsonSerializable(typeof(IdleSample))]
[JsonSerializable(typeof(JobHistoryPoint))]
[JsonSerializable(typeof(JobRecord))]
[JsonSerializable(typeof(LibrarianEntry<VersionKey, string>))]
[JsonSerializable(typeof(LibrarianEntry<long, JobHistoryPoint>))]
[JsonSerializable(typeof(LibrarianEntry<long, Sample>))]
[JsonSerializable(typeof(LibrarianEntry<string, DebounceEntry>))]
[JsonSerializable(typeof(LibrarianEntry<string, DispatchCandidate>))]
[JsonSerializable(typeof(LibrarianEntry<string, IdleSample>))]
[JsonSerializable(typeof(LibrarianEntry<string, JobRecord>))]
[JsonSerializable(typeof(LibrarianEntry<string, LogBuffer>))]
[JsonSerializable(typeof(LibrarianEntry<string, MethodPolicy>))]
[JsonSerializable(typeof(LibrarianEntry<string, Node>))]
[JsonSerializable(typeof(LibrarianEntry<string, Rate>))]
[JsonSerializable(typeof(LibrarianEntry<string, RunningEntry>))]
[JsonSerializable(typeof(LibrarianEntry<string, Schedule>))]
[JsonSerializable(typeof(LibrarianEntry<string, long>))]
[JsonSerializable(typeof(LibrarianEntry<string, string>))]
[JsonSerializable(typeof(LibrarianEntry<string, string[]>))]
[JsonSerializable(typeof(LogBuffer))]
[JsonSerializable(typeof(MethodPolicy))]
[JsonSerializable(typeof(Node))]
[JsonSerializable(typeof(Rate))]
[JsonSerializable(typeof(RunningEntry))]
[JsonSerializable(typeof(Sample))]
[JsonSerializable(typeof(Schedule))]
[JsonSerializable(typeof(StoreRevision))]
[JsonSerializable(typeof(VersionKey))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
internal partial class LibraryJsonContext : JsonSerializerContext
{
    internal static JsonTypeInfo<T> Get<T>() =>
        (JsonTypeInfo<T>)MetadataOptionsHolder.Value.GetTypeInfo(typeof(T));

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
