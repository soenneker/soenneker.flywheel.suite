using Soenneker.Flywheel.Core.Requests;

namespace Soenneker.Flywheel.Core.Dtos;

/// <summary>A job and serialized payload prepared for a chain. Create with a typed job definition's With method.</summary>
public sealed class JobStep
{
    internal EnqueueRequest Request { get; }
    internal JobStep(EnqueueRequest request) => Request = request;
}
