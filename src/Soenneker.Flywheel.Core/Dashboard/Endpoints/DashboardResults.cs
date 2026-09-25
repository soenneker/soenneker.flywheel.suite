using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Soenneker.Flywheel.Communication;

namespace Soenneker.Flywheel.Core.Dashboard.Endpoints;

internal static class DashboardResults
{
    internal static JsonHttpResult<T> Json<T>(T value) => TypedResults.Json(value, FlywheelJsonContext.Get<T>());
}
