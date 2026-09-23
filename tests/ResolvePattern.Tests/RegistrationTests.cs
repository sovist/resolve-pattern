namespace ResolvePattern.Tests;

/// <summary>
/// A <see cref="ServiceBase"/> must be wired however it is reached — through the pattern, as a
/// constructor dependency the container builds internally, or from a raw provider. Injection
/// therefore has to happen where construction happens, which is what <c>AddResolved</c> does by
/// assigning the resolver inside the implementation's own factory.
/// </summary>
public class RegistrationTests : TestBase
{
    /// <summary>A consumer that takes a service the ordinary way — what every controller does.</summary>
    private sealed class Report(ITaskService tasks)
    {
        public string Render() => tasks.Describe(1);
    }

    [Fact]
    public void AddResolved_ShouldInjectTheResolver_When_TheServiceIsAConstructorDependency()
    {
        // Resolved FIRST in a fresh scope: nothing has touched ITaskService through the pattern yet,
        // so the container constructs TaskService itself to satisfy Report.
        using var provider = BuildServices().AddScoped<Report>().BuildServiceProvider();
        using (provider.UseScope(out var resolver))
        {
            var report = resolver.Resolve<Report>();

            report.Render().ShouldBe("Task 1 in project 'Apollo'");
        }
    }

    [Fact]
    public void AddResolved_ShouldInjectTheResolver_When_ResolvedFromTheRawProvider()
    {
        using var provider = BuildProvider();
        using (provider.UseScope())
        {
            var tasks = provider.GetRequiredService<ITaskService>();

            tasks.Describe(1).ShouldBe("Task 1 in project 'Apollo'");
        }
    }

    [Fact]
    public void Resolve_ShouldThrowNamingTheFix_When_TheServiceWasRegisteredWithAddScopedInstead()
    {
        // The habit slip: a ServiceBase registered like any other service is not wired. It must fail
        // fast and point at AddResolved — and the registration smoke test catches it before that.
        var services = new ServiceCollection()
            .AddResolved<IProjectService, ProjectService>()
            .AddScoped<ITaskService, TaskService>();

        using var provider = services.BuildServiceProvider();
        using (provider.UseScope(out var resolver))
        {
            var act = () => resolver.Resolve<ITaskService>().Describe(1);

            var ex = act.ShouldThrow<InvalidOperationException>();

            ex.Message.ShouldContain("AddResolved");
        }
    }
}