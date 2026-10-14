using Soenneker.Extensions.ValueTask;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Flywheel.Communication.Responses;

namespace Soenneker.Flywheel.Core.Stores.Librarian;

public abstract partial class LibrarianJobStore
{
    private readonly LibrarianTable<string, ManualJob> _manualJobs;

    public Task<bool> AddManual(string id, EnqueueRequest request, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        if (request.IdempotencyKey is not null || request.Delay != TimeSpan.Zero)
            throw new ArgumentException("Manual definitions cannot have a delay or idempotency key.");
        JobRecord job = Create(request);
        return Mutate(cancellationToken, async _ => await _manualJobs.TryAdd(id, new ManualJob(job)).NoSync());
    }

    public Task<string?> RunManual(string id, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        return Mutate<string?>(cancellationToken, async now =>
        {
            if (await _manualJobs.Get(id) is not { } definition) return null;
            string executionId = await Insert(definition.Job with { Id = Guid.NewGuid().ToString("N"), ManualJobId = id }, 0, now);
            await _manualJobs.Set(id, definition with { LastExecutionId = executionId, LastExecutionStatus = null }).NoSync();
            return executionId;
        });
    }

    public Task<IReadOnlyList<ManualJobView>> ListManual(int count = 50, CancellationToken cancellationToken = default)
    {
        ValidatePage(0, count);
        return Mutate<IReadOnlyList<ManualJobView>>(cancellationToken, async now =>
        {
            var definitions = await _manualJobs.Range<ManualJobSummary>("key", minimum: "", take: count);
            string[] ids = definitions.Where(pair => pair.Value.LastExecutionId is not null).Select(pair => pair.Value.LastExecutionId!).ToArray();
            RecurringJobSummary?[] executions = await _jobs.GetMany<RecurringJobSummary>(ids);
            int executionIndex = 0;
            var result = new List<ManualJobView>(definitions.Count);
            foreach (var pair in definitions)
            {
                ManualJobSummary definition = pair.Value;
                RecurringJobSummary? execution = definition.LastExecutionId is null ? null : executions[executionIndex++];
                result.Add(new ManualJobView(pair.Key, definition.Job.Name, definition.Job.Description,
                    definition.LastExecutionId, execution?.DisplayState(now) ?? definition.LastExecutionStatus));
            }
            return result;
        });
    }
}
