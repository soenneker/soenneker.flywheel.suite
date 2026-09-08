using Soenneker.Flywheel.Communication.Dtos;

namespace Soenneker.Flywheel.Communication.Requests;

/// <summary>Immutable request; delays are relative to authoritative storage time.</summary>
/// <param name="Name">Stable job name used for registration and persisted work.</param>
/// <param name="Payload">JSON-serialized payload passed to the job handler.</param>
/// <param name="Policy">Execution timeout, attempt limit, and retry settings.</param>
/// <param name="Delay">Delay before the job becomes eligible to execute.</param>
/// <param name="IdempotencyKey">Optional key used to deduplicate enqueue requests while the key is retained.</param>
/// <param name="Description">Optional human-readable description displayed by dashboard clients.</param>
public sealed record EnqueueRequest(string Name, string Payload, JobPolicy Policy, TimeSpan Delay, string? IdempotencyKey = null,
    string? Description = null);
