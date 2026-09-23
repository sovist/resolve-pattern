using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ResolvePattern;

public static class ServiceProviderExtensions
{
    /// <summary>
    /// Registers <see cref="AmbientResolver"/> as both <see cref="IResolver"/> and <see cref="ILazyResolver"/>.
    /// </summary>
    public static IServiceCollection AddResolvePattern(this IServiceCollection services)
    {
        services.TryAddSingleton<IResolver>(AmbientResolver.Instance);

        services.TryAddSingleton<ILazyResolver>(AmbientResolver.Instance);

        return services;
    }

    /// <summary>
    /// Registers an <see cref="ILazyResolverProvider"/> implementation and assigns its
    /// <see cref="ILazyResolverProvider.Resolver"/> from the container at construction.
    ///
    /// This is property injection, done by hand, one visible line per service.
    /// Microsoft.Extensions.DependencyInjection has no property injection of its own — containers
    /// like Autofac provide it (<c>PropertiesAutowired</c>), but relying on one would trade a
    /// one-line helper for a container dependency.
    /// </summary>
    public static IServiceCollection AddResolved<TService, TImplementation>(this IServiceCollection services, ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TService : class
        where TImplementation : class, ILazyResolverProvider, TService
    {
        services.AddResolvePattern();

        var factory = ActivatorUtilities.CreateFactory(typeof(TImplementation), Type.EmptyTypes);

        services.Add(new ServiceDescriptor(typeof(TImplementation), provider =>
        {
            var service = (TImplementation)factory(provider, null);

            service.Resolver = provider.GetRequiredService<ILazyResolver>();

            return service;
        }, lifetime));

        services.AddTransient<TService>(provider => provider.GetRequiredService<TImplementation>());

        return services;
    }

    /// <summary>
    /// Opens an ambient resolver scope and hands back the <see cref="IResolver"/> bound to it.
    ///
    /// You need one at every entry point — background workers, hosted jobs, migrations, console
    /// apps, tests, and web requests alike. An ASP.NET Core request scope is an
    /// <see cref="IServiceScope"/>, which is not the same thing as an ambient resolver scope, so a
    /// web app still opens one per request from middleware. See the README.
    ///
    ///     using (serviceProvider.UseScope(out var resolver))
    ///     {
    ///         var svc = resolver.Resolve&lt;IMyService&gt;();
    ///         // ...
    ///     }
    /// </summary>
    public static IDisposable UseScope(this IServiceProvider provider, out IResolver resolver)
    {
        var scope = ResolverScope.Begin(provider);

        resolver = scope;

        return scope;
    }

    /// <summary>
    /// Opens an ambient resolver scope, for callers that do not need the resolver itself — middleware, mostly, where the services downstream have their own.
    /// </summary>
    public static IDisposable UseScope(this IServiceProvider provider)
    {
        return provider.UseScope(out _);
    }

    /// <summary>
    /// Opens an ambient resolver scope only if there is not one already, and otherwise hands back the resolver for the scope that is already open.
    /// </summary>
    public static IDisposable EnsureUseScope(this IServiceProvider provider, out IResolver resolver)
    {
        var current = ResolverScope.Current;

        if (current != null)
        {
            resolver = current;

            return NoOpDisposable.Instance;
        }

        return provider.UseScope(out resolver);
    }

    /// <summary>
    /// Opens an ambient resolver scope only if there is not one already, for callers that do not need the resolver itself.
    /// </summary>
    public static IDisposable EnsureUseScope(this IServiceProvider provider)
    {
        return provider.EnsureUseScope(out _);
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public static NoOpDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}