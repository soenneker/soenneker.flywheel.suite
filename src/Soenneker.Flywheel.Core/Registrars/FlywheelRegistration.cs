using System.Text.Json.Serialization;
using Soenneker.Flywheel.Core.Stores.Abstract;
using Soenneker.Flywheel.Core.Services.Abstract;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soenneker.Flywheel.Core.Logging;
using Soenneker.Flywheel.Core.Options;
using Soenneker.Flywheel.Core.Services;

namespace Soenneker.Flywheel.Core.Registrars;

/// <summary>Registers workers, maintenance, job submission, and ambient logging services.</summary>
public static class FlywheelRegistration
{
    /// <summary>Registers Flywheel runtime. Add a storage provider and generated job registrations before starting.</summary>
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Default job payload serialization uses reflection. Supply a generated JsonSerializerContext.")]
    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Default job payload serialization uses reflection. Supply a generated JsonSerializerContext.")]
    public static FlywheelBuilder AddFlywheel(this IServiceCollection services, Action<FlywheelOptions>? configure = null)
    {
        services.AddSingleton(sp => sp.GetService<JsonSerializerContext>() is { } context ? new JobPayloadJson(context) : new JobPayloadJson());
        return Register(services, configure);
    }

    /// <summary>Registers Flywheel with generated metadata for every job payload.</summary>
    public static FlywheelBuilder AddFlywheel(this IServiceCollection services, JsonSerializerContext jsonContext, Action<FlywheelOptions>? configure = null)
    {
        services.AddSingleton(jsonContext);
        services.AddSingleton(new JobPayloadJson(jsonContext));
        return Register(services, configure);
    }

    private static FlywheelBuilder Register(IServiceCollection services, Action<FlywheelOptions>? configure)
    {
        var options = new FlywheelOptions();
        configure?.Invoke(options);
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton<JobLogCapture>();
        services.AddSingleton<ILoggerProvider>(sp => sp.GetRequiredService<JobLogCapture>());
        services.AddSingleton<IJobClient>(sp => new JobClient(sp.GetRequiredService<IJobStore>(), sp.GetServices<IJobInvoker>(), sp.GetRequiredService<FlywheelOptions>(), sp.GetRequiredService<JobPayloadJson>()));
        services.AddSingleton<JobProgress>();
        services.AddSingleton<IJobProgress>(sp => sp.GetRequiredService<JobProgress>());
        services.AddSingleton<IJobExecutor, JobExecutor>();
        services.AddSingleton<IMaintenanceRunner, MaintenanceRunner>();
        services.AddHostedService<MethodPolicyRegistrationService>();
        services.AddSingleton<WorkerService>();
        services.AddSingleton<IWorkerPool>(sp => sp.GetRequiredService<WorkerService>());
        services.AddHostedService(sp => sp.GetRequiredService<WorkerService>());
        services.AddHostedService<MaintenanceService>();
        return new FlywheelBuilder(services);
    }
}
