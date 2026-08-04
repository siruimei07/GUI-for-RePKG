using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace WallpaperField.ViewModels;

/// <summary>
/// Replaces a data set with one reset notification, avoiding one UI layout and
/// aggregate-count pass per discovered wallpaper.
/// </summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
