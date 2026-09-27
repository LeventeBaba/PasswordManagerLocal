using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

using PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.Fixtures;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.Fixtures;

internal sealed record UserTombstoneGarbageCollectorMember(Guid DeviceId, Guid InstanceId);
