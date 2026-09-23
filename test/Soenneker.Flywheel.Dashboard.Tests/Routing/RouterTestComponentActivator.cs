using Microsoft.AspNetCore.Components;

namespace Soenneker.Flywheel.Dashboard.Tests;

internal sealed class RouterTestComponentActivator : IComponentActivator
{
    public FlywheelLayout? Layout { get; private set; }

    public IComponent CreateInstance(Type componentType)
    {
        var component = (IComponent)Activator.CreateInstance(componentType)!;
        if (component is FlywheelLayout layout) Layout = layout;
        return component;
    }
}
