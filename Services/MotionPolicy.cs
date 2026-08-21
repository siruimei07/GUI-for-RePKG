using System.ComponentModel;
using System.Windows;
using WallpaperField.Models;

namespace WallpaperField.Services;

public sealed class MotionPolicy : INotifyPropertyChanged, IDisposable
{
    private bool _reducedMotionRequested;
    private MotionPreference _preference;
    private bool _disposed;

    public MotionPolicy(bool reducedMotionRequested = false)
    {
        _reducedMotionRequested = reducedMotionRequested;
        _preference = ReadPreference();
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MotionPreference Preference => _preference;

    public bool MotionEnabled => Preference.MotionEnabled;

    public void SetReducedMotionRequested(bool requested)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_reducedMotionRequested == requested)
        {
            return;
        }

        _reducedMotionRequested = requested;
        RefreshPreference();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        _disposed = true;
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName)
            || e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
        {
            RefreshPreference();
        }
    }

    private MotionPreference ReadPreference()
        => new(SystemParameters.ClientAreaAnimation, _reducedMotionRequested);

    private void RefreshPreference()
    {
        var current = ReadPreference();
        if (current == _preference)
        {
            return;
        }

        _preference = current;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Preference)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MotionEnabled)));
    }
}
