using Microsoft.EntityFrameworkCore.Diagnostics;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Persistence;

public sealed class RelationshipIntegrityMaterializationInterceptor : IMaterializationInterceptor
{
    public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
    {
        if (entity is UserDevice userDevice)
            userDevice.VerifyIntegrity();
        else if (entity is LocalUserDevice localUserDevice)
            localUserDevice.VerifyIntegrity();

        return entity;
    }
}
