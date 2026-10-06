using System;
using Soenneker.Flywheel.Core.Services;
namespace Soenneker.Flywheel.Core.Tests.Jobs;
public sealed class JobPayloadJsonTests
{
    [Test]
    public void Generated_metadata_preserves_payload_names_and_round_trips()
    {
        var json = new JobPayloadJson(JobPayloadTestJsonContext.Default);
        object payload = new JobPayloadTestModel("kept");
        string text = json.Serialize(payload);
        if (text != "{\"wire_value\":\"kept\"}" || json.Deserialize<JobPayloadTestModel>(text)?.Value != "kept")
            throw new Exception("Generated payload contract was not honored.");
    }
    [Test]
    public void Legacy_constructor_preserves_existing_payload_support()
    {
        var json = new JobPayloadJson();
        if (json.Deserialize<JobPayloadTestModel>(json.Serialize(new JobPayloadTestModel("legacy")))?.Value != "legacy")
            throw new Exception("Legacy payload serialization failed.");
    }
    [Test]
    public void Generated_metadata_does_not_fall_back_to_reflection()
    {
        var json = new JobPayloadJson(JobPayloadTestJsonContext.Default);
        try { json.Serialize(new { Unregistered = true }); }
        catch (NotSupportedException) { return; }
        throw new Exception("Missing generated metadata should fail explicitly.");
    }
}
