using PasswordManagerLocal.Android.Runtime;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeAndroidSecureStorageAvailability : IAndroidSecureStorageAvailability
{
    public bool IsAvailable { get; set; } = true;
}
