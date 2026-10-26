using System.Reflection;
using System.Windows.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Frontend.Localization;
using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Common.Frontend.ViewModels.Pages;
using PasswordManagerLocal.Common.Tests.Fakes;

namespace PasswordManagerLocal.Common.Tests.Frontend.ViewModels;

[TestClass]
public sealed class PasswordNameValidationTests
{
    [TestMethod]
    [DataRow(AppLanguage.English, false)]
    [DataRow(AppLanguage.English, true)]
    [DataRow(AppLanguage.Hungarian, false)]
    [DataRow(AppLanguage.Hungarian, true)]
    public async Task AddingOrRenamingToAHiddenExistingNameIdentifiesTheNameFieldAndKeepsTheDraft(
        AppLanguage language, bool editing)
    {
        await using var preferences = await CreatePreferencesAsync(language);
        var endpoints = DispatchProxy.Create<IEndpoints, PasswordEndpoints>();
        var backend = (PasswordEndpoints)(object)endpoints;
        using var editor = new PasswordsViewModel(preferences, endpoints, new AuthSessionRegistry());
        Assert.IsTrue(await editor.LoadAsync(Guid.NewGuid()));
        editor.SearchQuery = "Bank";
        ((ICommand)editor.SearchCommand).Execute(null);
        Assert.AreEqual(1, editor.Passwords.Count);
        Assert.AreEqual("Bank", editor.Passwords[0].Name);
        OpenEditor(editor, editing);
        editor.EditorName = "  eMaIl  ";
        editor.EditorDescription = "unfinished description";
        editor.EditorPassword = "unfinished password";

        await SaveAsync(editor);

        Assert.AreEqual(0, backend.SaveCalls);
        AssertDuplicateNotice(editor, preferences.GetString("Error_DuplicatePasswordName"));
        Assert.AreEqual("  eMaIl  ", editor.EditorName);
        Assert.AreEqual("unfinished description", editor.EditorDescription);
        Assert.AreEqual("unfinished password", editor.EditorPassword);

        // Keep the corrected name in the active search results so refreshing an
        // edited entry cannot hide an accidental navigation back to its details.
        editor.EditorName = "Bank - personal";
        Assert.IsFalse(editor.HasEditorNameValidationError);
        Assert.IsFalse(editor.HasStatusMessage);
        await SaveAsync(editor);
        Assert.AreEqual(1, backend.SaveCalls);
        Assert.AreEqual("Bank - personal", backend.LastSavedName);
        Assert.IsTrue(editor.IsListPaneVisible);
        Assert.IsFalse(editor.HasSelection);
        Assert.IsTrue(editor.Passwords.Any(item => item.Name == "Bank - personal"));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task BackendOnlyConflictUsesTheSameNoticeForLocalAndWindowsFailures(
        bool editing, bool remoteFailure)
    {
        await using var preferences = await CreatePreferencesAsync(AppLanguage.English);
        var endpoints = DispatchProxy.Create<IEndpoints, PasswordEndpoints>();
        var backend = (PasswordEndpoints)(object)endpoints;
        backend.SaveFailure = remoteFailure
            ? new EndpointFailureException(EndpointFailureKind.DuplicatePasswordName, null,
                new Exception("private backend details"))
            : new DuplicatePasswordNameException("private backend name");
        using var editor = new PasswordsViewModel(preferences, endpoints, new AuthSessionRegistry());
        Assert.IsTrue(await editor.LoadAsync(Guid.NewGuid()));
        OpenEditor(editor, editing);
        editor.EditorName = "Added on another device";
        editor.EditorPassword = "unfinished password";

        await SaveAsync(editor);

        Assert.AreEqual(1, backend.SaveCalls);
        AssertDuplicateNotice(editor, preferences.GetString("Error_DuplicatePasswordName"));
        Assert.AreEqual("Added on another device", editor.EditorName);
        Assert.AreEqual("unfinished password", editor.EditorPassword);
        Assert.IsFalse(editor.StatusMessage!.Contains("private", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task KeepingTheEditedPasswordNameDoesNotConflictWithItself()
    {
        await using var preferences = await CreatePreferencesAsync(AppLanguage.English);
        var endpoints = DispatchProxy.Create<IEndpoints, PasswordEndpoints>();
        var backend = (PasswordEndpoints)(object)endpoints;
        using var editor = new PasswordsViewModel(preferences, endpoints, new AuthSessionRegistry());
        Assert.IsTrue(await editor.LoadAsync(Guid.NewGuid()));
        OpenEditor(editor, editing: true);
        editor.EditorName = "  bAnK  ";
        editor.EditorDescription = "updated description";

        await SaveAsync(editor);

        Assert.AreEqual(1, backend.SaveCalls);
        Assert.AreEqual("bAnK", backend.LastSavedName);
        Assert.IsFalse(editor.HasEditorNameValidationError);
        Assert.IsTrue(editor.IsListPaneVisible);
        Assert.IsFalse(editor.HasSelection);
        Assert.IsTrue(editor.Passwords.Any(item => item.Name == "bAnK"));
    }

    [TestMethod]
    public async Task AnUnrelatedFailureIsNotRelabeledFromItsInnerException()
    {
        await using var preferences = await CreatePreferencesAsync(AppLanguage.English);
        var endpoints = DispatchProxy.Create<IEndpoints, PasswordEndpoints>();
        var backend = (PasswordEndpoints)(object)endpoints;
        backend.SaveFailure = new EndpointFailureException(EndpointFailureKind.Generic, null,
            new DuplicatePasswordNameException("private details"));
        using var editor = new PasswordsViewModel(preferences, endpoints, new AuthSessionRegistry());
        Assert.IsTrue(await editor.LoadAsync(Guid.NewGuid()));
        OpenEditor(editor, editing: false);
        editor.EditorName = "New name";
        editor.EditorPassword = "unfinished password";

        await SaveAsync(editor);

        Assert.IsFalse(editor.HasEditorNameValidationError);
        Assert.AreEqual(preferences.GetString("Error_Generic"), editor.StatusMessage);
        Assert.IsTrue(editor.IsEditorPaneVisible);
    }

    private static void AssertDuplicateNotice(PasswordsViewModel editor, string expected)
    {
        Assert.IsTrue(editor.IsEditorPaneVisible);
        Assert.IsTrue(editor.HasEditorNameValidationError);
        Assert.IsTrue(editor.IsStatusMessageError);
        Assert.AreEqual(expected, editor.EditorNameValidationMessage);
        Assert.AreEqual(expected, editor.StatusMessage);
    }

    private static void OpenEditor(PasswordsViewModel editor, bool editing)
    {
        if (editing)
            ((ICommand)editor.EditPasswordCommand).Execute(editor.Passwords.Single(item => item.Name == "Bank"));
        else
            ((ICommand)editor.BeginCreatePasswordCommand).Execute(null);
    }

    private static Task SaveAsync(PasswordsViewModel editor) =>
        (Task)typeof(PasswordsViewModel).GetMethod("SavePasswordAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(editor, null)!;

    private static Task<UiPreferencesService> CreatePreferencesAsync(AppLanguage language) =>
        TestUiPreferencesFactory.CreateAsync(new ApplicationPreferences
        {
            Language = language,
            Theme = AppThemeId.Dark
        }, resourceLoader: new ShippedLocalizationLoader());

    public class PasswordEndpoints : DispatchProxy
    {
        private readonly SavedPasswordsResponse _passwords = new()
        {
            Passwords = [new PasswordInfoResponse { Name = "Email" }, new PasswordInfoResponse { Name = "Bank" }]
        };

        public Exception? SaveFailure { get; set; }
        public int SaveCalls { get; private set; }
        public string? LastSavedName { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == nameof(IEndpoints.GetSavedPasswordsAsync))
                return Task.FromResult(_passwords);
            if (method?.Name is nameof(IEndpoints.AddNewPasswordAsync) or nameof(IEndpoints.UpdatePasswordAsync))
            {
                SaveCalls++;
                var savedRequest = args![1];
                LastSavedName = savedRequest switch
                {
                    NewPasswordRequest request => request.Name,
                    UpdatePasswordRequest request => request.Name,
                    _ => throw new InvalidOperationException("Unexpected password request.")
                };
                if (SaveFailure is { } failure)
                    return Task.FromException(failure);

                // Return the saved state on refresh, as the real endpoints do.
                if (savedRequest is NewPasswordRequest added)
                {
                    _passwords.Passwords = [.. _passwords.Passwords, new PasswordInfoResponse { Name = added.Name }];
                }
                else if (savedRequest is UpdatePasswordRequest updated)
                {
                    var saved = _passwords.Passwords.Single(item => item.Id == updated.Id);
                    saved.Name = updated.Name ?? saved.Name;
                }

                return Task.CompletedTask;
            }
            throw new NotSupportedException($"Unexpected endpoint call: {method?.Name}.");
        }
    }

    private sealed class ShippedLocalizationLoader : IFrontendLocalizationResourceLoader
    {
        public ValueTask<Stream> OpenAsync(AppLanguage language, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Stream>(File.OpenRead(Path.Combine(AppContext.BaseDirectory,
                "Localization", language == AppLanguage.Hungarian ? "hu.json" : "en_us.json")));
    }
}
