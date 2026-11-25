using Soenneker.Flywheel.Communication.Enums;

namespace Soenneker.Flywheel.Core.Stores.Librarian;

internal sealed record WorkflowRetentionState(JobState State, long CompletedAt);
