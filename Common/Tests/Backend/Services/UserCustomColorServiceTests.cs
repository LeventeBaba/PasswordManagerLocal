using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class UserCustomColorServiceTests
{
    [TestMethod]
    public async Task AddCustomUserColors_AddsEveryColorInSingleBatch()
    {
        using var host = new BackendTestHost();

        var auth = host.Services.GetRequiredService<IAuthService>();
        var customColorService = host.Services.GetRequiredService<IUserCustomColorService>();
        var passwordService = host.Services.GetRequiredService<IUserPasswordsService>();

        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest("colors-batch"));

        await customColorService.AddCustomUserColorsAsync(token,
        [
            new NewCustomUserColorRequest { ColorName = "Blue", ColorCode = "#FF010203" },
            new NewCustomUserColorRequest { ColorName = "Green", ColorCode = "#FF040506" },
            new NewCustomUserColorRequest { ColorName = "Red", ColorCode = "#FF070809" }
        ]);

        var response = await passwordService.GetSavedPasswordsAsync(token);
        MSTestAssert.HasCount(3, response.CustomColors);
        CollectionAssert.AreEquivalent(
            new[] { "Blue", "Green", "Red" },
            response.CustomColors.Select(color => color.ColorName).ToArray());
    }


    [TestMethod]
    public async Task AddCustomUserColors_DuplicateWithinBatch_ThrowsWithoutAddingAnyColor()
    {
        using var host = new BackendTestHost();

        var auth = host.Services.GetRequiredService<IAuthService>();
        var customColorService = host.Services.GetRequiredService<IUserCustomColorService>();
        var passwordService = host.Services.GetRequiredService<IUserPasswordsService>();

        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest("colors-batch-atomic"));

        await ExpectThrowsAsync<DuplicateCustomUserColorNameException>(async () =>
        {
            await customColorService.AddCustomUserColorsAsync(token,
            [
                new NewCustomUserColorRequest { ColorName = "Duplicate", ColorCode = "#FF010203" },
                new NewCustomUserColorRequest { ColorName = " duplicate ", ColorCode = "#FF040506" }
            ]);
        });

        var response = await passwordService.GetSavedPasswordsAsync(token);
        MSTestAssert.IsEmpty(response.CustomColors);
    }



    [TestMethod]
    public async Task AddUpdateDeleteCustomUserColor_PersistsThroughEncryptedUserPasswordsBlob()
    {
        using var host = new BackendTestHost();

        var auth = host.Services.GetRequiredService<IAuthService>();
        var customColorService = host.Services.GetRequiredService<IUserCustomColorService>();
        var passwordService = host.Services.GetRequiredService<IUserPasswordsService>();
        var cache = host.Services.GetRequiredService<IDataCachingService>();

        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest("colors-crud"));

        await AddColorAsync(customColorService, token, new NewCustomUserColorRequest
        {
            ColorName = "Work",
            ColorCode = "#ff123456"
        });

        cache.InvalidateToken(token);
        var added = await passwordService.GetSavedPasswordsAsync(token);
        MSTestAssert.HasCount(1, added.CustomColors);
        MSTestAssert.AreEqual("Work", added.CustomColors[0].ColorName);
        MSTestAssert.AreEqual("#FF123456", added.CustomColors[0].ColorCode);

        var colorId = added.CustomColors[0].Id;
        await customColorService.UpdateCustomUserColorAsync(token, new UpdateCustomUserColorRequest
        {
            Id = colorId,
            ClearColorName = true,
            ColorCode = "#FF654321"
        });

        cache.InvalidateToken(token);
        var updated = await passwordService.GetSavedPasswordsAsync(token);
        MSTestAssert.HasCount(1, updated.CustomColors);
        MSTestAssert.IsNull(updated.CustomColors[0].ColorName);
        MSTestAssert.AreEqual("#FF654321", updated.CustomColors[0].ColorCode);

        await customColorService.DeleteCustomUserColorsAsync(token, [colorId]);

        cache.InvalidateToken(token);
        var deleted = await passwordService.GetSavedPasswordsAsync(token);
        MSTestAssert.IsEmpty(deleted.CustomColors);
    }


    [TestMethod]
    public async Task DeleteCustomUserColors_MultipleItems_RemovesAllRequestedPersistently()
    {
        using var host = new BackendTestHost();

        var auth = host.Services.GetRequiredService<IAuthService>();
        var customColorService = host.Services.GetRequiredService<IUserCustomColorService>();
        var passwordService = host.Services.GetRequiredService<IUserPasswordsService>();
        var cache = host.Services.GetRequiredService<IDataCachingService>();

        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest("colors-batch-delete"));

        await customColorService.AddCustomUserColorsAsync(token,
        [
            new NewCustomUserColorRequest { ColorName = "Blue", ColorCode = "#FF010203" },
            new NewCustomUserColorRequest { ColorName = "Green", ColorCode = "#FF040506" },
            new NewCustomUserColorRequest { ColorName = "Red", ColorCode = "#FF070809" }
        ]);

        var before = await passwordService.GetSavedPasswordsAsync(token);
        var idsToDelete = before.CustomColors
            .Where(color => color.ColorName is "Blue" or "Red")
            .Select(color => color.Id)
            .ToList();

        await customColorService.DeleteCustomUserColorsAsync(token, idsToDelete);

        cache.InvalidateToken(token);
        var after = await passwordService.GetSavedPasswordsAsync(token);

        MSTestAssert.HasCount(1, after.CustomColors);
        MSTestAssert.AreEqual("Green", after.CustomColors[0].ColorName);
    }


    [TestMethod]
    public async Task ExportCustomUserColorsToUser_DeleteOriginalFalse_CopiesAndKeepsSourceColors()
    {
        using var host = new BackendTestHost();

        var auth = host.Services.GetRequiredService<IAuthService>();
        var customColorService = host.Services.GetRequiredService<IUserCustomColorService>();
        var passwordService = host.Services.GetRequiredService<IUserPasswordsService>();
        var cache = host.Services.GetRequiredService<IDataCachingService>();

        var sourceToken = await auth.RegisterAsync(host.CreateValidRegistrationRequest("color-export-copy-source"));
        var targetToken = await auth.RegisterAsync(host.CreateValidRegistrationRequest("color-export-copy-target"));

        await customColorService.AddCustomUserColorsAsync(sourceToken,
        [
            new NewCustomUserColorRequest { ColorName = "Work", ColorCode = "#FF123456" },
            new NewCustomUserColorRequest { ColorName = "Personal", ColorCode = "#FF654321" }
        ]);

        var sourceBefore = await passwordService.GetSavedPasswordsAsync(sourceToken);
        var sourceColorIds = sourceBefore.CustomColors.Select(color => color.Id).ToList();

        await customColorService.ExportCustomUserColorsToUserAsync(sourceToken, new ExportCustomUserColorsToUserRequest
        {
            TargetToken = targetToken,
            CustomUserColorIds = sourceColorIds,
            DeleteOriginal = false
        });

        cache.InvalidateToken(sourceToken);
        cache.InvalidateToken(targetToken);

        var sourceAfter = await passwordService.GetSavedPasswordsAsync(sourceToken);
        var targetAfter = await passwordService.GetSavedPasswordsAsync(targetToken);

        MSTestAssert.HasCount(2, sourceAfter.CustomColors);
        MSTestAssert.HasCount(2, targetAfter.CustomColors);
        CollectionAssert.AreEquivalent(
            new[] { "Personal", "Work" },
            targetAfter.CustomColors.Select(color => color.ColorName).ToArray());
        MSTestAssert.IsFalse(targetAfter.CustomColors.Any(color => sourceColorIds.Contains(color.Id)));
    }


    [TestMethod]
    public async Task ExportCustomUserColorsToUser_DeleteOriginalTrue_MovesSelectedColors()
    {
        using var host = new BackendTestHost();

        var auth = host.Services.GetRequiredService<IAuthService>();
        var customColorService = host.Services.GetRequiredService<IUserCustomColorService>();
        var passwordService = host.Services.GetRequiredService<IUserPasswordsService>();
        var cache = host.Services.GetRequiredService<IDataCachingService>();

        var sourceToken = await auth.RegisterAsync(host.CreateValidRegistrationRequest("color-export-move-source"));
        var targetToken = await auth.RegisterAsync(host.CreateValidRegistrationRequest("color-export-move-target"));

        await customColorService.AddCustomUserColorsAsync(sourceToken,
        [
            new NewCustomUserColorRequest { ColorName = "Work", ColorCode = "#FF123456" },
            new NewCustomUserColorRequest { ColorName = "Personal", ColorCode = "#FF654321" }
        ]);

        var sourceBefore = await passwordService.GetSavedPasswordsAsync(sourceToken);
        var workColorId = sourceBefore.CustomColors.Single(color => color.ColorName == "Work").Id;

        await customColorService.ExportCustomUserColorsToUserAsync(sourceToken, new ExportCustomUserColorsToUserRequest
        {
            TargetToken = targetToken,
            CustomUserColorIds = [workColorId],
            DeleteOriginal = true
        });

        cache.InvalidateToken(sourceToken);
        cache.InvalidateToken(targetToken);

        var sourceAfter = await passwordService.GetSavedPasswordsAsync(sourceToken);
        var targetAfter = await passwordService.GetSavedPasswordsAsync(targetToken);

        MSTestAssert.HasCount(1, sourceAfter.CustomColors);
        MSTestAssert.AreEqual("Personal", sourceAfter.CustomColors[0].ColorName);
        MSTestAssert.HasCount(1, targetAfter.CustomColors);
        MSTestAssert.AreEqual("Work", targetAfter.CustomColors[0].ColorName);
        MSTestAssert.AreNotEqual(workColorId, targetAfter.CustomColors[0].Id);
    }


    [TestMethod]
    public async Task AddCustomUserColor_DuplicateCode_ThrowsAndKeepsExistingColor()
    {
        using var host = new BackendTestHost();

        var auth = host.Services.GetRequiredService<IAuthService>();
        var customColorService = host.Services.GetRequiredService<IUserCustomColorService>();
        var passwordService = host.Services.GetRequiredService<IUserPasswordsService>();

        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest("colors-duplicate"));

        await AddColorAsync(customColorService, token, new NewCustomUserColorRequest
        {
            ColorName = "Original",
            ColorCode = "#FF010203"
        });

        await ExpectThrowsAsync<DuplicateCustomUserColorCodeException>(async () =>
        {
            await AddColorAsync(customColorService, token, new NewCustomUserColorRequest
            {
                ColorName = "Duplicate",
                ColorCode = "#ff010203"
            });
        });

        var response = await passwordService.GetSavedPasswordsAsync(token);
        MSTestAssert.HasCount(1, response.CustomColors);
        MSTestAssert.AreEqual("Original", response.CustomColors[0].ColorName);
    }


    [TestMethod]
    public async Task AddCustomUserColor_DuplicateName_ThrowsAndKeepsExistingColor()
    {
        using var host = new BackendTestHost();

        var auth = host.Services.GetRequiredService<IAuthService>();
        var customColorService = host.Services.GetRequiredService<IUserCustomColorService>();
        var passwordService = host.Services.GetRequiredService<IUserPasswordsService>();

        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest("colors-duplicate-name"));

        await AddColorAsync(customColorService, token, new NewCustomUserColorRequest
        {
            ColorName = "Original",
            ColorCode = "#FF010203"
        });

        await ExpectThrowsAsync<DuplicateCustomUserColorNameException>(async () =>
        {
            await AddColorAsync(customColorService, token, new NewCustomUserColorRequest
            {
                ColorName = "  original  ",
                ColorCode = "#FF010204"
            });
        });

        var response = await passwordService.GetSavedPasswordsAsync(token);
        MSTestAssert.HasCount(1, response.CustomColors);
        MSTestAssert.AreEqual("Original", response.CustomColors[0].ColorName);
    }


    [TestMethod]
    public async Task UpdateCustomUserColor_InvalidRequest_Throws()
    {
        using var host = new BackendTestHost();

        var auth = host.Services.GetRequiredService<IAuthService>();
        var customColorService = host.Services.GetRequiredService<IUserCustomColorService>();

        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest("colors-invalid-update"));

        await ExpectThrowsAsync<InvalidInputException>(async () =>
        {
            await customColorService.UpdateCustomUserColorAsync(token, new UpdateCustomUserColorRequest
            {
                Id = Guid.NewGuid()
            });
        });
    }


    [TestMethod]
    public async Task DeleteCustomUserColors_NonExistingColor_Throws()
    {
        using var host = new BackendTestHost();

        var auth = host.Services.GetRequiredService<IAuthService>();
        var customColorService = host.Services.GetRequiredService<IUserCustomColorService>();

        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest("colors-missing-delete"));

        await ExpectThrowsAsync<CustomUserColorNotFoundException>(async () =>
        {
            await customColorService.DeleteCustomUserColorsAsync(token, [Guid.NewGuid()]);
        });
    }


    [TestMethod]
    public async Task InvalidToken_Throws()
    {
        using var host = new BackendTestHost();
        var customColorService = host.Services.GetRequiredService<IUserCustomColorService>();

        await ExpectThrowsAsync<InvalidTokenException>(async () =>
        {
            await AddColorAsync(customColorService, Guid.NewGuid(), new NewCustomUserColorRequest
            {
                ColorCode = "#FF000001"
            });
        });
    }


    private static Task AddColorAsync(
        IUserCustomColorService customColorService,
        Guid token,
        NewCustomUserColorRequest request) =>
        customColorService.AddCustomUserColorsAsync(token, [request]);


    private static async Task ExpectThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
            MSTestAssert.Fail($"Expected exception: {typeof(TException).Name}");
        }
        catch (TException)
        {
        }
    }
}
