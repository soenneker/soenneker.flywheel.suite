namespace Soenneker.Flywheel.Core.Stores.Librarian;

internal readonly record struct Node(long ExpiresAt, int Workers);
