namespace Soenneker.Flywheel.Website.Pages;

public partial class AdvancedUsage
{
    private const string DefinitionsExample = """
await client.SetRecurringEnabled("hourly-message", false);
await client.UpdateRecurring("hourly-message", FlywheelJobs.MessageJobs_Write,
    new Message("Updated message"), TimeSpan.FromMinutes(30), enabled: false);
await client.SetRecurringEnabled("hourly-message", true);
await client.DeleteRecurring("hourly-message");
""";

    private const string ContinuationsExample = """
using Soenneker.Flywheel.Communication.Enums;

string parentId = await client.Enqueue(FlywheelJobs.MessageJobs_Write, new Message("Prepare"));
string cleanupId = await client.ContinueWith(parentId, FlywheelJobs.MessageJobs_Write,
    new Message("Cleanup"), condition: ContinuationCondition.OnCompleted,
    idempotencyKey: "message-cleanup-123");
""";

    private const string BatchExample = """
var batch = await client.Batch([
    FlywheelJobs.MessageJobs_Write.With(new Message("Process A"), MessageJsonContext.Default.Message),
    FlywheelJobs.MessageJobs_Write.With(new Message("Process B"), MessageJsonContext.Default.Message)
], continuation: FlywheelJobs.MessageJobs_Write.With(
    new Message("Finalize"), MessageJsonContext.Default.Message),
    condition: ContinuationCondition.OnSuccess,
    idempotencyKey: "message-batch-123");
""";

    private const string ChainExample = """
IReadOnlyList<string> jobIds = await client.Chain([
    FlywheelJobs.MessageJobs_Write.With(
        new Message("First step"), MessageJsonContext.Default.Message),
    FlywheelJobs.MessageJobs_Write.With(
        new Message("Second step"), MessageJsonContext.Default.Message,
        delay: TimeSpan.FromSeconds(10))
], idempotencyKey: "message-workflow-123");
""";

    private const string ChainJsonExample = """
using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(System.Text.Json.JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(Message))]
internal partial class MessageJsonContext : JsonSerializerContext;
""";

    private const string PoolsExample = """
builder.Services.AddFlywheel(options =>
{
    options.Workers = 8;
    options.Pools["reports"] = 3;
    options.Pools["imports"] = 2;
})
    .AddRedis(options => options.ConnectionString = "localhost:6379")
    .AddGeneratedJobs();
""";

    private const string PoolJobExample = """
[FlywheelJob("message.write.v1", Pools = new[] { "reports" })]
""";

    private const string QueueExample = """
using Soenneker.Flywheel.Communication.Enums;

bool moved = await client.UpdateQueue(executionId, JobQueueAction.MoveToTop);
""";

    private const string LimitsExample = """
[FlywheelJob("message.write.v1",
    MaxConcurrency = 1,
    RateLimit = 10,
    RateWindowSeconds = 60)]
""";
}
