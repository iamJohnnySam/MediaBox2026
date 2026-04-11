using Microsoft.AspNetCore.Components;

namespace MediaBox2026.Services;

/// <summary>
/// Custom component activator that bypasses ActivatorUtilities.CreateFactory,
/// which fails on some Linux runtimes for components with parameterless constructors.
/// All components in this project use property injection (@inject), so simple
/// Activator.CreateInstance is sufficient.
/// </summary>
public sealed class FallbackComponentActivator : IComponentActivator
{
    public IComponent CreateInstance(Type componentType)
    {
        if (!typeof(IComponent).IsAssignableFrom(componentType))
            throw new ArgumentException($"The type {componentType.FullName} does not implement {nameof(IComponent)}.", nameof(componentType));

        return (IComponent)Activator.CreateInstance(componentType)!;
    }
}
