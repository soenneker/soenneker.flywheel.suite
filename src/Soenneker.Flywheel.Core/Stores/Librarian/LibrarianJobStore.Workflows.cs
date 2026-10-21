using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Flywheel.Communication.Responses;

namespace Soenneker.Flywheel.Core.Stores.Librarian;

public abstract partial class LibrarianJobStore
{
    private readonly LibrarianTable<string, WorkflowRecord> _workflows = new("workflows");
    private readonly LibrarianTable<string, string[]> _dependents = new("dependents");
    private readonly LibrarianTable<string, string[]> _workflowKeys = new("workflowKeys");
    private const int MaximumWorkflowJobs = 1000;

    public Task<string> ContinueWith(string parentId, EnqueueRequest request,
        ContinuationCondition condition = ContinuationCondition.OnSuccess, string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        ValidateId(parentId);
        if (!Enum.IsDefined(condition) || idempotencyKey?.Length > 256 || request.IdempotencyKey is not null)
            throw new ArgumentException("Invalid continuation condition or idempotency key.");
        JobRecord child = Create(request);
        string? key = idempotencyKey is null ? null : "continuation:" + idempotencyKey;
        return Mutate(cancellationToken, async now =>
        {
            if (key is not null && await _workflowKeys.Get(key) is { } existing) return existing[0];
            JobRecord parent = await _jobs.Get(parentId) ?? throw new KeyNotFoundException("Predecessor is not retained.");
            WorkflowRecord workflow = await EnsureWorkflow(parent);
            if (workflow.JobIds.Length >= MaximumWorkflowJobs) throw new InvalidOperationException("A workflow supports at most 1000 jobs.");
            await _workflows.Set(workflow.Id, workflow with { JobIds = [..workflow.JobIds, child.Id],
                SubmissionKeys = key is null ? workflow.SubmissionKeys : [..workflow.SubmissionKeys, key] });
            await Insert(child with { WorkflowId = workflow.Id, ParentJobId = parentId, PredecessorIds = [parentId],
                Condition = condition, State = JobState.Waiting, DelayAfterParent = (long)request.Delay.TotalMilliseconds }, 0, now);
            await _dependents.Set(parentId, [..await _dependents.Get(parentId) ?? [], child.Id]);
            if (key is not null) await _workflowKeys.Set(key, [child.Id]);
            await AdvanceWorkflow(parent, now);
            return child.Id;
        });
    }

    private async Task<WorkflowRecord> EnsureWorkflow(JobRecord parent, bool persist = true)
    {
        if (parent.WorkflowId is { } id)
            return await _workflows.Get(id) ?? throw new InvalidDataException("Workflow metadata is missing.");
        // Adopt retained legacy chains together so their graph and retention remain coherent.
        var jobs = new List<JobRecord>();
        var current = parent;
        var seen = new HashSet<string>();
        while (current.ParentJobId is { } previous && await _jobs.Get(previous) is { } prior)
        {
            if (!seen.Add(current.Id) || seen.Count >= MaximumWorkflowJobs) throw new InvalidDataException("Invalid legacy chain.");
            current = prior;
        }
        seen.Clear();
        while (true)
        {
            if (!seen.Add(current.Id) || seen.Count > MaximumWorkflowJobs) throw new InvalidDataException("Invalid legacy chain.");
            jobs.Add(current);
            if (current.NextJobId is not { } next || await _jobs.Get(next) is not { } successor) break;
            current = successor;
        }
        string workflowId = jobs[0].Id;
        if (!persist) return new(workflowId, jobs.Select(j => j.Id).ToArray(), []);
        foreach (var job in jobs)
        {
            await _jobs.Set(job.Id, job with { WorkflowId = workflowId,
                PredecessorIds = job.ParentJobId is { } priorId && seen.Contains(priorId) ? [priorId] : [] });
            if (job.NextJobId is { } nextId && seen.Contains(nextId))
                await _dependents.Set(job.Id, [..await _dependents.Get(job.Id) ?? [], nextId]);
        }
        var workflow = new WorkflowRecord(workflowId, jobs.Select(j => j.Id).ToArray(), []);
        await _workflows.Set(workflowId, workflow);
        return workflow;
    }

