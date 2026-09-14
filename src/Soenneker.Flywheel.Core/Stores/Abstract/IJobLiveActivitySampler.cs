namespace Soenneker.Flywheel.Core.Stores.Abstract;

/// <summary>Records authoritative concurrency samples for the hosted activity recorder.</summary>
public interface IJobLiveActivitySampler
{
    /// <summary>Persists a sample and returns whether queued, scheduled, or running work remains.</summary>
    Task<bool> SampleLiveActivity(CancellationToken cancellationToken = default);
}
