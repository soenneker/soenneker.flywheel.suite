using System;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Dtos;

namespace Soenneker.Flywheel.Core.Tests.Jobs;
public sealed class FlywheelTests
{
    [Test]
    public void DispatchPolicyValidation()
    {
        foreach (MethodPolicy policy in new[] { new MethodPolicy { MaxConcurrency = 0 }, new MethodPolicy { RateLimit = -1 },
                     new MethodPolicy { RateWindow = TimeSpan.Zero } })
        {
            try { policy.Validate(); throw new Exception("Invalid method policy accepted"); }
            catch (ArgumentOutOfRangeException) { }
        }
        new MethodPolicy().Validate();
        foreach (JobPriority priority in new[] { Soenneker.Flywheel.Communication.Enums.JobPriority.Low, Soenneker.Flywheel.Communication.Enums.JobPriority.Normal,
                     Soenneker.Flywheel.Communication.Enums.JobPriority.High, Soenneker.Flywheel.Communication.Enums.JobPriority.Critical })
        {
            var policy = new JobPolicy { Priority = priority };
            policy.Validate();
            var copy = System.Text.Json.JsonSerializer.Deserialize<JobPolicy>(System.Text.Json.JsonSerializer.Serialize(policy))!;
            if (copy.Priority != priority) throw new Exception("Priority did not round trip");
        }
    }

    [Test]
    public void RetryPolicyBoundsAndValidation()
    {
        var p = new JobPolicy { InitialBackoff = TimeSpan.FromSeconds(1), MaxBackoff = TimeSpan.FromSeconds(5), Jitter = .2 };
        if (p.RetryDelay(1, 0) != TimeSpan.FromMilliseconds(800) || p.RetryDelay(1, 1) != TimeSpan.FromMilliseconds(1200) || p.RetryDelay(1000, 1) != TimeSpan.FromSeconds(5))
            throw new Exception("Backoff bounds failed");
        try { (p with { Jitter = double.NaN }).Validate(); throw new Exception("Invalid jitter accepted"); }
        catch (ArgumentOutOfRangeException) { }
    }
}
