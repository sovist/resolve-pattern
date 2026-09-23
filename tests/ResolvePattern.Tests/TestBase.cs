namespace ResolvePattern.Tests;

/// <summary>
/// The sample domain, registered through <c>AddResolved</c> — one line per service, which is the
/// only registration-side difference the pattern asks for.
/// </summary>
public abstract class TestBase
{
    /// <summary>
    /// The sample services registered with one lifetime — for tests that add a registration of
    /// their own before building.
    /// </summary>
    protected static IServiceCollection BuildServices(ServiceLifetime lifetime = ServiceLifetime.Scoped)
    {
        return new ServiceCollection()
            .AddResolved<IProjectService, ProjectService>(lifetime)
            .AddResolved<ITaskService, TaskService>(lifetime);
    }

    protected static ServiceProvider BuildProvider(ServiceLifetime lifetime = ServiceLifetime.Scoped)
    {
        return BuildServices(lifetime).BuildServiceProvider();
    }
}