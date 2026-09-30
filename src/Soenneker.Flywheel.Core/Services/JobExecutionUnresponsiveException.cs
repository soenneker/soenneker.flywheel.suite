namespace Soenneker.Flywheel.Core.Services;

/// <summary>A handler did not stop within the cancellation grace period. The worker pool must stop admitting work.</summary>
public sealed class JobExecutionUnresponsiveException(string jobId)
    : Exception($"Job {jobId} did not stop after cancellation; the worker host must restart.");
