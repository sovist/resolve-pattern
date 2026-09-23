namespace ResolvePattern.Tests;

public class ScopeDisposalTests : TestBase
{
    [Fact]
    public async Task ResolveLazy_ShouldThrow_When_CalledFromAFlowThatOutlivedItsScope()
    {
        using var provider = BuildProvider();

        var ambient = provider.GetRequiredService<ILazyResolver>();

        var scopeEnded = new TaskCompletionSource();
        Task lateResolve;

        using (provider.UseScope())
        {
            // Prime the per-scope cache: the dangerous path is a cache HIT after disposal, which
            // would return the already-disposed instance without the container ever being asked.
            ambient.ResolveLazy<ITaskService>();

            // Forked inside the scope, so the AsyncLocal still points at it; completes after.
            lateResolve = Task.Run(async () =>
            {
                await scopeEnded.Task;

                var act = () => ambient.ResolveLazy<ITaskService>();

                act.ShouldThrow<ObjectDisposedException>();
            });
        }

        scopeEnded.SetResult();

        await lateResolve;
    }

    [Fact]
    public void Resolve_ShouldThrow_When_TheScopeIsDisposed()
    {
        using var provider = BuildProvider();

        provider.UseScope(out var resolver).Dispose();

        var act = () => resolver.Resolve<ITaskService>();

        act.ShouldThrow<ObjectDisposedException>();
    }

    [Fact]
    public void ResolveLazy_ShouldThrow_When_TheScopeIsDisposed()
    {
        using var provider = BuildProvider();

        var scope = ResolverScope.Begin(provider);
        scope.Dispose();

        var act = () => scope.ResolveLazy<ITaskService>();

        act.ShouldThrow<ObjectDisposedException>();
    }

    [Fact]
    public void GetService_ShouldThrow_When_TheScopeIsDisposed()
    {
        using var provider = BuildProvider();

        provider.UseScope(out var resolver).Dispose();

        var act = () => resolver.GetService(typeof(ITaskService));

        act.ShouldThrow<ObjectDisposedException>();
    }

    [Fact]
    public void Dispose_ShouldBeIdempotent_When_CalledTwice()
    {
        // A scope in a using block that is also disposed by hand must not throw or double-restore.
        using var provider = BuildProvider();

        var scope = ResolverScope.Begin(provider);
        scope.Dispose();

        var act = () => scope.Dispose();

        act.ShouldNotThrow();

        ResolverScope.Current.ShouldBeNull();
    }
}
