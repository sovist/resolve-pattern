using System.Reflection;
using System.Runtime.ExceptionServices;

namespace ResolvePattern.Tests;

public class RegistrationSmokeTests : TestBase
{
    [Fact]
    public void ResolveEverything_ShouldSucceed_When_EveryCollaboratorIsRegistered()
    {
        ResolveEverything(BuildServices());
    }

    [Fact]
    public void ResolveEverything_ShouldThrowNamingTheMissingType_When_ACollaboratorIsUnregistered()
    {
        // TaskService resolves IProjectService from a property, leave it unregistered.
        var services = new ServiceCollection()
            .AddResolved<ITaskService, TaskService>();

        var act = () => ResolveEverything(services);

        var ex = act.ShouldThrow<InvalidOperationException>();

        ex.Message.ShouldContain(nameof(IProjectService));
    }

    /// <summary>
    /// Resolves every registered service in a fresh scope and, for each <see cref="ServiceBase"/>,
    /// reads every property declared on it and its bases — which is what forces each lazy collaborator to resolve.
    /// </summary>
    private static void ResolveEverything(IServiceCollection services)
    {
        using var provider = services.BuildServiceProvider();
        using (provider.UseScope(out var resolver))
        {
            foreach (var descriptor in services.Where(d => !d.ServiceType.IsGenericTypeDefinition))
            {
                var instance = resolver.GetService(descriptor.ServiceType);

                instance.ShouldNotBeNull();

                if (instance is ServiceBase service)
                {
                    ReadEveryCollaborator(service);
                }
            }
        }
    }

    private static void ReadEveryCollaborator(ServiceBase service)
    {
        const BindingFlags declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        for (var type = service.GetType(); type != null && type != typeof(ServiceBase); type = type.BaseType)
        {
            foreach (var property in type.GetProperties(declared).Where(p => p.CanRead && p.GetIndexParameters().Length == 0))
            {
                try
                {
                    property.GetValue(service);
                }
                catch (TargetInvocationException e) when (e.InnerException != null)
                {
                    // Surface the real failure, not the reflection wrapper.
                    ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                }
            }
        }
    }
}
