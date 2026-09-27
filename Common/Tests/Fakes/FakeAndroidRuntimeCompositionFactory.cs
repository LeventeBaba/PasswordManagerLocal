using PasswordManagerLocal.Android.Runtime;
using PasswordManagerLocal.Common.Backend.Hosting;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeAndroidRuntimeCompositionFactory : IAndroidRuntimeCompositionFactory
{
    private readonly Func<BackendRuntimeComposition> _factory;

    public FakeAndroidRuntimeCompositionFactory(Func<BackendRuntimeComposition> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public int CreateCalls { get; private set; }

    public BackendRuntimeComposition Create()
    {
        CreateCalls++;
        return _factory();
    }
}
