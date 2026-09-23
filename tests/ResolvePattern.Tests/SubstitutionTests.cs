namespace ResolvePattern.Tests;

/// <summary>
/// Tests use the same resolution machinery as production code. Substituting a dependency happens
/// once at the registration layer — production code keeps calling <c>Resolve&lt;T&gt;()</c> and
/// transparently receives the fake. There is no per-test constructor or mock-setup ceremony, and a
/// service growing a new collaborator does not ripple into existing tests.
/// </summary>
public class SubstitutionTests : TestBase
{
    private sealed class FakeTaskService : ITaskService
    {
        public string Describe(int taskId) => $"[fake task {taskId}]";
    }

    private sealed class FakeProjectService : IProjectService
    {
        public string Name => "Gemini";

        public string SummarizeTasks() => "[fake summary]";
    }

    /// <summary>An ILazyResolver that answers from a fixed map — no container, no ambient scope.</summary>
    private sealed class StubResolver : ILazyResolver
    {
        private readonly Dictionary<Type, object> _services = new();

        public StubResolver Add<T>(T service) where T : notnull
        {
            _services[typeof(T)] = service;

            return this;
        }

        public T Resolve<T>() where T : notnull => (T)_services[typeof(T)];

        public T ResolveLazy<T>() where T : notnull => Resolve<T>();

        public object? GetService(Type serviceType) => _services.GetValueOrDefault(serviceType);
    }

    [Fact]
    public void Resolve_ShouldReturnTheSubstitute_When_ARegistrationIsOverridden()
    {
        // Override one registration. Last registration wins for GetRequiredService<T>.
        var services = BuildServices().AddScoped<ITaskService, FakeTaskService>();

        using var provider = services.BuildServiceProvider();
        using (provider.UseScope(out var resolver))
        {
            // ProjectService is the real service; it resolves ITaskService and gets the fake.
            var projectService = resolver.Resolve<IProjectService>();

            var summary = projectService.SummarizeTasks();

            summary.ShouldBe("Apollo: [fake task 1], [fake task 2]");
        }
    }

    [Fact]
    public void Resolve_ShouldUseTheAssignedResolver_When_NoScopeIsOpen()
    {
        // The other seam: assign the resolver directly.
        // No container is built and no scope is opened, so this is a plain unit test of one class.
        var taskService = new TaskService
        {
            Resolver = new StubResolver().Add<IProjectService>(new FakeProjectService()),
        };

        ResolverScope.Current.ShouldBeNull();

        taskService.Describe(7).ShouldBe("Task 7 in project 'Gemini'");
    }

    [Fact]
    public void Resolve_ShouldThrow_When_NoResolverIsAssigned()
    {
        // Constructed by hand and never assigned: the failure names the fix.
        var taskService = new TaskService();

        var act = () => taskService.Describe(7);

        var ex = act.ShouldThrow<InvalidOperationException>();

        ex.Message.ShouldContain("AddResolved");
    }
}