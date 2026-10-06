using System.Text.Json.Serialization;
namespace Soenneker.Flywheel.Core.Tests.Jobs;
[JsonSerializable(typeof(JobPayloadTestModel))]
internal partial class JobPayloadTestJsonContext : JsonSerializerContext;
