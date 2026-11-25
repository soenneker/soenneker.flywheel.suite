namespace Soenneker.Flywheel.Core.Stores.Librarian;

internal sealed record JoinState(int Remaining, bool Failed = false, bool Cancelled = false, bool Skipped = false);
