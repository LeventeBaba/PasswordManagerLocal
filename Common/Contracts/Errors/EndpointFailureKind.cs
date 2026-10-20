namespace PasswordManagerLocal.Common.Contracts.Errors;

public enum EndpointFailureKind
{
    Generic = 0,
    InvalidCredentials = 1,
    InvalidSession = 2,
    InvalidInput = 3,
    NotFound = 4,
    DuplicatePasswordName = 5,
    DuplicatePasswordTagName = 6,
    DuplicateCustomColorName = 7,
    DuplicateCustomColorCode = 8,
    LimitReached = 9,
    DataIntegrity = 10,
    DeviceIdentityUnavailable = 11,
    DeviceEnrollment = 12,
    RegistrationUsernameUnavailable = 13,
    RegistrationUsernameAvailabilityIndeterminate = 14
}
