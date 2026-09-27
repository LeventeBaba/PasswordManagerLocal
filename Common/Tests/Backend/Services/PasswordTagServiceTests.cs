using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Backend.Services;
using System.Security.Cryptography;
using System.Text;
using static PasswordManagerLocal.Common.Contracts.Constants.PasswordConstants;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class PasswordTagServiceTests
{
    private static UserPasswordsData CreateEmptyPasswords()
    {
        var passwords = new UserPasswordsData();
        passwords.PasswordKey = RandomNumberGenerator.GetBytes(32);
        passwords.GenerateIntegrityHash();
        return passwords;
    }


    [TestMethod]
    public void AddPasswordTag_ValidRequest_NormalizesAndPersists()
    {
        var service = new PasswordTagService();
        var passwords = CreateEmptyPasswords();

        service.AddPasswordTag(new NewPasswordTagRequest
        {
            Name = "  Work  ",
            Color = "#ff123456"
        }, passwords);

        MSTestAssert.HasCount(1, passwords.Tags);
        MSTestAssert.AreEqual("Work", passwords.Tags[0].Name);
        MSTestAssert.AreEqual("#FF123456", passwords.Tags[0].Color);
        passwords.Tags[0].VerifyIntegrity();
        passwords.VerifyIntegrity();
    }


    [TestMethod]
    public void AddPasswordTag_DuplicateName_ThrowsCaseInsensitively()
    {
        var service = new PasswordTagService();
        var passwords = CreateEmptyPasswords();

        service.AddPasswordTag(new NewPasswordTagRequest
        {
            Name = "Important",
            Color = "#FF000001"
        }, passwords);

        ExpectThrows<DuplicatePasswordTagNameException>(() =>
        {
            service.AddPasswordTag(new NewPasswordTagRequest
            {
                Name = "  important  ",
                Color = "#FF000002"
            }, passwords);
        });

        MSTestAssert.HasCount(1, passwords.Tags);
        passwords.VerifyIntegrity();
    }


    [TestMethod]
    public void AddPasswordTag_InvalidRequest_ThrowsAndDoesNotModifyData()
    {
        var service = new PasswordTagService();
        var passwords = CreateEmptyPasswords();
        var originalHash = passwords.IntegrityHash.ToArray();

        ExpectThrows<InvalidInputException>(() =>
        {
            service.AddPasswordTag(new NewPasswordTagRequest
            {
                Name = "",
                Color = "not-a-color"
            }, passwords);
        });

        MSTestAssert.IsEmpty(passwords.Tags);
        CollectionAssert.AreEqual(originalHash, passwords.IntegrityHash);
        passwords.VerifyIntegrity();
    }


    [TestMethod]
    public void AddPasswordTag_WhenLimitReached_ThrowsAndDoesNotModifyData()
    {
        var service = new PasswordTagService();
        var passwords = CreateEmptyPasswords();

        for (var i = 0; i < MaxNumberOfPasswordTags; i++)
        {
            var tag = new PasswordTag
            {
                Id = Guid.NewGuid(),
                Name = $"Tag {i}",
                Color = "#FF000001",
                LastUpdatedAt = DateTime.UtcNow
            };
            tag.GenerateIntegrityHash();
            passwords.Tags.Add(tag);
        }
        passwords.GenerateIntegrityHash();

        ExpectThrows<LimitReachedException>(() =>
        {
            service.AddPasswordTag(new NewPasswordTagRequest
            {
                Name = "Overflow",
                Color = "#FFFFFFFF"
            }, passwords);
        });

        MSTestAssert.HasCount(MaxNumberOfPasswordTags, passwords.Tags);
        MSTestAssert.IsFalse(passwords.Tags.Any(tag => tag.Name == "Overflow"));
        passwords.VerifyIntegrity();
    }


    [TestMethod]
    public void UpdatePasswordTag_ValidRequest_NormalizesAndPersists()
    {
        var service = new PasswordTagService();
        var passwords = CreateEmptyPasswords();

        service.AddPasswordTag(new NewPasswordTagRequest
        {
            Name = "Work",
            Color = "#FF112233"
        }, passwords);

        var tagId = passwords.Tags[0].Id;
        service.UpdatePasswordTag(new UpdatePasswordTagRequest
        {
            Id = tagId,
            Name = "  Personal  ",
            Color = "#ff445566"
        }, passwords);

        MSTestAssert.AreEqual("Personal", passwords.Tags[0].Name);
        MSTestAssert.AreEqual("#FF445566", passwords.Tags[0].Color);
        passwords.Tags[0].VerifyIntegrity();
        passwords.VerifyIntegrity();
    }


    [TestMethod]
    public void UpdatePasswordTag_DuplicateName_ThrowsAndKeepsOriginalValues()
    {
        var service = new PasswordTagService();
        var passwords = CreateEmptyPasswords();

        service.AddPasswordTag(new NewPasswordTagRequest
        {
            Name = "Primary",
            Color = "#FF000001"
        }, passwords);
        service.AddPasswordTag(new NewPasswordTagRequest
        {
            Name = "Secondary",
            Color = "#FF000002"
        }, passwords);

        var secondary = passwords.Tags.Single(tag => tag.Name == "Secondary");
        var originalHash = passwords.IntegrityHash.ToArray();

        ExpectThrows<DuplicatePasswordTagNameException>(() =>
        {
            service.UpdatePasswordTag(new UpdatePasswordTagRequest
            {
                Id = secondary.Id,
                Name = " primary "
            }, passwords);
        });

        MSTestAssert.AreEqual("Secondary", secondary.Name);
        MSTestAssert.AreEqual("#FF000002", secondary.Color);
        CollectionAssert.AreEqual(originalHash, passwords.IntegrityHash);
        passwords.VerifyIntegrity();
    }


    [TestMethod]
    public void DeletePasswordTag_RetainsDerivedReferencesAndCreatesTombstone()
    {
        var service = new PasswordTagService();
        var passwords = CreateEmptyPasswords();

        service.AddPasswordTag(new NewPasswordTagRequest
        {
            Name = "Work",
            Color = "#FF123456"
        }, passwords);

        var tagId = passwords.Tags[0].Id;
        var password = new SecurePassword
        {
            Id = Guid.NewGuid(),
            Name = "Email",
            Color = "#FFFFFFFF",
            Password = Encoding.UTF8.GetBytes("encrypted"),
            TagIds = [tagId],
            CreatedAt = DateTime.UtcNow,
            LastUpdatedAt = DateTime.UtcNow
        };
        password.GenerateIntegrityHash();
        passwords.Passwords.Add(password);
        passwords.GenerateIntegrityHash();

        service.DeletePasswordTag(tagId, passwords);

        MSTestAssert.IsEmpty(passwords.Tags);
        MSTestAssert.HasCount(1, passwords.DeletedTags);
        MSTestAssert.AreEqual(tagId, passwords.DeletedTags[0].Id);
        CollectionAssert.AreEqual(new[] { tagId }, passwords.Passwords[0].TagIds);
        MSTestAssert.IsEmpty(new PasswordService().ConvertToPasswordInfoResponses(passwords)[0].TagIds);
        passwords.DeletedTags[0].VerifyIntegrity();
        passwords.Passwords[0].VerifyIntegrity();
        passwords.VerifyIntegrity();
    }


    [TestMethod]
    public void ExportPasswordTags_CopiesSelectedTagsWithNewIds()
    {
        var service = new PasswordTagService();
        var sourcePasswords = CreateEmptyPasswords();
        var targetPasswords = CreateEmptyPasswords();

        service.AddPasswordTag(new NewPasswordTagRequest
        {
            Name = "Work",
            Color = "#ff123456"
        }, sourcePasswords);

        var sourceTag = sourcePasswords.Tags[0];
        service.ExportPasswordTags([sourceTag.Id], sourcePasswords, targetPasswords);

        MSTestAssert.HasCount(1, sourcePasswords.Tags);
        MSTestAssert.HasCount(1, targetPasswords.Tags);
        MSTestAssert.AreNotEqual(sourceTag.Id, targetPasswords.Tags[0].Id);
        MSTestAssert.AreEqual("Work", targetPasswords.Tags[0].Name);
        MSTestAssert.AreEqual("#FF123456", targetPasswords.Tags[0].Color);
        sourcePasswords.VerifyIntegrity();
        targetPasswords.VerifyIntegrity();
    }


    [TestMethod]
    public void ExportPasswordTags_DuplicateTargetName_ThrowsWithoutModifyingTarget()
    {
        var service = new PasswordTagService();
        var sourcePasswords = CreateEmptyPasswords();
        var targetPasswords = CreateEmptyPasswords();

        service.AddPasswordTag(new NewPasswordTagRequest
        {
            Name = "Work",
            Color = "#FF123456"
        }, sourcePasswords);
        service.AddPasswordTag(new NewPasswordTagRequest
        {
            Name = "work",
            Color = "#FF654321"
        }, targetPasswords);

        var targetHash = targetPasswords.IntegrityHash.ToArray();
        ExpectThrows<DuplicatePasswordTagNameException>(() =>
        {
            service.ExportPasswordTags(
                [sourcePasswords.Tags[0].Id],
                sourcePasswords,
                targetPasswords);
        });

        MSTestAssert.HasCount(1, targetPasswords.Tags);
        MSTestAssert.AreEqual("work", targetPasswords.Tags[0].Name);
        CollectionAssert.AreEqual(targetHash, targetPasswords.IntegrityHash);
        sourcePasswords.VerifyIntegrity();
        targetPasswords.VerifyIntegrity();
    }


    [TestMethod]
    public void ConvertToPasswordTagInfoResponses_ReturnsStableSortedResponses()
    {
        var service = new PasswordTagService();
        var passwords = CreateEmptyPasswords();

        service.AddPasswordTag(new NewPasswordTagRequest
        {
            Name = "Work",
            Color = "#FF000003"
        }, passwords);
        service.AddPasswordTag(new NewPasswordTagRequest
        {
            Name = "Personal",
            Color = "#FF000002"
        }, passwords);

        var response = service.ConvertToPasswordTagInfoResponses(passwords);

        MSTestAssert.HasCount(2, response);
        MSTestAssert.AreEqual("Personal", response[0].Name);
        MSTestAssert.AreEqual("Work", response[1].Name);
    }


    private static void ExpectThrows<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
            MSTestAssert.Fail($"Expected exception: {typeof(TException).Name}");
        }
        catch (TException)
        {
        }
    }
}
