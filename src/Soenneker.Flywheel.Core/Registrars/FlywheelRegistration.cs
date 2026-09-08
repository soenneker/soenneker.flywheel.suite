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
    public static FlywheelBuilder AddFlywheel(this IServiceCollection services, Action<FlywheelOptions>? configure = null)
    {
        var options = new FlywheelOptions();
        configure?.Invoke(options);
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton<JobLogCapture>();
        services.AddSingleton<ILoggerProvider>(sp => sp.GetRequiredService<JobLogCapture>());
        services.AddSingleton<IJobClient, JobClient>();
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
