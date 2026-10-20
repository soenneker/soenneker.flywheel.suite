namespace Soenneker.Flywheel.Core.Stores.Librarian;

// Keep the rolling file header separate from its batches: appending must not rewrite a 200 MB file in Redis.
internal sealed record LogArchiveFileState(string Id, long CreatedAt, long RotateAt, long Bytes,
    int PartCount = 0, string Token = "", long AvailableAt = 0, bool Saved = false);
