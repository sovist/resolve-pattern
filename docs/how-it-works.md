# How it works, in detail

The mechanics behind [the README](../README.md#how-it-works): where the ambient scope gets opened, how nesting and fire-and-forget behave, how a service comes by its resolver, and what the two `Resolve` layers do. The XML docs on the four source files cover the same ground, per member.

## ASP.NET Core: open the scope in middleware

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

Ten lines, deliberately not shipped in the library: it would put an ASP.NET Core dependency on four files that otherwise need nothing but `Microsoft.Extensions.DependencyInjection`.

## Reentrancy: `EnsureUseScope()`

Some code is reachable both from its own entry point and from inside someone else's scope — a job a request can also invoke inline, say. Calling `UseScope()` there nests a second scope with its own cache — the re-homing the README's [flow section](../README.md#a-service-belongs-to-the-flow-not-to-the-scope-that-built-it) describes, when you did not want it: the same `Resolve<T>()` returns a different instance inside than outside. `EnsureUseScope()` opens a scope only when there isn't one:

```csharp
using (serviceProvider.EnsureUseScope())   // no-op if a scope is already open
{
    // ...
}
```

## Fire-and-forget: `RunInOwnScope()`

Because the scope flows with the execution context, work forked inside a scope inherits it — and if the work outlives the scope, its next resolution fails with `ObjectDisposedException` (by design, rather than handing back an already-disposed instance). Work that should run on its own gets its own:

```csharp
serviceProvider.RunInOwnScope(async resolver =>
{
    await resolver.Resolve<IMailer>().SendAsync(message);            // its own scope, ended when the work is
});
```

It starts with the execution context suppressed, so the work inherits no scope, then opens one. Suppressing the flow drops every other `AsyncLocal` too — a current user, a correlation id, the culture — so restore what the work needs inside it. `ResolverScope.RunDetached()` is the primitive, for work that opens its scope itself.

## Where the resolver comes from

No service reaches the ambient scope through a static *API*: entry points get their resolver from the scope they open; services get theirs assigned when the container constructs them:

```csharp
services.AddResolved<ITaskService, TaskService>();             // AddScoped, plus: wire the resolver at construction

public class Job(IResolver resolver)                           // or just take it, like any dependency
{
    public void Run() => resolver.Resolve<ITaskService>().Describe(1);
}
```

`AddResolved` is property injection, hand-rolled: a factory constructs the instance and assigns its resolver in the same step, so every path to the instance is covered — resolved through the pattern, built as a controller's constructor dependency, or pulled from a raw `IServiceProvider`. Microsoft.Extensions.DependencyInjection has no property injection of its own; this is one visible line per service instead of a container that does (Autofac's `PropertiesAutowired`). Register a `ServiceBase` any other way and it fails fast at first use, naming the fix — and the [smoke test](../tests/ResolvePattern.Tests/RegistrationSmokeTests.cs) catches it before then.

That `ServiceBase.Resolver` is a settable property gives a second test seam: assign a substitute and exercise a service with no container and no scope (see [`SubstitutionTests.cs`](../tests/ResolvePattern.Tests/SubstitutionTests.cs)).

## Cached by default on the service, honest to the container on the resolver

Same name, two layers — worth being explicit about, because a `ServiceBase` and an entry point spell it differently:

| Call | Behavior |
|---|---|
| `resolver.Resolve<T>()` on an `IResolver` | **Whatever the registration says.** Transient is new each call, scoped is shared within the scope. The resolver never overrides your container. |
| `resolver.ResolveLazy<T>()` on an `ILazyResolver` | **Memoized for the scope**, regardless of registered lifetime. |
| `Resolve<T>()` inside a `ServiceBase` | `Resolver.ResolveLazy<T>()` — the cached one. The 99% path for collaborators. |

The cached default is centralized in the base class, so the decision doesn't live at hundreds of call sites. When a service genuinely wants the container's lifetime instead, it writes `Resolver.Resolve<T>()` in full — and that longer spelling is the review signal: "why are we bypassing the cache here?"
