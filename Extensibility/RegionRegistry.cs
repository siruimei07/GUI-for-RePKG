using System.Windows;

namespace FieldStation.Extensibility;

/// <summary>
/// Registers factories for named local UI slots. Factories prevent a WPF Visual from being
/// attached to more than one parent.
/// </summary>
public sealed class RegionRegistry
{
    private readonly Dictionary<string, Func<FrameworkElement>> _factories =
        new(StringComparer.OrdinalIgnoreCase);

    public static RegionRegistry Default { get; } = new();

    public void Register(string key, Func<FrameworkElement> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(factory);
        _factories[key] = factory;
    }

    public bool TryCreate(string key, out FrameworkElement? element)
    {
        if (_factories.TryGetValue(key, out var factory))
        {
            element = factory();
            return true;
        }

        element = null;
        return false;
    }
}
