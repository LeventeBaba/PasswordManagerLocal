using PasswordManagerLocal.Common.Backend.Abstractions.Services;

namespace PasswordManagerLocal.Common.Backend.Hosting;

internal interface IBackendExecutionProfileProviderSink
{
    void SetExecutionProfileProvider(IBackendExecutionProfileProvider executionProfileProvider);
}
