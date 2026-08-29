using System.Windows;
using System.Windows.Controls;

namespace WallpaperField.Controls;

public sealed class BrowseSelectionToggle : CheckBox
{
    public static readonly RoutedEvent SelectionRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(SelectionRequested),
        RoutingStrategy.Bubble,
        typeof(RoutedEventHandler),
        typeof(BrowseSelectionToggle));

    public event RoutedEventHandler SelectionRequested
    {
        add => AddHandler(SelectionRequestedEvent, value);
        remove => RemoveHandler(SelectionRequestedEvent, value);
    }

    protected override void OnToggle()
        => RaiseEvent(new RoutedEventArgs(SelectionRequestedEvent, this));
}
