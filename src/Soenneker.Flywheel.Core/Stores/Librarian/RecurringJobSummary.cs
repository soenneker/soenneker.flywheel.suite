using Soenneker.Flywheel.Communication.Enums;

namespace Soenneker.Flywheel.Core.Stores.Librarian;

// Read-only projection: recurring lists do not need payloads, policies, or execution details.
internal sealed record RecurringJobSummary(string Name, JobState State, long DueAt, bool CancelRequested)
{
    internal string DisplayState(long now) => CancelRequested && State == JobState.Running ? "Cancelling" :
        State == JobState.Scheduled && DueAt <= now ? "Queued" : State.Name;
}
