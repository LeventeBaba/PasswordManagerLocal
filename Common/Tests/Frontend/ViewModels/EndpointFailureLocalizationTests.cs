using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Enrollment;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Contracts.Errors;
using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Common.Frontend.ViewModels;
using PasswordManagerLocal.Common.Frontend.ViewModels.Auth;
using PasswordManagerLocal.Common.Tests.Fakes;
using System.Reflection;

namespace PasswordManagerLocal.Common.Tests.Frontend.ViewModels;

[TestClass]
public sealed class EndpointFailureLocalizationTests
{
    [TestMethod]
    public async Task StableFailureKindsResolveToExistingLocalizationKeys()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        using var viewModel = new TestViewModel(preferences);
        var cases = new Dictionary<EndpointFailureKind, string>
        {
            [EndpointFailureKind.InvalidCredentials] = "Error_InvalidCredentials",
            [EndpointFailureKind.InvalidSession] = "Error_InvalidSession",
            [EndpointFailureKind.InvalidInput] = "Error_InvalidInput",
            [EndpointFailureKind.NotFound] = "Error_NotFound",
            [EndpointFailureKind.DuplicatePasswordName] = "Error_DuplicatePasswordName",
            [EndpointFailureKind.DuplicatePasswordTagName] = "Error_DuplicatePasswordTagName",
            [EndpointFailureKind.DuplicateCustomColorName] = "Error_DuplicateCustomColorName",
            [EndpointFailureKind.DuplicateCustomColorCode] = "Error_DuplicateCustomColorCode",
            [EndpointFailureKind.LimitReached] = "Error_LimitReached",
            [EndpointFailureKind.DataIntegrity] = "Error_DataIntegrity",
            [EndpointFailureKind.DeviceIdentityUnavailable] = "Error_DeviceIdentity",
            [EndpointFailureKind.RegistrationUsernameUnavailable] = "Validation_Username_Unavailable",
            [EndpointFailureKind.RegistrationUsernameAvailabilityIndeterminate] =
                "Validation_Username_AvailabilityIndeterminate",
            [EndpointFailureKind.Generic] = "Error_Generic"
        };

        foreach (var (kind, key) in cases)
        {
            var message = viewModel.SafeMessage(new EndpointFailureException(
                kind,
                enrollmentErrorCode: null,
                innerException: new Exception("sensitive backend detail")));

            Assert.AreEqual($"[{key}]", message, kind.ToString());
            Assert.IsFalse(message.Contains("sensitive", StringComparison.Ordinal), kind.ToString());
        }
    }

    [TestMethod]
    public async Task EnrollmentAndUnknownFailureKindsUseSafeLocalizedFallbacks()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        using var viewModel = new TestViewModel(preferences);

        var enrollment = viewModel.SafeMessage(new EndpointFailureException(
            EndpointFailureKind.DeviceEnrollment,
            DeviceEnrollmentErrorCode.InvalidCode,
            new Exception("sensitive enrollment detail")));
        var unknown = viewModel.SafeMessage(new EndpointFailureException(
            (EndpointFailureKind)int.MaxValue,
            enrollmentErrorCode: null,
            innerException: new Exception("sensitive unknown detail")));

        Assert.AreEqual("[Error_DeviceEnrollment_InvalidCode]", enrollment);
        Assert.AreEqual("[Error_Generic]", unknown);
        Assert.IsFalse(enrollment.Contains("sensitive", StringComparison.Ordinal));
        Assert.IsFalse(unknown.Contains("sensitive", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RegistrationUsernameFailureRemainsFieldSpecific()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var endpoints = DispatchProxy.Create<IEndpoints, RegistrationFailureEndpoints>();
        using var viewModel = new RegistrationViewModel(
            preferences,
            endpoints,
            navigateToLogin: () => { },
            onAuthenticationSucceededAsync: _ => Task.CompletedTask)
        {
            Username = "ValidUser1",
            FirstName = "Valid",
            LastName = "Person",
            Email = "valid@example.com",
            Password = "ValidPassword1!",
            ConfirmPassword = "ValidPassword1!"
        };
        typeof(RegistrationViewModel)
            .GetMethod("SetBackendInitialized", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewModel, [true]);
        var register = typeof(RegistrationViewModel).GetMethod(
            "RegisterAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        await (Task)register.Invoke(viewModel, null)!;

        Assert.AreEqual("[Validation_Username_Unavailable]", viewModel.UsernameValidationMessage);
        Assert.IsFalse(viewModel.HasStatusMessage);
    }

    private sealed class TestViewModel(UiPreferencesService preferences) : ViewModelBase(preferences)
    {
        public string SafeMessage(Exception exception) => GetSafeErrorMessage(exception);
    }

    public class RegistrationFailureEndpoints : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IEndpoints.RegisterAsync))
            {
                return Task.FromException<Guid>(new EndpointFailureException(
                    EndpointFailureKind.RegistrationUsernameUnavailable,
                    enrollmentErrorCode: null,
                    innerException: new Exception("sensitive registration detail")));
            }

            throw new NotSupportedException($"Unexpected endpoint call: {targetMethod?.Name}.");
        }
    }
}
