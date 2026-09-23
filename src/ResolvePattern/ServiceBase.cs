namespace ResolvePattern;

/// <summary>
/// Base class for every domain / application service.
/// </summary>
public abstract class ServiceBase : ILazyResolverProvider
{
    /// <summary>
    /// The resolution surface this service resolves through.
    /// </summary>
    public ILazyResolver Resolver
    {
        get => field ?? throw new InvalidOperationException("No resolver is assigned to this service. Register it with AddResolved<TService, TImplementation>(), or assign Resolver directly in a test.");
        set;
    }

    protected T Resolve<T>() where T : notnull
    {
        //Default to: Cached, lazy, per-scope resolution.
        return Resolver.ResolveLazy<T>();
    }
}