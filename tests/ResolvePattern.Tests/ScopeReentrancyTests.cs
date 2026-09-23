namespace ResolvePattern.Tests;

/// <summary>
/// Entry points open a scope; some code is reachable both from its own entry point and from inside
/// someone else's. These cover the difference between opening a scope unconditionally and ensuring
/// one exists. They resolve through the ambient <see cref="ILazyResolver"/> — the path a service's
/// own <c>Resolve&lt;T&gt;()</c> takes — because the cache is where nesting bites.
/// </summary>
public class ScopeReentrancyTests : TestBase
{
    [Fact]
    public void UseScope_ShouldNestASecondScopeWithItsOwnCache_When_AScopeIsAlreadyOpen()
    {
        // This is the behaviour EnsureUseScope exists to avoid: the same Resolve<T>() call hands
        // back a different instance inside the nested scope than outside it.
        using var provider = BuildProvider(ServiceLifetime.Transient);
        var lazy = provider.GetRequiredService<ILazyResolver>();

        using (provider.UseScope())
        {
            var outer = lazy.ResolveLazy<ITaskService>();

            using (provider.UseScope())
            {
                lazy.ResolveLazy<ITaskService>().ShouldNotBeSameAs(outer);
            }

            lazy.ResolveLazy<ITaskService>().ShouldBeSameAs(outer);
        }
    }

    [Fact]
    public void EnsureUseScope_ShouldReuseTheOpenScope_When_AScopeIsAlreadyOpen()
    {
        using var provider = BuildProvider(ServiceLifetime.Transient);
        var lazy = provider.GetRequiredService<ILazyResolver>();

        using (provider.UseScope())
        {
            var outer = lazy.ResolveLazy<ITaskService>();

            using (provider.EnsureUseScope())
            {
                lazy.ResolveLazy<ITaskService>().ShouldBeSameAs(outer);
            }

            lazy.ResolveLazy<ITaskService>().ShouldBeSameAs(outer);
        }
    }

    [Fact]
    public void EnsureUseScope_ShouldOpenAScope_When_NoneIsOpen()
    {
        using var provider = BuildProvider(ServiceLifetime.Transient);

        ResolverScope.Current.ShouldBeNull();

        using (provider.EnsureUseScope(out var resolver))
        {
            ResolverScope.Current.ShouldNotBeNull();

            resolver.Resolve<ITaskService>().ShouldNotBeNull();
        }

        ResolverScope.Current.ShouldBeNull();
    }
}
