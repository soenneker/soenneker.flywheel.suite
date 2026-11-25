using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Flywheel.Communication.Logging.Dtos;

namespace Soenneker.Flywheel.Core.Stores.Librarian;

internal sealed class LibrarianSession
{
    internal bool NotifyServers;
    internal string? LifecycleRevision;
    internal readonly ILibrarianTable[] Tables;
    internal readonly LibrarianTable<string, string> Payloads = new("payloads", immutableValues: true);
    internal readonly LibrarianTable<string, JoinState> Joins = new("joins");
    internal readonly LibrarianTable<string, JobRecord> Jobs = new("jobs");
    internal readonly LibrarianTable<string, string> Dedupe = new("dedupe");
    internal readonly LibrarianTable<VersionKey, string> Versions = new("versions");
    internal readonly LibrarianTable<string, MethodPolicy> Policies = new("policies");
    internal readonly LibrarianTable<string, Rate> Rates = new("rates");
    internal readonly LibrarianTable<string, DispatchCandidate> Dispatch = new("dispatch");
    internal readonly LibrarianTable<string, RunningEntry> Running = new("running");
    internal readonly LibrarianTable<string, OutstandingScheduleExecution> ScheduleExecutions = new("scheduleExecutions");
    internal readonly LibrarianTable<string, long> PartitionTurns = new("partitionTurns");
    internal readonly LibrarianTable<string, string> ChainMembership = new("chainMembership");
    internal readonly LibrarianTable<string, long> InstanceChecks = new("instanceChecks");
    internal readonly LibrarianTable<string, DebounceEntry> Debounce = new("debounce");
    internal readonly LibrarianTable<string, LogBuffer> Logs = new("logs");
    internal readonly LibrarianTable<string, JobLogEntry[]> LogPages = new("logPages");
    internal readonly LibrarianTable<string, Node> Nodes = new("nodes");
    internal readonly LibrarianTable<string, long> Metadata = new("metadata");
    internal readonly LibrarianTable<long, JobHistoryPoint> History = new("history");
    internal readonly LibrarianTable<long, JobHistoryPoint> Live = new("live");
    internal readonly LibrarianTable<long, Sample> Samples = new("samples");
    internal readonly LibrarianTable<string, IdleSample> Idle = new("idle");
    internal readonly LibrarianTable<string, ManualJob> ManualJobs = new("manualJobs");
    internal readonly LibrarianTable<string, Schedule> Schedules = new("schedules");
    internal readonly LibrarianTable<string, string[]> Chains = new("chains");
    internal readonly LibrarianTable<string, WorkflowRecord> Workflows = new("workflows");
    internal readonly LibrarianTable<string, string[]> Dependents = new("dependents");
    internal readonly LibrarianTable<string, string[]> WorkflowKeys = new("workflowKeys");

    internal LibrarianSession()
    {
        Tables = [ManualJobs, Jobs, Dedupe, Versions, Policies, Rates, History, Live, Samples, Logs, LogPages, Nodes, Schedules, Chains, Metadata, Idle, Dispatch, Running, ScheduleExecutions, PartitionTurns, ChainMembership, Debounce, Workflows, Dependents, WorkflowKeys, InstanceChecks, Payloads, Joins];
    }
}
