using FieldStation.Contracts;
using FieldStation.Extensibility;
using FieldStation.Services;

namespace FieldStation.Composition;

/// <summary>The only place that chooses concrete backend and extension implementations.</summary>
public static class AppComposition
{
    private static IOperationsBackend? _backend;

    public static IOperationsBackend Backend => _backend ??= CreateBackend();

    public static void Configure()
    {
        _backend = CreateBackend();

        // Local UI extension example:
        // RegionRegistry.Default.Register("command.secondary", () => new YourControl());

        // Full-page extension example:
        // PageRegistry.Default.Register(new PageContribution(
        //     "your-page", "06", "业务", "BUSINESS", () => new YourPage()));
    }

    private static IOperationsBackend CreateBackend()
    {
        // Replace this one line when the production backend is ready.
        return new DemoOperationsBackend();
    }
}
