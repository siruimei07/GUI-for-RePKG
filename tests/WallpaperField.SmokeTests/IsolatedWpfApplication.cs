using System.Reflection;
using System.Windows;

internal static class IsolatedWpfApplication
{
    internal static WallpaperField.App Create()
        => Create(static application => application.InitializeComponent());

    internal static WallpaperField.App Create(
        Action<WallpaperField.App> initialize)
    {
        ArgumentNullException.ThrowIfNull(initialize);
        var application = new WallpaperField.App();
        try
        {
            initialize(application);
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var startupMethod = typeof(WallpaperField.App).GetMethod(
                "Application_Startup",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(
                    typeof(WallpaperField.App).FullName,
                    "Application_Startup");
            var startupHandler = startupMethod.CreateDelegate<StartupEventHandler>(application);
            application.Startup -= startupHandler;
            return application;
        }
        catch
        {
            try
            {
                application.Shutdown();
                _ = application.Run();
            }
            catch
            {
                // Preserve the initialization failure; shutdown is best-effort cleanup.
            }

            throw;
        }
    }
}
