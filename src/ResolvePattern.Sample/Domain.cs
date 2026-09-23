namespace ResolvePattern.Sample;

// A deliberately cyclic domain: a Project knows its Tasks, and a Task knows its Project.
//
//     IProjectService  ──needs──▶  ITaskService
//            ▲                          │
//            └──────────needs───────────┘
//
// Constructor injection cannot resolve this graph — building either service requires the other to
// already exist. The usual escapes are ugly: split the services artificially, wrap every ctor
// parameter in Lazy<T>, or eagerly build the whole graph. With property resolution there is no
// cycle at construction time, because nothing is resolved until a property is first read.

public interface ITaskService
{
    string Describe(int taskId);
}

public interface IProjectService
{
    string Name { get; }

    string SummarizeTasks();
}

public class TaskService : ServiceBase, ITaskService
{
    // Lazy, cached collaborator. No constructor involved.
    private IProjectService Project => Resolve<IProjectService>();

    public string Describe(int taskId) => $"Task {taskId} in project '{Project.Name}'";
}

public class ProjectService : ServiceBase, IProjectService
{
    // The other half of the cycle.
    private ITaskService Tasks => Resolve<ITaskService>();

    public string Name => "Apollo";

    public string SummarizeTasks() => $"{Name}: " + string.Join(", ", new[] { 1, 2 }.Select(Tasks.Describe));
}
