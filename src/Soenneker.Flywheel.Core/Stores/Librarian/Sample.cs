namespace Soenneker.Flywheel.Core.Stores.Librarian;

internal readonly record struct Sample(long Scheduled, long Running, long Queued);
