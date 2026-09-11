using Soenneker.Flywheel.Core.Stores.Abstract;
using Soenneker.Flywheel.Core.Services.Abstract;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Utils.Json;
using Soenneker.Cron.Parser;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Core.Options;

namespace Soenneker.Flywheel.Core.Services;

public sealed class JobClient(IJobStore store, IEnumerable<IJobInvoker> invokers, FlywheelOptions? options = null) : IJobClient
{
    private readonly string _applicationVersion = (options ?? new FlywheelOptions()).ApplicationVersion;
    private readonly HashSet<string> _names = invokers.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);

    public Task<string> RunOnceForCurrentVersion<T>(JobDefinition<T> job, T payload, JobPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        EnqueueRequest request = Request(job, payload, policy, TimeSpan.Zero, null);
        if (store is not IVersionedJobStore versioned)
            throw new NotSupportedException("The job store does not support version-restricted jobs.");
        return versioned.RunOnceForCurrentVersion(request, _applicationVersion, cancellationToken);
    }

    private EnqueueRequest Request<T>(JobDefinition<T> job, T payload, JobPolicy? policy, TimeSpan delay, string? key)
    {
        if (!_names.Contains(job.Name))
            throw new InvalidOperationException($"Unregistered job: {job.Name}");
        policy ??= new JobPolicy();
        policy.Validate();
        return new EnqueueRequest(job.Name, JsonUtil.Serialize(payload) ?? "null", policy, delay, key, job.Description);
    }

    public Task<string> Enqueue<T>(JobDefinition<T> job, T payload, JobPolicy? policy = null, TimeSpan? delay = null,
        string? idempotencyKey = null, CancellationToken cancellationToken = default) =>
        store.Enqueue(Request(job, payload, policy, delay ?? TimeSpan.Zero, idempotencyKey), cancellationToken);

    public Task<string> Schedule<T>(JobDefinition<T> job, T payload, DateTimeOffset scheduledAt,
        JobPolicy? policy = null, string? idempotencyKey = null, CancellationToken cancellationToken = default)
    {
        TimeSpan delay = scheduledAt - DateTimeOffset.UtcNow;
        return Enqueue(job, payload, policy, delay > TimeSpan.Zero ? delay : TimeSpan.Zero, idempotencyKey,
            cancellationToken);
    }

    public Task ConfigureMethod<T>(JobDefinition<T> job, MethodPolicy policy,
        CancellationToken cancellationToken = default)
    {
        if (!_names.Contains(job.Name))
            throw new InvalidOperationException($"Unregistered job: {job.Name}");
        policy.Validate();
        if (store is not IMethodPolicyStore policies)
            throw new NotSupportedException("The job store does not support method policies.");
        return policies.ConfigureMethod(job.Name, policy, cancellationToken);
    }

    public Task<bool> Recurring<T>(string id, JobDefinition<T> job, T payload, TimeSpan interval,
        JobPolicy? policy = null, CancellationToken cancellationToken = default) => store.AddRecurring(id,
        Request(job, payload, policy, TimeSpan.Zero, null), interval, cancellationToken);

    public Task<bool> Schedule<T>(string id, JobDefinition<T> job, T payload, string expression, string timeZoneId = "UTC",
        JobPolicy? policy = null, bool includeSeconds = false, CancellationToken cancellationToken = default)
    {
        EnqueueRequest request = Request(job, payload, policy, TimeSpan.Zero, null);
        _ = CronParser.Parse(expression, timeZoneId, includeSeconds);
        if (store is not ICronJobStore schedules)
            throw new NotSupportedException("The job store does not support cron schedules.");
        return schedules.AddCron(id, request, expression, timeZoneId, includeSeconds, cancellationToken);
    }

    public Task<IReadOnlyList<string>> Chain(IReadOnlyList<JobStep> steps, string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(steps));
        var requests = new EnqueueRequest[steps.Count];
        for (var i = 0; i < requests.Length; i++)
        {
            ArgumentNullException.ThrowIfNull(steps[i]);
            EnqueueRequest request = steps[i].Request;
            if (!_names.Contains(request.Name))
                throw new InvalidOperationException($"Unregistered job: {request.Name}");
            request.Policy.Validate();
            requests[i] = request;
        }

        if (store is not IJobChainStore chains)
            throw new NotSupportedException("The job store does not support job chains.");
        return chains.EnqueueChain(requests, idempotencyKey, cancellationToken);
    }
}
