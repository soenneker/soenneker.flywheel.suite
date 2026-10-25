using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;

namespace Soenneker.Flywheel.Core.Stores.Librarian;

// Deserializes only searchable fields; payloads and execution credentials stay out of scan buffers.
internal sealed record JobSearchDocument(string Id, string Name, JobState State, long CreatedAt,
    long UpdatedAt, long DueAt, string? Owner)
{
    public JobRecord ToRecord() => new() { Id = Id, Name = Name, State = State, CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt, DueAt = DueAt, Owner = Owner, Payload = "", Policy = new JobPolicy() };
}
