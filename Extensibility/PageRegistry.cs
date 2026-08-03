using System.Windows;

namespace FieldStation.Extensibility;

/// <summary>Contribution contract for adding complete pages without editing MainWindow.</summary>
public sealed record PageContribution(
    string Key,
    string Index,
    string Label,
    string EnglishLabel,
    Func<FrameworkElement> CreatePage);

public sealed class PageRegistry
{
    private readonly List<PageContribution> _pages = [];

    public static PageRegistry Default { get; } = new();

    public IReadOnlyList<PageContribution> Pages => _pages;

    public void Register(PageContribution contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        if (_pages.Any(page => string.Equals(page.Key, contribution.Key, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Page key '{contribution.Key}' is already registered.");
        }

        _pages.Add(contribution);
    }
}
