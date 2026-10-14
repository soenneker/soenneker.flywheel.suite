using Soenneker.Flywheel.Communication.Dtos;

namespace Soenneker.Flywheel.Core.Stores.Librarian;

internal sealed record ManualJob(JobRecord Job, string? LastExecutionId = null, string? LastExecutionStatus = null);
