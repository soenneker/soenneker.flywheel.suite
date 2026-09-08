using Microsoft.Extensions.DependencyInjection;

namespace Soenneker.Flywheel.Core.Registrars;

/// <summary>Fluent provider registration surface.</summary>
public sealed class FlywheelBuilder(IServiceCollection services)
{
    /// <summary>Service collection receiving Flywheel registrations.</summary>
    public IServiceCollection Services { get; } = services;
}
