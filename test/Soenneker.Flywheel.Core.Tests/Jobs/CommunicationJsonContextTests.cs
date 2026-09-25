using System;
using System.Text.Json;
using Soenneker.Flywheel.Communication;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Flywheel.Communication.Enums;

namespace Soenneker.Flywheel.Core.Tests.Jobs;

public sealed class CommunicationJsonContextTests
{
    [Test]
    public void DashboardContractsWorkWithoutReflection()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.TypeInfoResolverChain.Clear();
        FlywheelJsonContext.Configure(options);
        const string json = """{"name":"example","payload":"{}","policy":{"priority":0},"delay":"00:00:00"}""";
        var request = JsonSerializer.Deserialize<EnqueueRequest>(json, options)!;
        string serialized = JsonSerializer.Serialize(request, options);
        using var document = JsonDocument.Parse(serialized);
        if (document.RootElement.GetProperty("policy").GetProperty("priority").GetInt32() != 0)
            throw new Exception("Job priority must remain numeric.");
        var board = new LiveBoard(1, [], 0, null, null);
        string snapshot = JsonSerializer.Serialize(board, options);
        if (JsonSerializer.Deserialize(snapshot, FlywheelJsonContext.Get<LiveBoard>())?.Version != 1)
            throw new Exception("Live board did not round-trip.");
        if (JsonSerializer.Deserialize<JobState>("2", options) != JobState.Succeeded)
            throw new Exception("Job state must retain its numeric value.");
    }
}
