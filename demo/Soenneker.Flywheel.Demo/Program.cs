using Soenneker.Flywheel.Demo.Services.Abstract;
using Soenneker.Hashing.Pbkdf2;
using Microsoft.AspNetCore.RateLimiting;
using Soenneker.Flywheel.Core;
using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Flywheel.Demo;
using Soenneker.Flywheel.Demo.Services;
using Soenneker.Flywheel.Generated;
using Soenneker.Flywheel.Redis;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
string username = builder.Configuration["Flywheel:Dashboard:Username"] ?? "admin";
string? passwordPhc = builder.Configuration["Flywheel:Dashboard:PasswordPhc"];
if (string.IsNullOrWhiteSpace(passwordPhc) && builder.Environment.IsDevelopment())
{
    const string password = "flywheel-demo";
    passwordPhc = Pbkdf2HashingUtil.Hash(password);
    Console.WriteLine($"Flywheel demo Development login: {username} / {password}");
    Console.WriteLine("This demo password is for Development only. Configure Flywheel:Dashboard:PasswordPhc to override it.");
}

builder.Services.AddFlywheel(options => options.Workers = 4)
    .AddRedis(options =>
    {
        options.ConnectionString = builder.Configuration["Flywheel:Redis"] ?? "localhost:6379";
        options.Namespace = builder.Configuration["Demo:RedisNamespace"] ?? "demo";
    })
    .AddDashboard(options => { options.Username = username; options.PasswordPhc = passwordPhc ?? ""; })
    .AddGeneratedJobs();
builder.Services.AddScoped<IDemoDeliveryGateway, DemoDeliveryGateway>();
builder.Services.AddSingleton<DemoTour>();
builder.Services.AddRateLimiter(options => options.AddFixedWindowLimiter("DemoTour", limiter =>
{
    limiter.PermitLimit = 3;
    limiter.Window = TimeSpan.FromMinutes(1);
    limiter.QueueLimit = 0;
}));

WebApplication app = builder.Build();
app.UseHttpsRedirection();
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();
app.MapFlywheelDashboard();
app.MapFallbackToFile("index.html");

if (builder.Configuration.GetValue("Demo:SeedOnStartup", true))
{
    await app.Services.GetRequiredService<DemoTour>().Run(builder.Configuration["Demo:BatchId"]);
}
app.Logger.LogInformation("Dashboard: /. Open an execution row to see its logs.");
await app.RunAsync();
