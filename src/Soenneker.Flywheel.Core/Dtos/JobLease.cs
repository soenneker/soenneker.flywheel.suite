namespace Soenneker.Flywheel.Core.Dtos;

/// <summary>Lease capability. Every mutation checks token, version, state and expiry.</summary>
/// <param name="Job">Job snapshot associated with this lease.</param>
/// <param name="Token">Ownership token required to mutate the leased job.</param>
/// <param name="Version">Fencing version identifying the current lease generation.</param>
public sealed record JobLease(JobRecord Job, string Token, long Version);
