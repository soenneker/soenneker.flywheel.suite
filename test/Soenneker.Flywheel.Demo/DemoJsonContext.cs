using System.Text.Json;
using System.Text.Json.Serialization;
using Soenneker.Flywheel.Demo.Dtos;
using Soenneker.Flywheel.Demo.Requests;

namespace Soenneker.Flywheel.Demo;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(DeliveryRequest))]
[JsonSerializable(typeof(FailureRequest))]
[JsonSerializable(typeof(PulseRequest))]
[JsonSerializable(typeof(ReportRequest))]
[JsonSerializable(typeof(WelcomeEmail))]
[JsonSerializable(typeof(WorkRequest))]
[JsonSerializable(typeof(OrderPlaced))]
internal partial class DemoJsonContext : JsonSerializerContext
{
}
