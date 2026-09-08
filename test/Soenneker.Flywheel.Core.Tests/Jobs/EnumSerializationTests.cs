using System;
using System.Text.Json;

namespace Soenneker.Flywheel.Core.Tests.Jobs;

public sealed class EnumSerializationTests
{
    [Test]
    public void PreservesStoredNumericValues()
    {
        Verify(new[] { Enums.JobState.Scheduled, Enums.JobState.Running, Enums.JobState.Succeeded, Enums.JobState.DeadLettered, Enums.JobState.Cancelled, Enums.JobState.Waiting });
        Verify(new[] { Enums.JobOutcome.Succeeded, Enums.JobOutcome.Failed, Enums.JobOutcome.Cancelled });
        Verify(new[] { Enums.LeaseStatus.Lost, Enums.LeaseStatus.Renewed, Enums.LeaseStatus.CancellationRequested });
    }

    private static void Verify<T>(T[] values) where T : struct
    {
        for (var i = 0; i < values.Length; i++)
        {
            var json = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (JsonSerializer.Serialize(values[i]) != json || !values[i].Equals(JsonSerializer.Deserialize<T>(json)))
                throw new Exception($"{typeof(T).Name} changed its persisted value for {values[i]}.");
        }

        if (!default(T).Equals(values[0])) throw new Exception($"{typeof(T).Name} changed its default value.");
    }
}
