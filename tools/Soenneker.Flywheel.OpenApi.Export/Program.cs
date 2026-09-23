using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.OpenApi;
using Soenneker.Flywheel.Core.Dashboard;
using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Hashing.Pbkdf2;

// Build-time generation inspects endpoint metadata without starting workers or a web server.
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
new FlywheelBuilder(builder.Services).AddDashboard(options =>
{
    options.PasswordPhc = Pbkdf2HashingUtil.Hash(Guid.NewGuid().ToString());
});
builder.Services.Remove(builder.Services.Single(service => service.ImplementationType == typeof(DashboardNotifications)));
builder.Services.AddOpenApi("v1", options =>
{
    options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_0;
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "Flywheel Dashboard API";
        document.Info.Version = "v1";
        document.Info.Description = "Flywheel dashboard HTTP API. Routes use the default /flywheel EnginePath; " +
            "hosts can configure a different prefix. Obtain a token from /flywheel/csrf before signing in, " +
            "send it in X-Flywheel-CSRF to /flywheel/login, and retain the cookies. " +
            "Refresh the CSRF token after signing in and send it in X-Flywheel-CSRF for all POST requests. " +
            "The SignalR hub is not described by this HTTP specification.";
        document.Servers = [];
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
        {
            ["FlywheelCookie"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Cookie,
                Name = "__Host-Flywheel"
            }
        };
        document.Security = [new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("FlywheelCookie", document)] = []
        }];
        return Task.CompletedTask;
    });
    options.AddOperationTransformer((operation, context, _) =>
    {
        if (context.Description.ActionDescriptor is ControllerActionDescriptor action)
            operation.OperationId = $"{action.ControllerName}_{action.ActionName}";
        if (context.Description.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any())
            operation.Security = [];
        if (context.Description.HttpMethod == "POST")
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "X-Flywheel-CSRF",
                In = ParameterLocation.Header,
                Required = true,
                Description = "Request token from /flywheel/csrf. Retain the antiforgery cookie as well.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String }
            });
        }
        return Task.CompletedTask;
    });
});

WebApplication app = builder.Build();
app.MapControllers();
app.Run();
