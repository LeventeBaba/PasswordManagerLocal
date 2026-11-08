using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Tests.Fakes;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.Fixtures;

internal sealed record SeededUserRoutes(
    User User,
    IReadOnlyList<Device> EnabledRemotes,
    IReadOnlyList<Device> DisabledRemotes,
    FakeDeviceIdentityService Identity);