    public Task<WorkflowSubmission> EnqueueBatch(IReadOnlyList<EnqueueRequest> jobs, EnqueueRequest? continuation = null,
        ContinuationCondition condition = ContinuationCondition.OnSuccess, string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        if (jobs.Count is < 1 or > 999 || condition is not (ContinuationCondition.OnSuccess or ContinuationCondition.OnCompleted) ||
            idempotencyKey?.Length > 256 || jobs.Any(j => j.IdempotencyKey is not null) || continuation?.IdempotencyKey is not null)
            throw new ArgumentException("A batch needs 1–999 jobs, a success/completed condition, and no step idempotency keys.");
        var requests = jobs.ToArray();
        var records = requests.Select(Create).ToArray();
        JobRecord? final = continuation is null ? null : Create(continuation);
        string workflowId = Guid.NewGuid().ToString("N");
        string[] ids = records.Select(j => j.Id).Concat(final is null ? [] : new[] { final.Id }).ToArray();
        string? key = idempotencyKey is null ? null : "batch:" + idempotencyKey;
        return Mutate(cancellationToken, async now =>
        {
            if (key is not null && await _workflowKeys.Get(key) is { } existing)
                return new WorkflowSubmission(existing[0], existing.Skip(2).ToArray(), existing[1].Length == 0 ? null : existing[1]);
            await _workflows.Set(workflowId, new(workflowId, ids, key is null ? [] : [key]));
            for (int i = 0; i < records.Length; i++)
            {
                await Insert(records[i] with { WorkflowId = workflowId, State = JobState.Scheduled }, (long)requests[i].Delay.TotalMilliseconds, now);
                if (final is not null) await _dependents.Set(records[i].Id, [final.Id]);
            }
            if (final is not null)
                await Insert(final with { WorkflowId = workflowId, State = JobState.Waiting,
                    PredecessorIds = records.Select(j => j.Id).ToArray(), Condition = condition,
                    DelayAfterParent = (long)continuation!.Delay.TotalMilliseconds }, 0, now);
            if (key is not null) await _workflowKeys.Set(key, [workflowId, final?.Id ?? "", ..ids]);
            return new WorkflowSubmission(workflowId, ids.ToArray(), final?.Id);
        });
    }

    private async Task AdvanceWorkflow(JobRecord parent, long now)
    {
        if (!Terminal(parent.State)) return;
        var queue = new Queue<string>();
        queue.Enqueue(parent.Id);
        while (queue.TryDequeue(out string? id))
        {
            foreach (string childId in await _dependents.Get(id) ?? [])
            {
                if (await _jobs.Get(childId) is not { State: var state } child || state != JobState.Waiting) continue;
                JobRecord?[] predecessors = await _jobs.GetMany<JobRecord>(child.PredecessorIds);
                if (predecessors.Any(p => p is null)) throw new InvalidDataException("Workflow predecessor is missing.");
                if (predecessors.Any(p => !Terminal(p!.State))) continue;
                bool matched = !predecessors.Any(p => p!.DependencySkipped) && child.Condition switch
                {
                    ContinuationCondition.OnSuccess => predecessors.All(p => p!.State == JobState.Succeeded),
                    ContinuationCondition.OnFailure => predecessors.Any(p => p!.State == JobState.DeadLettered),
                    ContinuationCondition.OnCancelled => predecessors.Any(p => p!.State == JobState.Cancelled),
                    ContinuationCondition.OnCompleted => true,
                    _ => false
                };
                if (matched)
                    await Save(child with { State = JobState.Scheduled, DueAt = checked(now + child.DelayAfterParent) }, now);
                else
                {
                    var skipped = child with { State = JobState.Cancelled, DependencySkipped = true,
                        CancelRequested = true, Version = child.Version + 1, Error = "Continuation condition was not met." };
                    await Save(skipped, now);
                    queue.Enqueue(child.Id);
                }
            }
        }
    }

    public Task<WorkflowView?> GetWorkflow(string jobId, CancellationToken cancellationToken = default)
    {
        ValidateId(jobId);
        return Mutate<WorkflowView?>(cancellationToken, async now =>
        {
            if (await _jobs.Get(jobId) is not { } job) return null;
            if (job.WorkflowId is null && job.ParentJobId is null && job.NextJobId is null) return null;
            var workflow = await EnsureWorkflow(job, persist: false);
            var records = await _jobs.GetMany<JobRecord>(workflow.JobIds);
            if (records.Any(j => j is null)) throw new InvalidDataException("Workflow metadata is incomplete.");
            var steps = records.Select(j => new WorkflowStepView(j!.Id, j.Name, j.DisplayState(now),
                j.PredecessorIds.Length != 0 ? j.PredecessorIds : j.ParentJobId is { } p ? [p] : [],
                j.Condition, j.DependencySkipped, j.Progress, j.Error)).ToArray();
            int completed = records.Count(j => Terminal(j!.State));
            string status = records.Any(j => j!.State == JobState.Running) ? "Running" : completed != records.Length ? "Waiting" :
                records.Any(j => j!.State == JobState.DeadLettered) ? "Failed" :
                records.Any(j => j!.State == JobState.Cancelled && !j.DependencySkipped) ? "Cancelled" : "Succeeded";
            return new(workflow.Id, status, completed, steps.Length, steps);
        });
    }

    private async Task<List<JobRecord>> RetainWorkflowMembers(List<JobRecord> candidates, long now)
    {
        var result = new Dictionary<string, JobRecord>();
        var visited = new HashSet<string>();
        foreach (var job in candidates)
        {
            if (job.WorkflowId is not { } id) { result[job.Id] = job; continue; }
            if (!visited.Add(id)) continue;
            var workflow = await _workflows.Get(id) ?? throw new InvalidDataException("Workflow metadata is missing.");
            var members = await _jobs.GetMany<JobRecord>(workflow.JobIds);
            if (members.Any(j => j is null || !Terminal(j.State) || _retainCompletedJobs && j.CompletedAt > now - (long)HistoryRetention.TotalMilliseconds)) continue;
            foreach (var member in members) result[member!.Id] = member;
            foreach (string key in workflow.SubmissionKeys) await _workflowKeys.Remove(key);
            foreach (string memberId in workflow.JobIds) await _dependents.Remove(memberId);
            await _workflows.Remove(id);
        }
        return result.Values.ToList();
    }
}
