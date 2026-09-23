# The `Resolve<T>` pattern

[![CI](https://github.com/sovist/resolve-pattern/actions/workflows/ci.yml/badge.svg)](https://github.com/sovist/resolve-pattern/actions/workflows/ci.yml)

```
A circular dependency was detected for the service of type 'IProjectService'.
IProjectService(ProjectService) -> ITaskService(TaskService) -> IProjectService
```

Landed here from that error? Usually it means the design is wrong and the cycle should be broken — and if it is one cycle, `Lazy<T>` on one constructor parameter breaks it. Go do that. Sometimes, though, the domain is genuinely a graph, no refactor will make it a tree, and the cycle is only the first symptom of something `Lazy<T>` can't touch: the constructor you'll have in year five. **This repo is for that case, and only that case.**

**Constructor injection is the right default — until your application gets old enough and tangled enough that it isn't. This repo is a small, runnable reference for the alternative that has run in production for ~8 years: resolve collaborators lazily from an ambient scope instead of injecting them through constructors.**

It is deliberately tiny (four files of infrastructure, no framework dependency beyond `Microsoft.Extensions.DependencyInjection`) so you can read the whole thing in ten minutes and decide for yourself.

> **Read this first:** this pattern is for *cohesive internal applications*, not for libraries, and it only pays off under specific conditions. The [When **not** to use this](#when-not-to-use-this) section is not a disclaimer — it's half the argument. If you skip it you'll misapply the idea.

---

## The problem it solves

There's a pull request that shows up in every long-lived service. It adds one parameter to a constructor:

```csharp
public TaskAppService(
    ITaskRepository taskRepository,
    ITaskManager taskManager,
    IProjectManager projectManager,
+   ITaskHistoryManager taskHistoryManager,
    /* ... */)
```

Small diff. Passes review in thirty seconds. The service genuinely needs the collaborator, and constructor injection is *the* recommended way to take a dependency. No single instance of this PR is ever wrong.

But the pressure is monotonic — dependencies get added, essentially never removed — and it compounds:

| Year | State |
|---|---|
| 1 | 3–5 ctor params. Clean. The textbook is vindicated. |
| 3 | 8–12 params. Feels heavy. "We should refactor." Not this sprint. |
| 5 | 15+ params, several wrapped in `Lazy<T>` to break cycles. Every feature touches a ctor. Every test opens with a page of mock setup. |
| 7 | You are writing workarounds *for your DI container*. |

The root cause is structural, not cultural: **constructor injection has no natural resistance to dependency accumulation.** The friction to add a dependency is ~zero and stays ~zero whether it's dep #3 or #30. The constructor is supposed to be the checkpoint where you notice a class does too much — but "too many params" is a smell everyone learns to tolerate, so the checkpoint never fires until it's a crisis.

### The accelerant: graph-shaped domains

Constructor injection assumes your dependency graph is a *DAG* — it resolves depth-first at construction time, so `A → B → A` simply fails to construct. Many domains can pretend they're tree-shaped. Some can't:

```
Task ↔ Project ↔ Resource ↔ Permissions ↔ Task
```

When ctor injection meets a real cycle you get three escapes:

1. **Split services artificially** to break a cycle the domain doesn't want broken — accidental complexity manufactured to satisfy a tool.
2. **Wrap the offending parameter in `Lazy<T>`** — which works, and for a single cycle is the right fix. It keeps the signature explicit; it only makes it noisier. What it does nothing about is the fifteen-parameter constructor the cycle lives in — and that constructor, not the cycle, is the disease.
3. **Eagerly construct the whole graph per request** — a request touching 3 services pays to build 30.

The graph domain is where this shows up *first and clearest*. Tree-shaped domains hit the same wall later and call it "15-param constructor" or "60 lines of mock setup per test." Same disease, slower onset.

---

## The idea: resolve, don't inject

Collaborators become **lazily-resolved, per-scope-cached properties** on a shared base class. There is no constructor.

```csharp
public class TaskService : ServiceBase, ITaskService
{
    private IProjectService Project => Resolve<IProjectService>();   // lazy, cached for the scope

    public string Describe(int taskId) => $"Task {taskId} in project '{Project.Name}'";
}

public class ProjectService : ServiceBase, IProjectService
{
    private ITaskService Tasks => Resolve<ITaskService>();           // the other half of the cycle

    public string Name => "Apollo";
    public string SummarizeTasks() => $"{Name}: " + string.Join(", ", new[] { 1, 2 }.Select(Tasks.Describe));
}
```

`ProjectService` and `TaskService` depend on **each other**. Constructor injection cannot build this graph. Property resolution doesn't care — nothing is resolved until a property is first read, which is well after every object exists. ([`Domain.cs`](src/ResolvePattern.Sample/Domain.cs), [`Program.cs`](src/ResolvePattern.Sample/Program.cs))

What the one change in shape buys:

- **No construction-time cycles.** The cycle resolves lazily at *use* time, exactly as it would if you'd newed the objects by hand.
- **No pressure toward bloat.** Adding dep #30 is one property — identical cost and visibility to dep #3. No constructor to bloat means no false "ctor is getting long" signal everyone learns to ignore.
- **Pay for what you use.** A request that touches 3 collaborators instantiates 3, not the 30 reachable in the graph.
- **Uniform shape.** Every service looks the same in every module, written in any year. One pattern to learn.
- **Refactor-safe tests** (see below).

### Not for you if

Now that you've seen the shape, the three cases where it is the wrong tool:

- **You're writing a library.** Your consumers can't see your container, so hiding dependencies genuinely harms them. The anti-pattern rule applies at full force — use constructor injection.
- **Your team won't apply it everywhere.** The safety comes from uniformity. A codebase that is 60% constructor injection, 20% property resolution and 10% `GetService()` calls is worse than any one of those applied consistently.
- **Your domain is a tree and your services are small.** No cycles and 5-param constructors three years in? You may never hit the decay curve hard enough to justify swimming against the ecosystem's defaults.

The longer version, with the trade-offs you take on even when it *is* the right tool, is in [When **not** to use this](#when-not-to-use-this).

---

## How it works

Four small files, no magic:

| File | Role |
|---|---|
| [`ResolverScope.cs`](src/ResolvePattern/ResolverScope.cs) | The ambient scope. Lives in `AsyncLocal`, wraps an `IServiceScope`. `Resolve<T>()` defers to the container's lifetimes; `ResolveLazy<T>()` **memoizes for the life of the scope**. Supports nesting. |
| [`ServiceBase.cs`](src/ResolvePattern/ServiceBase.cs) | Base class exposing `protected Resolve<T>()`, which is `Resolver.ResolveLazy<T>()` — cached for the scope. That default is the one policy the base class adds. |
| [`Resolver.cs`](src/ResolvePattern/Resolver.cs) | `IResolver` — container-lifetime resolution — and `ILazyResolver`, which adds the memoizing `ResolveLazy<T>()`. `AmbientResolver` implements both by forwarding to the current scope. No static facade. |
| [`ServiceProviderExtensions.cs`](src/ResolvePattern/ServiceProviderExtensions.cs) | `UseScope(out var resolver)` opens a scope and hands back its resolver, `EnsureUseScope()` opens one only if none is open, and `AddResolved<TService, TImpl>()` registers a service and wires it at construction. |

`Resolve<T>()` requires a current scope, and every entry point opens one — worker, job, migration, test, and web request alike:

```csharp
using (serviceProvider.UseScope(out var resolver))
{
    var svc = resolver.Resolve<IMyService>();   // and Resolve<T>() works anywhere inside the block
}
```

Resolve outside a scope and you get a fast, explicit failure — not a confusing null:

```
InvalidOperationException: There is no current resolver scope.
Wrap the entry point in serviceProvider.UseScope().
```

### ASP.NET Core: open the scope in middleware

A request scope in ASP.NET Core is an `IServiceScope`. That is *not* an ambient resolver scope — `Resolve<T>()` inside a request still throws unless you open one:

```csharp
public sealed class ResolverScopeMiddleware(RequestDelegate next)
{
    public async Task Invoke(HttpContext context)
    {
        using (context.RequestServices.UseScope())
        {
            await next(context);
        }
    }
}

// Program.cs
app.UseMiddleware<ResolverScopeMiddleware>();
```

Six lines, deliberately not shipped in the library: it would put an ASP.NET Core dependency on four files that otherwise need nothing but `Microsoft.Extensions.DependencyInjection`.

### Reentrancy: `EnsureUseScope()`

Some code is reachable both from its own entry point and from inside someone else's scope — a job a request can also invoke inline, say. Calling `UseScope()` there nests a second scope with its own cache, so the same `Resolve<T>()` returns a different instance inside than outside. `EnsureUseScope()` opens a scope only when there isn't one:

```csharp
using (serviceProvider.EnsureUseScope())   // no-op if a scope is already open
{
    // ...
}
```

### There is no static resolver

Nothing here reaches the container through a static. Entry points get their resolver from the scope they open; services get theirs assigned when the container constructs them:

```csharp
services.AddResolved<ITaskService, TaskService>();             // AddScoped, plus: wire the resolver at construction

public class Job(IResolver resolver)                           // or just take it, like any dependency
{
    public void Run() => resolver.Resolve<ITaskService>().Describe(1);
}
```

`AddResolved` is property injection, hand-rolled: it registers the implementation with a factory that constructs the instance and assigns its resolver in the same step. Because the assignment happens *at construction*, every path to the instance is covered — resolved through the pattern, built by the container as a controller's constructor dependency, or pulled from a raw `IServiceProvider`. Microsoft.Extensions.DependencyInjection has no property injection of its own; Autofac's `PropertiesAutowired` is the usual answer, and this is one visible line per service instead of a container. Register a `ServiceBase` any other way and it fails fast at first use, naming the fix — and the smoke test below catches it before then.

That `ServiceBase.Resolver` is a settable property gives a second test seam: assign a substitute and exercise a service with no container and no scope (see [`SubstitutionTests.cs`](tests/ResolvePattern.Tests/SubstitutionTests.cs)).

### Cached by default on the service, honest to the container on the resolver

Same name, two layers — worth being explicit about, because you meet both in the first snippet:

| Call | Behavior |
|---|---|
| `resolver.Resolve<T>()` on an `IResolver` | **Whatever the registration says.** Transient is new each call, scoped is shared within the scope. The resolver never overrides your container. |
| `resolver.ResolveLazy<T>()` on an `ILazyResolver` | **Memoized for the scope**, regardless of registered lifetime. |
| `Resolve<T>()` inside a `ServiceBase` | `Resolver.ResolveLazy<T>()` — the cached one. The 99% path for collaborators. |

The cached default is centralized in the base class, so the decision doesn't live at hundreds of call sites. When a service genuinely wants the container's lifetime instead, it writes `Resolver.Resolve<T>()` in full — and that longer spelling is the review signal: "why are we bypassing the cache here?"

### Tests use the exact same machinery

No parallel test DI philosophy. Substitution happens **once at the registration layer**; production code keeps calling `Resolve<T>()` and transparently gets the fake. A service growing a collaborator doesn't ripple into existing tests. ([`SubstitutionTests.cs`](tests/ResolvePattern.Tests/SubstitutionTests.cs))

```csharp
services.AddResolved<IProjectService, ProjectService>();
services.AddResolved<ITaskService, TaskService>();
services.AddScoped<ITaskService, FakeTaskService>();   // override one registration; last wins

using (provider.UseScope(out var resolver))
{
    // ProjectService is real; it resolves ITaskService and gets the fake.
    var projectService = resolver.Resolve<IProjectService>();

    var summary = projectService.SummarizeTasks();
}
```

This is the same property that survives domain refactors without production-side churn: lazy resolution keeps the contract narrow ("this service needs a container") instead of wide ("this service needs these 12 specific types"). One decision, two payoffs.

---

## Run it

```bash
dotnet test                                   # 6 tests: cycles, caching, fresh, scope errors, substitution
dotnet run --project src/ResolvePattern.Sample
# -> Apollo: Task 1 in project 'Apollo', Task 2 in project 'Apollo'
```

Requires the .NET 10 SDK (or newer).

---

## "Isn't this the service locator anti-pattern?"

Short answer: yes — and the rule is being misapplied if you reach for it here.

The canonical "service locator is an anti-pattern" argument was written for **libraries**: code consumed by strangers who can't see your DI setup. Hiding a library's dependencies behind a locator genuinely harms its consumer, who has no way to learn what's needed except by running it and watching it throw. That advice is correct, and you should follow it in a library.

Inside a **cohesive internal application** where every service inherits one base class and one team owns the whole graph, the premise inverts. There are no strangers. `Resolve<ITaskManager>()` on a base class is no more a "hidden dependency" than `this.HttpContext` on an MVC controller or an `@Autowired` field on a Spring bean — it's an ambient framework contract you opted into. **The pattern *is* the contract.**

It is also not reached statically. `IResolver` is an ordinary injectable interface: entry points receive one from the scope they open, services are assigned one at registration, and any class that wants the dependency visible in its signature can take it as a constructor parameter. That does not make it stop being a locator — it makes it a locator you can substitute, and one that no longer hides behind a global.

There's a precedent, and it cuts both ways. The Java enterprise world hit this decay curve ~15 years earlier under the same domain pressures, and a large share of Spring code answered it with `@Autowired` on fields — collaborators found by the framework, no constructor. The Spring team's own guidance says the opposite: prefer constructor injection, treat field injection as a smell. Both facts are true, and the disagreement is this README's argument in miniature. The practice drifted toward ambient resolution because the pressure is real; the guidance resists it because, without a team holding the line, it degrades into hidden dependencies nobody owns. That is why the uniformity condition in [When **not** to use this](#when-not-to-use-this) is not optional.

---

## When **not** to use this

This is a narrow tool. Outside these conditions, use constructor injection.

- **Don't do this in a library.** If consumers can't see your container, the original anti-pattern rule applies at full force. Make dependencies explicit. The whole argument is conditioned on "cohesive internal product."
- **Don't do this without a shared base class and a team that holds the line.** The safety comes from *uniformity*: every service the same way, a bare `Resolver.Resolve<T>()` inside one a flagged exception. A codebase that's 60% ctor injection, 20% property resolution, 10% `IServiceProvider.GetService()` is worse than any of those applied consistently. If your team won't commit everywhere, pick something else and apply *it* everywhere.
- **Don't do this if your domain is genuinely a tree and your services are small.** Three years in with 5-param constructors and no cycles? You may never hit the decay curve hard enough to justify swimming against the ecosystem's defaults. Being unconventional has real costs — onboarding, tooling, this very conversation forever.
- **Know the trade-offs even when it's right.** You give up compile-time "this dependency is missing" errors for a failure at first use. The mitigation is mechanical, because every collaborator is a property on a `ServiceBase`: a smoke test opens a scope, resolves every registration and reads every property, so a missing registration fails in CI rather than in production ([`RegistrationSmokeTests.cs`](tests/ResolvePattern.Tests/RegistrationSmokeTests.cs)). You take on an ambient scope that must be opened at every non-request entry point; forgetting is a runtime error (with a clear message), not a compile error. Manageable, not free.

The real lesson generalizes past DI: **the cheapest decision and the correct decision are not the same, and the gap is invisible at the moment you choose.** "Add one more constructor parameter" wins every PR review and loses the decade.

---

## License

MIT — see [LICENSE](LICENSE).
