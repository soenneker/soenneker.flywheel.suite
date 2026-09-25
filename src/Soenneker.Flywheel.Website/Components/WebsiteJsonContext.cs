using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Website.Components;

[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(object[]))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
internal partial class WebsiteJsonContext : JsonSerializerContext;
