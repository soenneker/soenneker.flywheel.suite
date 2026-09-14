using Soenneker.Flywheel.Communication.Requests;

namespace Soenneker.Flywheel.Communication.Dtos;

/// <summary>A job and serialized payload prepared for a chain. Create with a typed job definition's With method.</summary>
public sealed class JobStep
{
    /// <summary>The enqueue request prepared for this chain step.</summary>
    public EnqueueRequest Request { get; }

    /// <summary>Creates a chain step from an enqueue request.</summary>
    /// <param name="request">The job and serialized payload to enqueue.</param>
    public JobStep(EnqueueRequest request) => Request = request;
}
