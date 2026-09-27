using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using System.Text;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class UserPasswordTagServiceTests
{
    [TestMethod]
    public async Task AddUpdateDeletePasswordTag_PersistsThroughEncryptedUserPasswordsBlob()
    {
        using var host = new BackendTestHost();

        var auth = host.Services.GetRequiredService<IAuthService>();
        var tagService = host.Services.GetRequiredService<IUserPasswordTagService>();
        var passwordService = host.Services.GetRequiredService<IUserPasswordsService>();
        var cache = host.Services.GetRequiredService<IDataCachingService>();

        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest("tags-crud"));

        await tagService.AddPasswordTagAsync(token, new NewPasswordTagRequest
        {
            Name = "Work",
            Color = "#ff123456"
        });

        cache.InvalidateToken(token);
        var added = await passwordService.GetSavedPasswordsAsync(token);
        MSTestAssert.HasCount(1, added.Tags);
        MSTestAssert.AreEqual("Work", added.Tags[0].Name);
        MSTestAssert.AreEqual("#FF123456", added.Tags[0].Color);

        var tagId = added.Tags[0].Id;
        await tagService.UpdatePasswordTagAsync(token, new UpdatePasswordTagRequest
        {
            Id = tagId,
            Name = "Personal",
            Color = "#FF654321"
        });

        cache.InvalidateToken(token);
        var updated = await passwordService.GetSavedPasswordsAsync(token);
        MSTestAssert.HasCount(1, updated.Tags);
        MSTestAssert.AreEqual("Personal", updated.Tags[0].Name);
        MSTestAssert.AreEqual("#FF654321", updated.Tags[0].Color);

        await tagService.DeletePasswordTagAsync(token, tagId);

        cache.InvalidateToken(token);
        var deleted = await passwordService.GetSavedPasswordsAsync(token);
        MSTestAssert.IsEmpty(deleted.Tags);
    }


    [TestMethod]
    public async Task ExportPasswordTagsToUser_DeleteOriginalFalse_CopiesAndKeepsSourceTags()
    {
        using var host = new BackendTestHost();

        var auth = host.Services.GetRequiredService<IAuthService>();
        var tagService = host.Services.GetRequiredService<IUserPasswordTagService>();
        var passwordService = host.Services.GetRequiredService<IUserPasswordsService>();
        var cache = host.Services.GetRequiredService<IDataCachingService>();

        var sourceToken = await auth.RegisterAsync(host.CreateValidRegistrationRequest("tag-export-copy-source"));
        var targetToken = await auth.RegisterAsync(host.CreateValidRegistrationRequest("tag-export-copy-target"));

        await tagService.AddPasswordTagAsync(sourceToken, new NewPasswordTagRequest
        {
            Name = "Work",
            Color = "#FF123456"
        });
        await tagService.AddPasswordTagAsync(sourceToken, new NewPasswordTagRequest
        {
            Name = "Personal",
            Color = "#FF654321"
        });

        var sourceBefore = await passwordService.GetSavedPasswordsAsync(sourceToken);
        var sourceTagIds = sourceBefore.Tags.Select(tag => tag.Id).ToList();

        await tagService.ExportPasswordTagsToUserAsync(sourceToken, new ExportPasswordTagsToUserRequest
        {
            TargetToken = targetToken,
            PasswordTagIds = sourceTagIds,
            DeleteOriginal = false
        });

        cache.InvalidateToken(sourceToken);
        cache.InvalidateToken(targetToken);

        var sourceAfter = await passwordService.GetSavedPasswordsAsync(sourceToken);
        var targetAfter = await passwordService.GetSavedPasswordsAsync(targetToken);

        MSTestAssert.HasCount(2, sourceAfter.Tags);
        MSTestAssert.HasCount(2, targetAfter.Tags);
        CollectionAssert.AreEquivalent(
            new[] { "Personal", "Work" },
            targetAfter.Tags.Select(tag => tag.Name).ToArray());
        MSTestAssert.IsFalse(targetAfter.Tags.Any(tag => sourceTagIds.Contains(tag.Id)));
    }


    [TestMethod]
    public async Task ExportPasswordTagsToUser_DeleteOriginalTrue_MovesTagsAndRemovesSourcePasswordReferences()
    {
        using var host = new BackendTestHost();

        var auth = host.Services.GetRequiredService<IAuthService>();
        var tagService = host.Services.GetRequiredService<IUserPasswordTagService>();
        var passwordService = host.Services.GetRequiredService<IUserPasswordsService>();
        var cache = host.Services.GetRequiredService<IDataCachingService>();

        var sourceToken = await auth.RegisterAsync(host.CreateValidRegistrationRequest("tag-export-move-source"));
        var targetToken = await auth.RegisterAsync(host.CreateValidRegistrationRequest("tag-export-move-target"));

        await tagService.AddPasswordTagAsync(sourceToken, new NewPasswordTagRequest
        {
            Name = "Work",
            Color = "#FF123456"
        });

        var sourceTagId = (await passwordService.GetSavedPasswordsAsync(sourceToken)).Tags[0].Id;
        await passwordService.AddNewPasswordAsync(sourceToken, new NewPasswordRequest
        {
            Name = "Email",
            Password = Encoding.UTF8.GetBytes("secret"),
            TagIds = [sourceTagId]
        });

        await tagService.ExportPasswordTagsToUserAsync(sourceToken, new ExportPasswordTagsToUserRequest
        {
            TargetToken = targetToken,
            PasswordTagIds = [sourceTagId],
            DeleteOriginal = true
        });

        cache.InvalidateToken(sourceToken);
        cache.InvalidateToken(targetToken);

        var sourceAfter = await passwordService.GetSavedPasswordsAsync(sourceToken);
        var targetAfter = await passwordService.GetSavedPasswordsAsync(targetToken);

        MSTestAssert.IsEmpty(sourceAfter.Tags);
        MSTestAssert.HasCount(1, sourceAfter.Passwords);
        MSTestAssert.IsEmpty(sourceAfter.Passwords[0].TagIds);
        MSTestAssert.HasCount(1, targetAfter.Tags);
        MSTestAssert.AreEqual("Work", targetAfter.Tags[0].Name);
        MSTestAssert.AreNotEqual(sourceTagId, targetAfter.Tags[0].Id);
    }


    [TestMethod]
    public async Task DeletePasswordTag_RemovesTagIdFromSavedPasswords()
    {
        using var host = new BackendTestHost();

        var auth = host.Services.GetRequiredService<IAuthService>();
        var tagService = host.Services.GetRequiredService<IUserPasswordTagService>();
        var passwordService = host.Services.GetRequiredService<IUserPasswordsService>();
        var cache = host.Services.GetRequiredService<IDataCachingService>();

        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest("tags-password"));

        await tagService.AddPasswordTagAsync(token, new NewPasswordTagRequest
        {
            Name = "Work",
            Color = "#FF123456"
        });

        var tagId = (await passwordService.GetSavedPasswordsAsync(token)).Tags[0].Id;
        await passwordService.AddNewPasswordAsync(token, new NewPasswordRequest
        {
            Name = "Email",
            Password = Encoding.UTF8.GetBytes("secret"),
            TagIds = [tagId]
        });

        cache.InvalidateToken(token);
        var beforeDelete = await passwordService.GetSavedPasswordsAsync(token);
        MSTestAssert.HasCount(1, beforeDelete.Passwords[0].TagIds);
        MSTestAssert.AreEqual(tagId, beforeDelete.Passwords[0].TagIds[0]);

        await tagService.DeletePasswordTagAsync(token, tagId);

        cache.InvalidateToken(token);
        var afterDelete = await passwordService.GetSavedPasswordsAsync(token);
        MSTestAssert.HasCount(1, afterDelete.Passwords);
        MSTestAssert.IsEmpty(afterDelete.Passwords[0].TagIds);
    }
}
