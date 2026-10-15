using Microsoft.AspNetCore.Components;
using Soenneker.Quark;
using IComponent = Microsoft.AspNetCore.Components.IComponent;

namespace Soenneker.Flywheel.Dashboard.Tests;

internal sealed class JobActionsTestActivator : IComponentActivator
{
    public IComponent CreateInstance(Type componentType)
    {
        var component = (IComponent)Activator.CreateInstance(componentType)!;
        // Start the test menu open so the renderer can inspect its available actions.
#pragma warning disable BL0005
        if (component is Dropdown dropdown) dropdown.Visible = true;
#pragma warning restore BL0005
        return component;
    }
}
