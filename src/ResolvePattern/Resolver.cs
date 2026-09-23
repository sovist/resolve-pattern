namespace ResolvePattern;

/// <summary>
/// The resolution surface, as an injectable contract.
/// </summary>
public interface IResolver : IServiceProvider
{
    /// <summary>
    /// Resolves with the service's registered lifetime — a transient is new each call, a scoped one is shared within the scope.
    /// </summary>
    T Resolve<T>() where T : notnull;
}

/// <summary>
/// An <see cref="IResolver"/> that can also memoize.
/// </summary>
public interface ILazyResolver : IResolver
{
    /// <summary>
    /// Cached, lazy resolution. Returns the same instance for the life of the current scope, whatever the service's registered lifetime.
    /// </summary>
    T ResolveLazy<T>() where T : notnull;
}

/// <summary>
/// Marks a type whose <see cref="Resolver"/> the container assigns at construction.
/// </summary>
public interface ILazyResolverProvider
{
    ILazyResolver Resolver { get; set; }
}

/// <summary>
/// The <see cref="ILazyResolver"/> that forwards to whichever <see cref="ResolverScope"/> is ambient at the moment of the call.
///
/// It is stateless and therefore safe to register as a singleton: it holds no scope of its own, it looks one up per call.
/// That indirection is what lets a single registered instance serve every scope.
/// <c>services.AddResolvePattern()</c> registers it under both <see cref="IResolver"/> and <see cref="ILazyResolver"/>.
/// Register your own <see cref="ILazyResolver"/> first and every <see cref="ILazyResolverProvider"/> the
/// container constructs receives that instead.
/// </summary>
public sealed class AmbientResolver : ILazyResolver
{
    /// <summary>
    /// The single instance <c>AddResolvePattern()</c> registers.
    /// </summary>
    public static AmbientResolver Instance { get; } = new();

    public T Resolve<T>() where T : notnull
    {
        return ResolverScope.Require().Resolve<T>();
    }

    public T ResolveLazy<T>() where T : notnull
    {
        return ResolverScope.Require().ResolveLazy<T>();
    }

    public object? GetService(Type serviceType)
    {
        return ResolverScope.Require().GetService(serviceType);
    }
}