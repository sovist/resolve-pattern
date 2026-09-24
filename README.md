# The `Resolve<T>` pattern

[![CI](https://github.com/sovist/resolve-pattern/actions/workflows/ci.yml/badge.svg)](https://github.com/sovist/resolve-pattern/actions/workflows/ci.yml)

```
A circular dependency was detected for the service of type 'IProjectService'.
IProjectService(ProjectService) -> ITaskService(TaskService) -> IProjectService
```

Landed here from that error? Usually it means the design is wrong and the cycle should be broken — and if it is one cycle, `Lazy<T>` on one constructor parameter breaks it. Go do that. Sometimes, though, the domain is genuinely a graph, no refactor will make it a tree, and the cycle is only the first symptom of something `Lazy<T>` can't touch: the constructor you'll have in year five. **This repo is for that case.**

**Constructor injection is the right default — until your application gets old enough and tangled enough that it isn't. This repo is a small, runnable reference for the alternative that has run in production for ~8 years: resolve collaborators lazily from an ambient scope instead of injecting them through constructors.**

*Scope* here means what it means to the container — an `IServiceScope`, the unit of work that a request, a job run, a parallel lane or a test opens and later disposes — with one addition: there is always a *current* one, it travels with the async flow, and every `Resolve<T>()` answers from it. Collaborators are cached per scope: the same property, read from two scopes, is resolved once in each.

Cycles and constructor bloat are where the pain shows first, and where this README starts. The reason the pattern earns its keep is something else, and constructor injection has no answer to it: an existing object can be moved into a new scope — a parallel lane, a post-commit hook, a test — and its collaborators follow, with no change to its code. That is what [A service belongs to the flow](#a-service-belongs-to-the-flow-not-to-the-scope-that-built-it) is about.

> **Read this first:** this pattern is for *cohesive internal applications*, not for libraries, and it only pays off under specific conditions. The [When **not** to use this](#when-not-to-use-this) section is not a disclaimer — it's half the argument. If you skip it you'll misapply the idea. The repo is deliberately tiny — four files of infrastructure, no dependency beyond `Microsoft.Extensions.DependencyInjection` — so you can read the whole thing in ten minutes and decide for yourself.

---

## The problem it solves

There's a pull request that shows up in every long-lived service. It adds one parameter to a constructor:

```csharp
public TaskBoardService(
    ITaskStore taskStore,
    ITaskPlanner taskPlanner,
    IProjectCatalog projectCatalog,
+   ITaskAuditTrail taskAuditTrail,
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

- **Isolation is a call-site decision.** Wrap any call in `UseScope()` and everything inside it — the service, its collaborators, theirs — runs against fresh scoped state, with no change to the code inside. An existing object can be re-homed into a new scope; with constructor injection its dependencies are fixed at construction. It is the one item on this list constructor injection has no answer to ([why](#a-service-belongs-to-the-flow-not-to-the-scope-that-built-it)), and the one that leans hardest on [the one rule](#the-one-rule).
- **No construction-time cycles.** The cycle resolves lazily at *use* time, exactly as it would if you'd newed the objects by hand.
- **No friction from bloat.** Adding dep #30 is one property — identical cost and visibility to dep #3. That cuts both ways; see [What it does not fix](#what-it-does-not-fix-bloat).
- **Pay for what you use.** A request that touches 3 collaborators instantiates 3, not the 30 reachable in the graph.
- **Uniform shape.** Every service looks the same in every module, written in any year. One pattern to learn.
- **Refactor-safe tests** (see below).

### The one rule

Every collaborator is a `Resolve<T>()` in a property getter. Never store the result in a field. A `Resolve<T>()` inside a method body is allowed — a collaborator only one branch needs, say — but it is the exception, not the shape, and the smoke test cannot see it:

```csharp
private IProjectService Project => Resolve<IProjectService>();               // yes: per call, so per scope
private IProjectService Project => _project ??= Resolve<IProjectService>();  // no: pins the first scope's instance to the object
```

The second looks like an optimisation and is a bug: the cache it adds already exists, per scope, inside the resolver — and a field on a singleton is shared by every scope that singleton ever serves. Three things in this README lean on the rule, and none of them works without it: the smoke test that turns a missing registration into a CI failure reads every property, so a collaborator resolved in a method body is invisible to it; the isolation above holds only for state reached through `Resolve<T>()`; and the size check that replaces the constructor counts properties. The pattern cannot enforce the rule — but it is one shape, so a Roslyn analyzer or an architecture test can, cheaply.

### What it does not fix: bloat

Be clear about which half of the problem this solves. It removes the *pain* of a service with thirty collaborators — the constructor, the cycle workarounds, the page of mock setup. It does nothing about the service *having* thirty: the thirtieth property is one line, the same as the third, so most services stay small while a handful keep growing and nothing stops them. A thirty-parameter constructor would have forced a conversation long before; thirty one-line properties grow without anyone noticing.

The constructor was a checkpoint everyone learned to ignore, and this pattern removes it rather than repairing it. Put one back on purpose, one that cannot be tolerated into silence: count the collaborator properties per service in CI — [the one rule](#the-one-rule) is what makes that count *mean* collaborators — and fail above a threshold you choose, with an allow-list for the few services you have decided may be large. The pattern makes a large service cheap to *live with*; it must not make it free to *become*.

### Not for you if

Now that you've seen the shape, the four cases where it is the wrong tool — each expanded, with the trade-offs you take on even when it *is* the right tool, in [When **not** to use this](#when-not-to-use-this):

- **You're writing a library.** Consumers can't see your container; hidden dependencies genuinely harm them.
- **Your team won't apply it everywhere.** The safety comes from uniformity; a mixed codebase is worse than either pure one.
- **Your domain is a tree and your services are small.** You may never hit the decay curve hard enough to justify being unconventional.
- **Your code runs in exactly one context.** Request-scoped, no fan-out, no post-commit hooks: you would never use the one thing this does that constructor injection can't, and you would pay the ambient-state cost for nothing.

---

## How it works

Four small files, no magic:

| File | Role |
|---|---|
| [`ResolverScope.cs`](src/ResolvePattern/ResolverScope.cs) | The ambient scope. Lives in `AsyncLocal`, wraps an `IServiceScope`. `Resolve<T>()` defers to the container's lifetimes; `ResolveLazy<T>()` **memoizes for the life of the scope**. Supports nesting. `RunDetached()` starts work with no ambient scope. |
| [`ServiceBase.cs`](src/ResolvePattern/ServiceBase.cs) | Base class exposing `protected Resolve<T>()`, which is `Resolver.ResolveLazy<T>()` — cached for the scope. That default is the one policy the base class adds. |
| [`Resolver.cs`](src/ResolvePattern/Resolver.cs) | `IResolver` — container-lifetime resolution — and `ILazyResolver`, which adds the memoizing `ResolveLazy<T>()`. `AmbientResolver` implements both by forwarding to the current scope. No static facade. |
| [`ServiceProviderExtensions.cs`](src/ResolvePattern/ServiceProviderExtensions.cs) | `UseScope(out var resolver)` opens a scope and hands back its resolver, `EnsureUseScope()` opens one only if none is open, `RunInOwnScope()` runs fire-and-forget work in a scope of its own, and `AddResolved<TService, TImpl>()` registers a service and wires it at construction. |

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

### A service belongs to the flow, not to the scope that built it

This is the rule the whole design turns on, and it is deliberate. A service's `Resolve<T>()` answers from whichever scope is ambient *at the moment of the call* — not from the scope that happened to construct it. The service object is a bundle of behaviour; the scope is the context it runs in. This is `TransactionScope` applied to the whole container scope: wrap the call site, and the code inside neither changes nor knows.

That is what makes the most common shape in a long-lived backend work — one singleton worker, many batches in parallel, a scope per lane:

```csharp
public class BatchWorker(IServiceProvider root) : ServiceBase, IBatchWorker     // registered as a singleton
{
    private IUnitOfWork UnitOfWork => Resolve<IUnitOfWork>();          // per scope: per lane

    public Task RunAll(IEnumerable<int> batchIds) =>
        Task.WhenAll(batchIds.Select(id => Task.Run(async () =>
        {
            using (root.UseScope())                                    // each lane opens its own
            {
                await RunBatch(id);                                    // same `this`, lane's own UnitOfWork
            }
        })));

    private Task RunBatch(int batchId) => /* ... uses UnitOfWork ... */;
}
```

Eight lanes, one worker object, eight units of work that never meet ([`ScopeFlowTests.cs`](tests/ResolvePattern.Tests/ScopeFlowTests.cs)). The alternative that looks cleaner on paper — assign each service the resolver of the scope that built it, and drop the ambient state — breaks exactly this: a singleton is built by the root scope, so every lane would resolve from *that one*, sharing a single unit of work and DbContext across threads. Scoped services re-home the same way: one constructed in a request and called inside a nested scope resolves from the nested scope, which is what a "do this in a fresh scope once the request is done" hook relies on.

The price is the one every ambient context pays (`HttpContext.Current`, `Transaction.Current`): the scope is static state that flows with the execution context. It is visible in one place — the `AsyncLocal` in [`ResolverScope.cs`](src/ResolvePattern/ResolverScope.cs) — and paying it carefully is what the [mechanics](docs/how-it-works.md) are about.

Two consequences follow, and neither is enforced for you. First, the isolation is *on request*, not by default: a forked task inherits the caller's scope, so a singleton that fans out lanes *without* a `UseScope()` per lane has all of them sharing one scoped `DbContext`, concurrently and silently — where constructor injection under `ValidateOnBuild` would have refused to start. Second, what is isolated is *resolved* state, not *field* state: a singleton `ServiceBase` with a mutable field is still shared across every lane. The second is [the one rule](#the-one-rule). The first is a `UseScope()` at every fork — where to put it, and when not to, is in [How it works, in detail](docs/how-it-works.md).

### The mechanics, elsewhere

Where the scope gets opened in ASP.NET Core (one middleware), `EnsureUseScope()` for code reachable from inside someone else's scope, `RunInOwnScope()` for fire-and-forget work, how `AddResolved` hands a service its resolver at construction, and the two `Resolve` layers — cached on the service, honest to the container on the resolver — are in [How it works, in detail](docs/how-it-works.md). None of it changes the argument; all of it you will need on day one of adopting.

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

---

## Run it

```bash
dotnet test                                   # 30 tests: cycles, caching, lifetimes, scope flow and disposal, substitution, smoke
dotnet run --project src/ResolvePattern.Sample
# -> Apollo: Task 1 in project 'Apollo', Task 2 in project 'Apollo'
```

Requires the .NET 10 SDK (or newer).

---

## "Isn't this the service locator anti-pattern?"

Short answer: yes — and the rule is being misapplied if you reach for it here.

The canonical "service locator is an anti-pattern" argument was written for **libraries**: code consumed by strangers who can't see your DI setup. Hiding a library's dependencies behind a locator genuinely harms its consumer, who has no way to learn what's needed except by running it and watching it throw. That advice is correct, and you should follow it in a library.

Inside a **cohesive internal application** where every service inherits one base class and one team owns the whole graph, the premise inverts. There are no strangers. `Resolve<ITaskPlanner>()` on a base class is no more a "hidden dependency" than `this.HttpContext` on an MVC controller or an `@Autowired` field on a Spring bean — it's an ambient framework contract you opted into. **The pattern *is* the contract.**

It is also not called statically. `IResolver` is an ordinary injectable interface: entry points receive one from the scope they open, services are assigned one at registration, and any class that wants the dependency visible in its signature can take it as a constructor parameter. Underneath, though, the scope it forwards to *is* ambient static state — an `AsyncLocal`, like `Transaction.Current` — and that is not an accident to apologise for: it is what lets one singleton serve many concurrent scopes (see [A service belongs to the flow](#a-service-belongs-to-the-flow-not-to-the-scope-that-built-it)). None of this makes it stop being a locator. It makes it a locator you can substitute, with its one piece of global state in one visible place.

There is a precedent, and it cuts both ways: much Spring code answered the same pressure with `@Autowired` on fields, while the Spring team's guidance still prefers constructors. The practice drifts toward ambient resolution because the pressure is real; the guidance resists because, without a team holding the line, it degrades into dependencies nobody owns. That is why the uniformity condition in [When **not** to use this](#when-not-to-use-this) is not optional.

---

## When **not** to use this

This is a narrow tool. Outside these conditions, use constructor injection.

- **Don't do this in a library.** Consumers can't see your container, so the anti-pattern rule applies at full force: make dependencies explicit.
- **Don't do this without a shared base class and a team that holds the line.** Every service the same way, and a bare `Resolver.Resolve<T>()` inside one is the flagged exception. A codebase that's 60% ctor injection, 20% property resolution, 10% `GetService()` is worse than any of those applied consistently. If your team won't commit everywhere, pick something else and apply *it* everywhere.
- **Don't do this if your domain is genuinely a tree and your services are small.** You may never hit the decay curve hard enough to justify being unconventional — and being unconventional has real costs: onboarding, tooling, this very conversation forever.
- **Don't do this if your code runs in exactly one context.** A request-scoped application with no background fan-out, no post-commit hooks and nothing that re-homes never uses the one capability here that constructor injection lacks. You would be paying for an ambient scope — opened at every entry point, minded at every fork — to get benefits that `Lazy<T>` and a registration override already give you.
- **Know the trade-offs even when it's right.** Manageable, not free:
  - You give up compile-time "this dependency is missing" errors for a failure at first use. The mitigation is mechanical, because of [the one rule](#the-one-rule): a smoke test opens a scope, resolves every registration and reads every property, so a missing registration fails in CI rather than in production ([`RegistrationSmokeTests.cs`](tests/ResolvePattern.Tests/RegistrationSmokeTests.cs)).
  - You take on an ambient scope that must be opened at every non-request entry point; forgetting is a runtime error (with a clear message), not a compile error.
  - Because the scope flows with the execution context, fire-and-forget work must be started with [`RunInOwnScope()`](docs/how-it-works.md#fire-and-forget-runinownscope), or it inherits a scope it will outlive.
  - You lose the constructor as a size signal without gaining a replacement — add one yourself ([What it does not fix](#what-it-does-not-fix-bloat)).

The real lesson generalizes past DI: **the cheapest decision and the correct decision are not the same, and the gap is invisible at the moment you choose.** "Add one more constructor parameter" wins every PR review and loses the decade. So does "add one more property" — which is why this pattern needs a checkpoint of its own.

---

## License

MIT — see [LICENSE](LICENSE).
