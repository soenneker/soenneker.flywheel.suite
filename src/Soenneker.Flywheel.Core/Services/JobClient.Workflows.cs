using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Flywheel.Core.Stores.Abstract;

namespace Soenneker.Flywheel.Core.Services;

public sealed partial class JobClient
{
    private IJobWorkflowStore Workflows => store as IJobWorkflowStore ?? throw new NotSupportedException("The store does not support workflows.");
    private IJobDefinitionStore Definitions => store as IJobDefinitionStore ?? throw new NotSupportedException("The store does not support definition updates.");

    public Task<string> ContinueWith<T>(string parentId, JobDefinition<T> job, T payload,
        ContinuationCondition condition = ContinuationCondition.OnSuccess, JobPolicy? policy = null, TimeSpan? delay = null,
        string? idempotencyKey = null, CancellationToken cancellationToken = default) =>
        Workflows.ContinueWith(parentId, Request(job, payload, policy, delay ?? TimeSpan.Zero, null), condition, idempotencyKey, cancellationToken);

    public Task<WorkflowSubmission> Batch(IReadOnlyList<JobStep> jobs, JobStep? continuation = null,
        ContinuationCondition condition = ContinuationCondition.OnSuccess, string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        if (jobs.Count is < 1 or > 999) throw new ArgumentOutOfRangeException(nameof(jobs));
        EnqueueRequest Validate(JobStep step)
        {
            ArgumentNullException.ThrowIfNull(step);
            if (!_names.Contains(step.Request.Name)) throw new InvalidOperationException($"Unregistered job: {step.Request.Name}");
            step.Request.Policy.Validate();
            return step.Request;
        }
        return Workflows.EnqueueBatch(jobs.Select(Validate).ToArray(), continuation is null ? null : Validate(continuation),
            condition, idempotencyKey, cancellationToken);
    }

    public Task<bool> UpdateRecurring<T>(string id, JobDefinition<T> job, T payload, TimeSpan interval, JobPolicy? policy = null,
        bool enabled = true, long? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        var request = Request(job, payload, policy, TimeSpan.Zero, null);
        return Definitions.UpdateRecurring(id, new(request.Name, request.Payload, request.Policy, (long)interval.TotalMilliseconds,
            Enabled: enabled, ExpectedRevision: expectedRevision, Description: request.Description), cancellationToken);
    }

    public Task<bool> UpdateSchedule<T>(string id, JobDefinition<T> job, T payload, string expression, string timeZoneId = "UTC",
        JobPolicy? policy = null, bool includeSeconds = false, bool enabled = true, long? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        var request = Request(job, payload, policy, TimeSpan.Zero, null);
        return Definitions.UpdateRecurring(id, new(request.Name, request.Payload, request.Policy, Cron: expression,
            TimeZoneId: timeZoneId, IncludeSeconds: includeSeconds, Enabled: enabled,
            ExpectedRevision: expectedRevision, Description: request.Description), cancellationToken);
    }

    public Task<bool> SetRecurringEnabled(string id, bool enabled, CancellationToken cancellationToken = default) =>
        Definitions.SetRecurringEnabled(id, enabled, cancellationToken: cancellationToken);
    public Task<bool> DeleteRecurring(string id, CancellationToken cancellationToken = default) =>
        Definitions.DeleteRecurring(id, cancellationToken: cancellationToken);
    public Task<bool> UpdateManual<T>(string id, JobDefinition<T> job, T payload, JobPolicy? policy = null, CancellationToken cancellationToken = default) =>
        Definitions.UpdateManual(id, Request(job, payload, policy, TimeSpan.Zero, null), cancellationToken);
    public Task<bool> DeleteManual(string id, CancellationToken cancellationToken = default) => Definitions.DeleteManual(id, cancellationToken);
}
