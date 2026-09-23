using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace ResolvePattern;

/// <summary>
/// The ambient resolver scope.
/// </summary>
public sealed class ResolverScope : ILazyResolver, IDisposable
{
    static readonly AsyncLocal<ResolverScope?> CurrentScope = new();

    readonly IServiceScope _scope;

    readonly ResolverScope? _previous;

    readonly ConcurrentDictionary<Type, object> _cache = new();

    bool _disposed;

    private ResolverScope(IServiceScope scope)
    {
        _scope = scope;

        _previous = CurrentScope.Value; // support nested scopes (e.g. a job inside a request)

        CurrentScope.Value = this;
    }

    public static ResolverScope? Current => CurrentScope.Value;

    public static ResolverScope Require()
    {
        return CurrentScope.Value ?? throw new InvalidOperationException("There is no current resolver scope. Wrap the entry point in serviceProvider.UseScope().");
    }

    public static ResolverScope Begin(IServiceProvider provider)
    {
        return new ResolverScope(provider.CreateScope());
    }

    /// <summary>
    /// Resolves with the service's registered lifetime.
    /// </summary>
    public T Resolve<T>() where T : notnull
    {
        ThrowIfDisposed();

        return _scope.ServiceProvider.GetRequiredService<T>();
    }

    /// <summary>
    /// Cached, lazy resolution. Returns the same instance for the life of this scope, whatever the registered lifetime.
    /// </summary>
    public T ResolveLazy<T>() where T : notnull
    {
        return (T)_cache.GetOrAdd(typeof(T), _ => Resolve<T>());
    }

    /// <summary>
    /// Untyped resolution, for interop with code that expects an <see cref="IServiceProvider"/>.
    /// </summary>
    public object? GetService(Type serviceType)
    {
        ThrowIfDisposed();

        return _scope.ServiceProvider.GetService(serviceType);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _cache.Clear();

        CurrentScope.Value = _previous;

        _scope.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ResolverScope), "The resolver scope has ended. This flow was forked inside a scope that has since been disposed — a continuation outliving its request, typically. Open a scope of its own for the work.");
        }
    }
}
