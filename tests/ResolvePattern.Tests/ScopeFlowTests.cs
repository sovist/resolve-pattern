namespace ResolvePattern.Tests;

/// <summary>
/// A service belongs to the flow it is called from, not to the scope that constructed it: its
/// <c>Resolve&lt;T&gt;()</c> answers from whichever scope is ambient at the moment of the call.
///
/// That is what lets one singleton serve many concurrent scopes, each getting its own collaborators — and what a
/// resolver bound to the constructing scope cannot do, since every caller would then share that one scope.
/// </summary>
public class ScopeFlowTests : TestBase
{
    /// <summary>
    /// Per-scope state: a unit of work, a DbContext, a user session.
    /// </summary>
    private sealed class LaneState;

    private interface ILaneWorker
    {
        LaneState State { get; }
    }

    private sealed class LaneWorker : ServiceBase, ILaneWorker
    {
        public LaneState State => Resolve<LaneState>();
    }

    [Fact]
    public async Task Resolve_ShouldAnswerFromEachLanesOwnScope_When_ASingletonServesConcurrentScopes()
    {
        // The background-worker shape: one singleton, N parallel lanes, a scope per lane.
        const int lanes = 8;

        var services = new ServiceCollection()
            .AddScoped<LaneState>()
            .AddResolved<ILaneWorker, LaneWorker>(ServiceLifetime.Singleton);

        using var provider = services.BuildServiceProvider();

        var worker = provider.GetRequiredService<ILaneWorker>();

        // Every lane waits until all are open, so the scopes genuinely overlap.
        var opened = 0;
        var allOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var seen = await Task.WhenAll(Enumerable.Range(0, lanes).Select(_ => Task.Run(async () =>
        {
            using (provider.UseScope(out var resolver))
            {
                if (Interlocked.Increment(ref opened) == lanes)
                {
                    allOpen.SetResult();
                }

                await allOpen.Task;

                var state = worker.State;

                state.ShouldBeSameAs(resolver.Resolve<LaneState>());

                return state;
            }
        })));

        seen.Distinct().Count().ShouldBe(lanes);
    }

    [Fact]
    public void Resolve_ShouldAnswerFromTheNestedScope_When_AServiceFromTheOuterScopeIsCalledInsideIt()
    {
        // The same rule for a scoped service: constructed in the outer scope, called in the inner one,
        // it resolves from the inner one. EnsureUseScope exists for when that is not what you want.
        using var provider = BuildProvider(ServiceLifetime.Transient);
        using (provider.UseScope(out var outer))
        {
            var project = (ProjectService)outer.Resolve<IProjectService>();
            var outerTasks = project.Resolver.ResolveLazy<ITaskService>();

            using (provider.UseScope())
            {
                project.Resolver.ResolveLazy<ITaskService>().ShouldNotBeSameAs(outerTasks);
            }
        }
    }

    [Fact]
    public async Task RunDetached_ShouldStartWithNoScope_When_CalledInsideOne()
    {
        using var provider = BuildProvider();
        using (provider.UseScope())
        {
            ResolverScope? seen = null;

            await ResolverScope.RunDetached(() =>
            {
                seen = ResolverScope.Current;

                return Task.CompletedTask;
            });

            seen.ShouldBeNull();

            ResolverScope.Current.ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task RunInOwnScope_ShouldOutliveTheCallersScope_When_StartedInsideIt()
    {
        // Fire-and-forget from inside a scope that ends first. Forked the ordinary way,
        // the work would resolve from the dead scope and throw ObjectDisposedException.
        using var provider = BuildProvider();

        var callerEnded = new TaskCompletionSource();
        ITaskService callers;
        ITaskService? owned = null;
        Task work;

        using (provider.UseScope(out var caller))
        {
            callers = caller.Resolve<ITaskService>();

            // Passing the caller's own resolver, which will be dead by the time the work starts.
            work = caller.RunInOwnScope(async own =>
            {
                await callerEnded.Task;

                owned = own.Resolve<ITaskService>();

                owned.Describe(1).ShouldBe("Task 1 in project 'Apollo'");
            });
        }

        callerEnded.SetResult();

        await work;

        owned.ShouldNotBeNull().ShouldNotBeSameAs(callers);
    }
}
