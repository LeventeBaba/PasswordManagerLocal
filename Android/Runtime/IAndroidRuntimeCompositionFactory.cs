using PasswordManagerLocal.Common.Backend.Hosting;

namespace PasswordManagerLocal.Android.Runtime;

public interface IAndroidRuntimeCompositionFactory
{
    BackendRuntimeComposition Create();
}
