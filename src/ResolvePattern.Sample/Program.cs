using Microsoft.Extensions.DependencyInjection;

namespace ResolvePattern.Sample;

public static class Program
{
    public static void Main()
    {
        // 1. Register services as normal. Note the cycle: ProjectService <-> TaskService.
        //    AddResolved assigns each service its resolver at construction.
        var services = new ServiceCollection()
            .AddResolved<IProjectService, ProjectService>()
            .AddResolved<ITaskService, TaskService>();

        var provider = services.BuildServiceProvider();

        // 2. Open an ambient scope at the entry point, which hands back the resolver to start from.
        using (provider.UseScope(out var resolver))
        {
            var project = resolver.Resolve<IProjectService>();

            // This call walks the cycle (Project -> Task -> Project) and resolves it lazily.
            Console.WriteLine(project.SummarizeTasks());
            // -> Apollo: Task 1 in project 'Apollo', Task 2 in project 'Apollo'
        }
    }
}
