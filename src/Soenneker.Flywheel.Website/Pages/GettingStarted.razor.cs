namespace Soenneker.Flywheel.Website.Pages;

public partial class GettingStarted
{
    private const string CreateAppExample = """
dotnet new web -n FlywheelExample
cd FlywheelExample
dotnet add package Soenneker.Flywheel.Core
dotnet add package Soenneker.Flywheel.Redis
dotnet add package Soenneker.Flywheel.Generators
""";

    private const string FirstJobExample = """
using Soenneker.Flywheel.Core.Attributes;
using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Flywheel.Core.Services.Abstract;
using Soenneker.Flywheel.Generated;
using Soenneker.Flywheel.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddFlywheel(options => options.Workers = 4)
    .AddRedis(options => options.ConnectionString = "localhost:6379")
    .AddGeneratedJobs();

var app = builder.Build();
var client = app.Services.GetRequiredService<IJobClient>();

await client.Enqueue(FlywheelJobs.MessageJobs_Write,
    new Message("Hello from Flywheel"));

await app.RunAsync();

public sealed record Message(string Text);

public sealed class MessageJobs(ILogger<MessageJobs> logger)
{
    [FlywheelJob("message.write.v1")]
    public Task Write(Message message, CancellationToken cancellationToken)
    {
        logger.LogInformation("{Message}", message.Text);
        return Task.CompletedTask;
    }
}
""";

    private const string RunExample = """
dotnet run
""";
}
