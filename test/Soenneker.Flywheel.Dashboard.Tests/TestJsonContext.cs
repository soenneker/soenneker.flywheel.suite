using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Dashboard.Tests;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(string))]
internal partial class TestJsonContext : JsonSerializerContext
{
}
