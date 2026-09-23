namespace ResolvePattern.Tests;

public class ResolverTests : TestBase
{
    [Fact]
    public void Resolve_ShouldWalkTheCycle_When_CollaboratorsDependOnEachOther()
    {
        using var provider = BuildProvider();
        using (provider.UseScope(out var resolver))
        {
            var projectService = resolver.Resolve<IProjectService>();

            var result = projectService.SummarizeTasks();

            result.ShouldBe("Apollo: Task 1 in project 'Apollo', Task 2 in project 'Apollo'");
        }
    }

    [Fact]
    public void Resolve_ShouldReturnANewInstance_When_TheServiceIsRegisteredTransient()
    {
        using var provider = BuildProvider(ServiceLifetime.Transient);
        using (provider.UseScope(out var resolver))
        {
            resolver.Resolve<ITaskService>().ShouldNotBeSameAs(resolver.Resolve<ITaskService>());
        }
    }

    [Fact]
    public void Resolve_ShouldReturnTheSameInstance_When_TheServiceIsRegisteredScoped()
    {
        using var provider = BuildProvider(ServiceLifetime.Scoped);
        using (provider.UseScope(out var resolver))
        {
            resolver.Resolve<ITaskService>().ShouldBeSameAs(resolver.Resolve<ITaskService>());
        }
    }

    [Fact]
    public void ResolveLazy_ShouldReturnTheCachedInstance_When_TheServiceIsRegisteredTransient()
    {
        using var provider = BuildProvider(ServiceLifetime.Transient);
        var lazy = provider.GetRequiredService<ILazyResolver>();

        using (provider.UseScope())
        {
            lazy.ResolveLazy<ITaskService>().ShouldBeSameAs(lazy.ResolveLazy<ITaskService>());
        }
    }

    [Fact]
    public void ResolveLazy_ShouldReturnDifferentInstances_When_CalledFromSeparateScopes()
    {
        using var provider = BuildProvider(ServiceLifetime.Transient);
        var lazy = provider.GetRequiredService<ILazyResolver>();

        ITaskService first, second;

        using (provider.UseScope()) first = lazy.ResolveLazy<ITaskService>();
        using (provider.UseScope()) second = lazy.ResolveLazy<ITaskService>();

        second.ShouldNotBeSameAs(first);
    }

    [Fact]
    public void Resolve_ShouldReturnTheSameInstance_When_CalledThroughTheRegisteredAndTheScopeResolver()
    {
        // Scoped registration: both resolvers must be looking at the same scope to agree.
        using var provider = BuildProvider(ServiceLifetime.Scoped);
        var registered = provider.GetRequiredService<IResolver>();

        using (provider.UseScope(out var resolver))
        {
            registered.Resolve<ITaskService>().ShouldBeSameAs(resolver.Resolve<ITaskService>());
        }
    }

    [Fact]
    public void Resolve_ShouldThrow_When_NoScopeIsOpen()
    {
        using var provider = BuildProvider();

        var resolver = provider.GetRequiredService<IResolver>();

        var act = () => resolver.Resolve<ITaskService>();

        var ex = act.ShouldThrow<InvalidOperationException>();

        ex.Message.ShouldContain("UseScope");
    }

    [Fact]
    public void GetService_ShouldForwardToTheCurrentScope_When_CalledOnTheAmbientResolver()
    {
        // The untyped IServiceProvider path: scoped registration, so agreeing on the instance means
        // agreeing on the scope.
        using var provider = BuildProvider(ServiceLifetime.Scoped);
        var ambient = provider.GetRequiredService<IResolver>();

        using (provider.UseScope(out var resolver))
        {
            ambient.GetService(typeof(ITaskService)).ShouldBeSameAs(resolver.Resolve<ITaskService>());
        }
    }
}
