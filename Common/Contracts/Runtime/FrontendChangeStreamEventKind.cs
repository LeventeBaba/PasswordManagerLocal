namespace PasswordManagerLocal.Common.Contracts.Runtime;

public enum FrontendChangeStreamEventKind
{
    Connected = 1,
    Change = 2,
    ResyncRequired = 3,
    Disconnected = 4
}
