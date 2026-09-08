using System;
using System.Text.Json;

namespace Soenneker.Flywheel.Core.Tests.Jobs;

public sealed class EnumSerializationTests
{
    [Test]
    public void PreservesStoredNumericValues()
    {
        Verify(new[] { Communication.Enums.JobState.Scheduled, Communication.Enums.JobState.Running, Communication.Enums.JobState.Succeeded, Communication.Enums.JobState.DeadLettered, Communication.Enums.JobState.Cancelled, Communication.Enums.JobState.Waiting });
        Verify(new[] { Communication.Enums.JobOutcome.Succeeded, Communication.Enums.JobOutcome.Failed, Communication.Enums.JobOutcome.Cancelled });
        Verify(new[] { Communication.Enums.LeaseStatus.Lost, Communication.Enums.LeaseStatus.Renewed, Communication.Enums.LeaseStatus.CancellationRequested });
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
