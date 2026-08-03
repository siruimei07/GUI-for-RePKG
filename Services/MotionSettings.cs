using System.Windows;

namespace FieldStation.Services;

/// <summary>Single motion policy shared by the shell, pages, controls, and QA harness.</summary>
public static class MotionSettings
{
    private static bool _userReducedMotion = !SystemParameters.ClientAreaAnimation;

    public static event EventHandler? Changed;

    public static bool IsReducedMotion
    {
        get => _userReducedMotion;
        set
        {
            if (_userReducedMotion == value)
            {
                return;
            }

            _userReducedMotion = value;
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }
}
