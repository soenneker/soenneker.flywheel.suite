using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace Soenneker.Flywheel.Core.Dashboard;

internal sealed class DashboardRouteConvention(string enginePath) : IApplicationModelConvention
{
    public void Apply(ApplicationModel application)
    {
        foreach (ControllerModel controller in application.Controllers)
        foreach (SelectorModel selector in controller.Selectors)
        {
            AttributeRouteModel? route = selector.AttributeRouteModel;
            if (route?.Template is not { } template || !template.Contains("[flywheel]", StringComparison.Ordinal)) continue;
            route.Template = template.Replace("[flywheel]", enginePath.Trim('/'), StringComparison.Ordinal).Trim('/');
        }
    }
}
