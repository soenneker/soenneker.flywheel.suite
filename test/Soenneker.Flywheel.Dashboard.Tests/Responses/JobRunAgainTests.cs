using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Flywheel.Core.Dashboard.Endpoints;
using Soenneker.Flywheel.Core.Stores.Abstract;

namespace Soenneker.Flywheel.Dashboard.Tests;

public sealed class JobRunAgainTests
{
    [Test]
    [Arguments("Succeeded")]
    [Arguments("DeadLettered")]
    [Arguments("Cancelled")]
    public async Task FinishedJobsCreateFreshStandaloneExecutions(string state)
    {
        IJobStore store = DispatchProxy.Create<IJobStore, RunStore>();
        var capture = (RunStore)store;
        capture.Job = CreateJob(state);
        IResult result = await FlywheelJobsEndpoints.RunAgain(store, "original", CancellationToken.None);
        if (result is not JsonHttpResult<StartedJob> { Value: StartedJob { Id: "new-job" } } || capture.Request is not { } request)
            throw new Exception("Run did not return the newly queued execution.");
        if (request.Name != capture.Job.Name || request.Payload != capture.Job.Payload || request.Policy != capture.Job.Policy ||
            request.Description != capture.Job.Description || request.Delay != TimeSpan.Zero || request.IdempotencyKey is not null)
            throw new Exception("Run changed the original work or reused its scheduling or deduplication settings.");
    }

    [Test]
    [Arguments("Running", null, 409)]
    [Arguments("Scheduled", null, 409)]
    [Arguments("Waiting", null, 409)]
    [Arguments("Succeeded", "build-1", 501)]
    public async Task IneligibleJobsNeverEnqueue(string state, string? version, int status)
    {
        IJobStore store = DispatchProxy.Create<IJobStore, RunStore>();
        var capture = (RunStore)store;
        capture.Job = CreateJob(state) with { ApplicationVersion = version };
        IResult result = await FlywheelJobsEndpoints.RunAgain(store, "original", CancellationToken.None);
        if (result is not IStatusCodeHttpResult code || code.StatusCode != status || capture.Request is not null)
            throw new Exception("An ineligible job was queued.");
    }

    [Test]
    public async Task MissingAndInvalidJobsNeverEnqueue()
    {
        IJobStore store = DispatchProxy.Create<IJobStore, RunStore>();
        if (await FlywheelJobsEndpoints.RunAgain(store, "missing", CancellationToken.None) is not NotFound ||
            await FlywheelJobsEndpoints.RunAgain(store, "", CancellationToken.None) is not BadRequest || ((RunStore)store).Request is not null)
            throw new Exception("Invalid or missing job was accepted.");
    }

    private static JobRecord CreateJob(string state) => new()
    {
        Id = "original", Name = "Report.Run", Payload = "{\"account\":42}", Policy = new JobPolicy { MaxAttempts = 3 },
        Description = "Report", State = state switch
        {
            "Succeeded" => JobState.Succeeded, "DeadLettered" => JobState.DeadLettered,
            "Cancelled" => JobState.Cancelled, "Running" => JobState.Running,
            "Waiting" => JobState.Waiting, _ => JobState.Scheduled
        },
        ScheduleId = "recurring", ParentJobId = "previous", NextJobId = "next"
    };

    public class RunStore : DispatchProxy
    {
        public JobRecord? Job { get; set; }
        public EnqueueRequest? Request { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IJobStore.Get)) return Task.FromResult(Job);
            if (targetMethod?.Name == nameof(IJobStore.Enqueue))
            {
                Request = (EnqueueRequest)args![0]!;
                return Task.FromResult("new-job");
            }
            throw new NotSupportedException();
        }
    }
}
