using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Tests.Fakes;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.Fixtures;

internal sealed record SyncAuthorizationSetup(
    SyncAuthorizationService Service,
    FakeGroupRepository Groups,
    FakeUserDeviceRepository UserDevices,
    FakeLocalUserDeviceRepository LocalUsers,
    FakeDeviceIdentityService Identity);
