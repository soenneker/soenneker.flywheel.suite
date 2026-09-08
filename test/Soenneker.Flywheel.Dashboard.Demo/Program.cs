using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Soenneker.Flywheel.Dashboard.Registrars;

namespace Soenneker.Flywheel.Dashboard.Demo;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebAssemblyHostBuilder.CreateDefault(args);
        builder.RootComponents.Add<App>("#app");
        builder.RootComponents.Add<HeadOutlet>("head::after");
        builder.Services.AddFlywheelDashboardAsScoped(new Uri("https://localhost:7443/"), options => options.HomePath = "/");
        await builder.Build().RunAsync();
    }
}
