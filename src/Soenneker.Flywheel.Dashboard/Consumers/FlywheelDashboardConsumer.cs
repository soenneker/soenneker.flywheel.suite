using Microsoft.Extensions.Logging;
using Soenneker.Blazor.Consumers.Core;
using Soenneker.Dtos.Results.Operation;
using Soenneker.Extensions.HttpResponseMessage;
using Soenneker.Flywheel.Dashboard.Communication.Abstract;
using Soenneker.Flywheel.Dashboard.Consumers.Abstract;
using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Flywheel.Communication.Requests;

namespace Soenneker.Flywheel.Dashboard.Consumers;

public sealed class FlywheelDashboardConsumer(IFlywheelApiClient apiClient, ILogger<FlywheelDashboardConsumer> logger)
    : CoreConsumer(apiClient, logger, "flywheel"), IFlywheelDashboardConsumer
{
    public ValueTask<OperationResult<SearchResult>> Search(string query = "", int offset = 0, int count = 50, DateTimeOffset? startAt = null, DateTimeOffset? endAt = null, CancellationToken cancellationToken = default) =>
        Read<SearchResult>($"jobs/search?q={Uri.EscapeDataString(query)}&offset={offset}&count={count}" + Range(startAt, endAt), cancellationToken);
    public ValueTask<OperationResult<HistoryOptions>> GetHistoryOptions(CancellationToken cancellationToken = default) => Read<HistoryOptions>("jobs/history/options", cancellationToken);
    public ValueTask<OperationResult<List<JobHistoryPoint>>> GetHistory(DateTimeOffset startAt, DateTimeOffset endAt, CancellationToken cancellationToken = default) => Read<List<JobHistoryPoint>>("jobs/history?" + Range(startAt, endAt).TrimStart('&'), cancellationToken);
    public ValueTask<OperationResult<ScheduleView>> GetSchedules(CancellationToken cancellationToken = default) => Read<ScheduleView>("jobs/schedules", cancellationToken);
    public ValueTask<OperationResult<JobView>> GetJob(string id, CancellationToken cancellationToken = default) => Read<JobView>($"jobs/{Uri.EscapeDataString(id)}", cancellationToken);
    public ValueTask<OperationResult<List<ServerView>>> GetServers(CancellationToken cancellationToken = default) => Read<List<ServerView>>("servers", cancellationToken);
    public ValueTask<OperationResult<ServerView>> GetServer(string id, CancellationToken cancellationToken = default) => Read<ServerView>($"servers/{Uri.EscapeDataString(id)}", cancellationToken);
    public ValueTask<OperationResult<object>> Login(string username, string password, CancellationToken cancellationToken = default) => Write<object>("login", new LoginRequest(username, password), cancellationToken);
    public ValueTask<OperationResult<object>> Logout(CancellationToken cancellationToken = default) => Write<object>("logout", null, cancellationToken);
    public ValueTask<OperationResult<object>> CancelJob(string id, CancellationToken cancellationToken = default) => Write<object>($"jobs/{Uri.EscapeDataString(id)}/cancel", null, cancellationToken);
    public ValueTask<OperationResult<StartedJob>> RunRecurring(string id, CancellationToken cancellationToken = default) => Write<StartedJob>($"jobs/schedules/{Uri.EscapeDataString(id)}/run", null, cancellationToken);

    private async ValueTask<OperationResult<T>> Read<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await ApiClient.Get($"{PrefixUri}/{path}", cancellationToken: cancellationToken);
        return await Convert<T>(response, cancellationToken);
    }

    private async ValueTask<OperationResult<T>> Write<T>(string path, object? body, CancellationToken cancellationToken)
    {
        using var response = await ApiClient.Post($"{PrefixUri}/{path}", body, logResponse: false, cancellationToken: cancellationToken);
        return await Convert<T>(response, cancellationToken);
    }

    private static async ValueTask<OperationResult<T>> Convert<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var result = await response.ToResult<T>(cancellationToken: cancellationToken);
        // Cookie challenges and unsupported store operations can have no response body.
        return !response.IsSuccessStatusCode && result.Succeeded
            ? OperationResult.Fail<T>("Dashboard request failed", response.ReasonPhrase ?? "The operation could not be completed.", response.StatusCode)
            : result;
    }

    private static string Range(DateTimeOffset? startAt, DateTimeOffset? endAt) => startAt is { } start && endAt is { } end
        ? $"&startAt={Uri.EscapeDataString(start.ToString("O"))}&endAt={Uri.EscapeDataString(end.ToString("O"))}" : "";
}
