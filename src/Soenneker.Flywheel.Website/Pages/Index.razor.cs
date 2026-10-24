namespace Soenneker.Flywheel.Website.Pages;

public partial class Index
{

    private const string EnqueueExample = """
                                          await client.Enqueue(
                                              FlywheelJobs.MessageJobs_Write,
                                              new Message("Hello from Flywheel"));
                                          """;

    private const string DelayExample = """
                                        await client.Enqueue(
                                            FlywheelJobs.MessageJobs_Write,
                                            new Message("In ten minutes"),
                                            delay: TimeSpan.FromMinutes(10));
                                        """;

    private const string ScheduleExample = """
                                           await client.Schedule(
                                               FlywheelJobs.MessageJobs_Write,
                                               new Message("Tomorrow at this time"),
                                               DateTimeOffset.UtcNow.AddDays(1));
                                           """;

    private const string RecurringExample = """
                                            await client.Recurring(
                                                "hourly-message", FlywheelJobs.MessageJobs_Write,
                                                new Message("Every hour"),
                                                TimeSpan.FromHours(1));
                                            """;

    private const string ManualExample = """
        await client.Manual("manual-message", FlywheelJobs.MessageJobs_Write,
            new Message("Requested by an operator"));

        await client.RunManual("manual-message");
        """;

    private const string CronExample = """
                                       await client.Schedule(
                                           "weekday-message", FlywheelJobs.MessageJobs_Write,
                                           new Message("Good morning"), "0 9 * * MON-FRI",
                                           timeZoneId: "America/Chicago");
                                       """;

    private const string RegistrationExample = """
                                               builder.Services.AddFlywheel()
                                                   .AddRedis(options =>
                                                       options.ConnectionString = "localhost:6379")
                                                   .AddGeneratedJobs();

                                               await client.Enqueue(
                                                   FlywheelJobs.MessageJobs_Write,
                                                   new Message("Hello from Flywheel"));
                                               """;

}
