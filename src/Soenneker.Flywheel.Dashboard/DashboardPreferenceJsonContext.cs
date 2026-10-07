using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Dashboard;

[JsonSerializable(typeof(string))]
internal partial class DashboardPreferenceJsonContext : JsonSerializerContext;
