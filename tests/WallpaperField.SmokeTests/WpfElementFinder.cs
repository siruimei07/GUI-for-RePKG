using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Media;

internal static class WpfElementFinder
{
    internal static T? FindByName<T>(DependencyObject root, string name)
        where T : DependencyObject
    {
        if (root is FrameworkElement element
            && element.FindName(name) is T namedElement)
        {
            return namedElement;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (FindByName<T>(child, name) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }
}

internal sealed class WpfBindingErrorCollector : TraceListener
{
    private readonly TraceSource _source;
    private readonly SourceLevels _previousLevel;
    private readonly StringBuilder _errors = new();

    internal WpfBindingErrorCollector()
    {
        _source = PresentationTraceSources.DataBindingSource;
        _previousLevel = _source.Switch.Level;
        _source.Switch.Level = SourceLevels.Error;
        _source.Listeners.Add(this);
    }

    internal bool HasErrors => _errors.Length > 0;

    internal string Summary
    {
        get
        {
            const int maxLength = 2_000;
            var value = _errors.ToString().Trim();
            return value.Length <= maxLength ? value : value[..maxLength];
        }
    }

    public override void Write(string? message) => _errors.Append(message);

    public override void WriteLine(string? message) => _errors.AppendLine(message);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _source.Listeners.Remove(this);
            _source.Switch.Level = _previousLevel;
        }

        base.Dispose(disposing);
    }
}
