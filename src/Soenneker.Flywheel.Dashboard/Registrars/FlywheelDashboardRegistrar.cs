using Microsoft.Extensions.DependencyInjection;
using Soenneker.Quark;
using Soenneker.SignalR.Web.Clients.Registrars;
using Soenneker.Quark.Gen.Lucide.Generated;
using Soenneker.Flywheel.Dashboard.Communication;
using Soenneker.Flywheel.Dashboard.Communication.Abstract;
using Soenneker.Flywheel.Dashboard.Consumers;
using Soenneker.Flywheel.Dashboard.Consumers.Abstract;

namespace Soenneker.Flywheel.Dashboard.Registrars;

/// <summary>Registers dashboard state and the Quark and SignalR dependencies used by the client.</summary>
public static class FlywheelDashboardRegistrar
{
    /// <summary>
    /// Registers the dashboard and an HTTP client for the specified backend base address.
    /// HTTP requests and SignalR negotiation include browser cookies. The backend must allow
    /// credentialed requests from the dashboard origin when hosted separately.
    /// </summary>
    public static IServiceCollection AddFlywheelDashboardAsScoped(this IServiceCollection services, Uri backendBaseAddress,
        Action<DashboardNavigationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(backendBaseAddress);
        if (!backendBaseAddress.IsAbsoluteUri || (backendBaseAddress.Scheme != Uri.UriSchemeHttps && backendBaseAddress.Scheme != Uri.UriSchemeHttp) ||
            !string.IsNullOrEmpty(backendBaseAddress.Query) || !string.IsNullOrEmpty(backendBaseAddress.Fragment) ||
            !string.IsNullOrEmpty(backendBaseAddress.UserInfo))
            throw new ArgumentException("Backend base address must be an absolute HTTP(S) URL without credentials, query, or fragment.", nameof(backendBaseAddress));

        var baseAddress = new Uri(backendBaseAddress.AbsoluteUri.TrimEnd('/') + "/");
        services.AddFlywheelDashboardAsScoped(configure ?? (_ => { }));
        services.AddScoped(_ => new HttpClient(new DashboardCredentialsHandler(new HttpClientHandler())) { BaseAddress = baseAddress });
        return services;
    }

    /// <summary>Registers the dashboard pages, layout, and Quark components. Supply a same-origin HttpClient in the WASM host.</summary>
    public static IServiceCollection AddFlywheelDashboardAsScoped(this IServiceCollection services) =>
        services.AddFlywheelDashboardAsScoped(_ => { });

    /// <summary>Registers the dashboard home path. Use FlywheelRouter in the host to serve the configured home without a wrapper page.</summary>
    public static IServiceCollection AddFlywheelDashboardAsScoped(this IServiceCollection services, Action<DashboardNavigationOptions> configure)
    {
        var options = new DashboardNavigationOptions();
        configure(options);
        if (options.HomePath != "/" && options.HomePath != "/flywheel")
            throw new ArgumentException("Dashboard HomePath must be /flywheel or /.", nameof(configure));

        return services.AddSingleton(options).AddScoped<ActivityTotalsState>().AddScoped<DashboardSessionState>().AddScoped<DashboardBoardConnection>()
            .AddScoped<IFlywheelApiClient, FlywheelApiClient>()
            .AddScoped<IFlywheelDashboardConsumer, FlywheelDashboardConsumer>()
            .AddScoped<IFlywheelLiveClient, FlywheelLiveClient>()
            .AddQuarkSuiteAsScoped().AddSignalRWebClientsAsScoped().AddLucideIconsAsScoped();
    }
}
