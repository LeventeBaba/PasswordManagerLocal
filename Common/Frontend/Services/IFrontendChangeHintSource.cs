using PasswordManagerLocal.Common.Contracts.Runtime;

namespace PasswordManagerLocal.Common.Frontend.Services;

public interface IFrontendChangeHintSource
{
    event Action<FrontendChangeStreamDelivery>? ChangeHintReceived;
}
