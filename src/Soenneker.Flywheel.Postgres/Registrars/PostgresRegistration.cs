using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Flywheel.Core.Stores.Abstract;
using Microsoft.Extensions.DependencyInjection;


namespace Soenneker.Flywheel.Postgres;

/// <summary>Registers the Postgres persistence provider with a Flywheel runtime.</summary>
public static class PostgresRegistration
{
    /// <summary>Adds PostgreSQL lifecycle persistence and distributed coordination.</summary>
    public static FlywheelBuilder AddPostgres(this FlywheelBuilder builder, Action<FlywheelPostgresOptions>? configure = null)
    {
        var options = new FlywheelPostgresOptions();
        configure?.Invoke(options);
        PostgresJobStore.ValidateOptions(options);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<PostgresJobStore>();
        builder.Services.AddHostedService<PostgresLiveActivityRecorder>();
        builder.Services.AddSingleton<IJobChangeFeed>(sp => sp.GetRequiredService<PostgresJobStore>());
        builder.Services.AddSingleton<IJobStore>(sp => sp.GetRequiredService<PostgresJobStore>());
        builder.Services.AddSingleton<ICronJobStore>(sp => sp.GetRequiredService<PostgresJobStore>());
        builder.Services.AddSingleton<IJobChainStore>(sp => sp.GetRequiredService<PostgresJobStore>());
        builder.Services.AddSingleton<IMethodPolicyStore>(sp => sp.GetRequiredService<PostgresJobStore>());
        builder.Services.AddSingleton<INodeStore>(sp => sp.GetRequiredService<PostgresJobStore>());
        builder.Services.AddSingleton<IJobLogStore>(sp => sp.GetRequiredService<PostgresJobStore>());
        builder.Services.AddSingleton<IJobProgressStore>(sp => sp.GetRequiredService<PostgresJobStore>());
        builder.Services.AddSingleton<IServerStore>(sp => sp.GetRequiredService<PostgresJobStore>());
        return builder;
    }
}

