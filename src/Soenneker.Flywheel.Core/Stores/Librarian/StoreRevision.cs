using Soenneker.Flywheel.Communication.Dtos;

namespace Soenneker.Flywheel.Core.Stores.Librarian;

internal sealed record StoreRevision(string Revision, string? Previous, JobChange[] Changes);
