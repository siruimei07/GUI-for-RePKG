using System.Windows;

namespace FieldStation.ViewModels;

internal static class UiThread
{
    public static void Run(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }
}
