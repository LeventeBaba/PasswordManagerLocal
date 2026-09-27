using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure;

/// <summary>
/// Stable, stateless service instances shared by production-composition test hosts. Reusing the
/// materialization interceptor keeps otherwise equivalent DbContext options equivalent in EF
/// Core's internal service-provider cache.
/// </summary>
internal static class ProductionTestServiceInstances
{
    public static RelationshipIntegrityMaterializationInterceptor RelationshipIntegrityInterceptor { get; } = new();
}
