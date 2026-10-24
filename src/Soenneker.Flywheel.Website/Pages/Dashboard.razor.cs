namespace Soenneker.Flywheel.Website.Pages;

public partial class Dashboard
{
    private const string DemoExample = """
git clone https://github.com/soenneker/soenneker.flywheel.suite.git
cd soenneker.flywheel.suite
dotnet dev-certs https --trust
dotnet run --project test/Soenneker.Flywheel.Demo
# In a second terminal:
dotnet run --project test/Soenneker.Flywheel.Dashboard.Demo
""";

    private const string InstallExample = """
dotnet add package Soenneker.Flywheel.Dashboard
""";

    private const string ClientExample = """
using Soenneker.Flywheel.Dashboard.Registrars;

builder.Services.AddScoped(_ => new HttpClient
{
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress)
});
builder.Services.AddFlywheelDashboardAsScoped();
""";

    private const string ServerExample = """
.AddDashboard(options =>
{
    options.Username = builder.Configuration["Flywheel:Dashboard:Username"]!;
    options.PasswordPhc = builder.Configuration["Flywheel:Dashboard:PasswordPhc"]!;
})
""";
}
