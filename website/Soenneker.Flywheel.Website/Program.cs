using Soenneker.Flywheel.Website.Components;
using Soenneker.Quark;
using Soenneker.Quark.Gen.Lucide.Generated;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents();
builder.Services.AddQuarkSuiteAsScoped().AddLucideIconsAsScoped();
var app = builder.Build();
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>();
app.Run();
