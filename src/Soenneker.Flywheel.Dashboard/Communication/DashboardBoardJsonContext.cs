using System.Text.Json.Serialization;
using Soenneker.Flywheel.Communication.Responses;

namespace Soenneker.Flywheel.Dashboard.Communication;

[JsonSerializable(typeof(JobView))]
[JsonSerializable(typeof(JobHistoryPoint))]
[JsonSerializable(typeof(RecurringScheduleView))]
internal partial class DashboardBoardJsonContext : JsonSerializerContext;
