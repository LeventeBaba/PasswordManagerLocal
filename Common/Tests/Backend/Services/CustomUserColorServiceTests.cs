using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Backend.Services;
using System.Security.Cryptography;
using static PasswordManagerLocal.Common.Contracts.Constants.PasswordConstants;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class CustomUserColorServiceTests
{
    private static UserPasswordsData CreateEmptyPasswords()
    {
        var passwords = new UserPasswordsData();
        passwords.PasswordKey = RandomNumberGenerator.GetBytes(32);
        passwords.GenerateIntegrityHash();
        return passwords;
    }


    [TestMethod]
    public void AddCustomUserColor_ValidRequest_NormalizesAndPersists()
    {
        var service = new CustomUserColorService();
        var passwords = CreateEmptyPasswords();

        service.AddCustomUserColor(new NewCustomUserColorRequest
        {
            ColorName = "  Personal  ",
            ColorCode = "#ffabcdef"
        }, passwords);

        MSTestAssert.HasCount(1, passwords.CustomColors);
        MSTestAssert.AreEqual("Personal", passwords.CustomColors[0].ColorName);
        MSTestAssert.AreEqual("#FFABCDEF", passwords.CustomColors[0].ColorCode);
        passwords.CustomColors[0].VerifyIntegrity();
        passwords.VerifyIntegrity();
    }


    [TestMethod]
    public void AddCustomUserColor_InvalidRequest_ThrowsAndDoesNotModifyData()
    {
        var service = new CustomUserColorService();
        var passwords = CreateEmptyPasswords();
        var originalHash = passwords.IntegrityHash.ToArray();

        ExpectThrows<InvalidInputException>(() =>
        {
            service.AddCustomUserColor(new NewCustomUserColorRequest
            {
                ColorName = "Useful",
                ColorCode = "not-a-color"
            }, passwords);
        });

        MSTestAssert.IsEmpty(passwords.CustomColors);
        CollectionAssert.AreEqual(originalHash, passwords.IntegrityHash);
        passwords.VerifyIntegrity();
    }


    [TestMethod]
    public void AddCustomUserColor_DuplicateColorCode_ThrowsCaseInsensitively()
    {
        var service = new CustomUserColorService();
        var passwords = CreateEmptyPasswords();

        service.AddCustomUserColor(new NewCustomUserColorRequest
        {
            ColorCode = "#FF000001"
        }, passwords);

        ExpectThrows<DuplicateCustomUserColorCodeException>(() =>
        {
            service.AddCustomUserColor(new NewCustomUserColorRequest
            {
                ColorCode = "#ff000001"
            }, passwords);
        });

        MSTestAssert.HasCount(1, passwords.CustomColors);
        passwords.VerifyIntegrity();
    }


    [TestMethod]
    public void AddCustomUserColor_DuplicateColorName_ThrowsCaseInsensitively()
    {
        var service = new CustomUserColorService();
        var passwords = CreateEmptyPasswords();

        service.AddCustomUserColor(new NewCustomUserColorRequest
        {
            ColorName = "Personal",
            ColorCode = "#FF000001"
        }, passwords);

        ExpectThrows<DuplicateCustomUserColorNameException>(() =>
        {
            service.AddCustomUserColor(new NewCustomUserColorRequest
            {
                ColorName = "  personal  ",
                ColorCode = "#FF000002"
            }, passwords);
        });

        MSTestAssert.HasCount(1, passwords.CustomColors);
        MSTestAssert.AreEqual("Personal", passwords.CustomColors[0].ColorName);
        passwords.VerifyIntegrity();
    }


    [TestMethod]
    public void AddCustomUserColor_MultipleUnnamedColors_AreAllowed()
    {
        var service = new CustomUserColorService();
        var passwords = CreateEmptyPasswords();

        service.AddCustomUserColor(new NewCustomUserColorRequest
        {
            ColorCode = "#FF000001"
        }, passwords);
        service.AddCustomUserColor(new NewCustomUserColorRequest
        {
            ColorCode = "#FF000002"
        }, passwords);

        MSTestAssert.HasCount(2, passwords.CustomColors);
        MSTestAssert.IsTrue(passwords.CustomColors.All(color => color.ColorName is null));
        passwords.VerifyIntegrity();
    }


    [TestMethod]
    public void AddCustomUserColor_WhenLimitReached_ThrowsAndDoesNotModifyData()
    {
        var service = new CustomUserColorService();
        var passwords = CreateEmptyPasswords();

        for (var i = 0; i < MaxNumberOfCustomUserColors; i++)
        {
            var color = new CustomUserColor
            {
                Id = Guid.NewGuid(),
                ColorName = $"Color {i}",
                ColorCode = $"#FF{i:X6}",
                LastUpdatedAt = DateTime.UtcNow
            };
            color.GenerateIntegrityHash();
            passwords.CustomColors.Add(color);
        }
        passwords.GenerateIntegrityHash();

        ExpectThrows<LimitReachedException>(() =>
        {
            service.AddCustomUserColor(new NewCustomUserColorRequest
            {
                ColorName = "Overflow",
                ColorCode = "#FFFFFFFF"
            }, passwords);
        });

        MSTestAssert.HasCount(MaxNumberOfCustomUserColors, passwords.CustomColors);
        MSTestAssert.IsFalse(passwords.CustomColors.Any(color => color.ColorName == "Overflow"));
        passwords.VerifyIntegrity();
    }


    [TestMethod]
    public void UpdateCustomUserColor_ValidRequest_CanClearNameAndChangeColorCode()
    {
        var service = new CustomUserColorService();
        var passwords = CreateEmptyPasswords();

        service.AddCustomUserColor(new NewCustomUserColorRequest
        {
            ColorName = "Work",
            ColorCode = "#FF112233"
        }, passwords);

        var colorId = passwords.CustomColors[0].Id;
        service.UpdateCustomUserColor(new UpdateCustomUserColorRequest
        {
            Id = colorId,
            ClearColorName = true,
            ColorCode = "#ff445566"
        }, passwords);

        MSTestAssert.IsNull(passwords.CustomColors[0].ColorName);
        MSTestAssert.AreEqual("#FF445566", passwords.CustomColors[0].ColorCode);
        passwords.CustomColors[0].VerifyIntegrity();
        passwords.VerifyIntegrity();
    }


    [TestMethod]
    public void UpdateCustomUserColor_DuplicateColorCode_ThrowsAndKeepsOriginalValues()
    {
        var service = new CustomUserColorService();
        var passwords = CreateEmptyPasswords();

        service.AddCustomUserColor(new NewCustomUserColorRequest
        {
            ColorName = "Primary",
            ColorCode = "#FF000001"
        }, passwords);
        service.AddCustomUserColor(new NewCustomUserColorRequest
        {
            ColorName = "Secondary",
            ColorCode = "#FF000002"
        }, passwords);

        var secondary = passwords.CustomColors.Single(color => color.ColorName == "Secondary");
        var originalHash = passwords.IntegrityHash.ToArray();

        ExpectThrows<DuplicateCustomUserColorCodeException>(() =>
        {
            service.UpdateCustomUserColor(new UpdateCustomUserColorRequest
            {
                Id = secondary.Id,
                ColorCode = "#ff000001"
            }, passwords);
        });

        MSTestAssert.AreEqual("Secondary", secondary.ColorName);
        MSTestAssert.AreEqual("#FF000002", secondary.ColorCode);
        CollectionAssert.AreEqual(originalHash, passwords.IntegrityHash);
        passwords.VerifyIntegrity();
    }


    [TestMethod]
    public void UpdateCustomUserColor_DuplicateColorName_ThrowsAndKeepsOriginalValues()
    {
        var service = new CustomUserColorService();
        var passwords = CreateEmptyPasswords();

        service.AddCustomUserColor(new NewCustomUserColorRequest
        {
            ColorName = "Primary",
            ColorCode = "#FF000001"
        }, passwords);
        service.AddCustomUserColor(new NewCustomUserColorRequest
        {
            ColorName = "Secondary",
            ColorCode = "#FF000002"
        }, passwords);

        var secondary = passwords.CustomColors.Single(color => color.ColorName == "Secondary");
        var originalHash = passwords.IntegrityHash.ToArray();

        ExpectThrows<DuplicateCustomUserColorNameException>(() =>
        {
            service.UpdateCustomUserColor(new UpdateCustomUserColorRequest
            {
                Id = secondary.Id,
                ColorName = "  primary  "
            }, passwords);
        });

        MSTestAssert.AreEqual("Secondary", secondary.ColorName);
        MSTestAssert.AreEqual("#FF000002", secondary.ColorCode);
        CollectionAssert.AreEqual(originalHash, passwords.IntegrityHash);
        passwords.VerifyIntegrity();
    }


    [TestMethod]
    public void UpdateCustomUserColor_InvalidRequest_ThrowsAndDoesNotModifyData()
    {
        var service = new CustomUserColorService();
        var passwords = CreateEmptyPasswords();

        service.AddCustomUserColor(new NewCustomUserColorRequest
        {
            ColorName = "Accent",
            ColorCode = "#FFABCDEF"
        }, passwords);

        var color = passwords.CustomColors[0];
        var originalHash = passwords.IntegrityHash.ToArray();

        ExpectThrows<InvalidInputException>(() =>
        {
            service.UpdateCustomUserColor(new UpdateCustomUserColorRequest
            {
                Id = color.Id,
                ColorCode = "#XYZ"
            }, passwords);
        });

        MSTestAssert.AreEqual("Accent", color.ColorName);
        MSTestAssert.AreEqual("#FFABCDEF", color.ColorCode);
        CollectionAssert.AreEqual(originalHash, passwords.IntegrityHash);
        passwords.VerifyIntegrity();
    }


    [TestMethod]
    public void DeleteCustomUserColors_SingleItemList_RemovesAndCreatesTombstone()
    {
        var service = new CustomUserColorService();
        var passwords = CreateEmptyPasswords();

        service.AddCustomUserColor(new NewCustomUserColorRequest
        {
            ColorName = "Personal",
            ColorCode = "#FF123456"
        }, passwords);

        var colorId = passwords.CustomColors[0].Id;
        service.DeleteCustomUserColors([colorId], passwords);

        MSTestAssert.IsEmpty(passwords.CustomColors);
        MSTestAssert.HasCount(1, passwords.DeletedCustomColors);
        MSTestAssert.AreEqual(colorId, passwords.DeletedCustomColors[0].Id);
        passwords.DeletedCustomColors[0].VerifyIntegrity();
        passwords.VerifyIntegrity();
    }


    [TestMethod]
    public void DeleteCustomUserColors_MultipleItems_RemovesAllAndCreatesTombstones()
    {
        var service = new CustomUserColorService();
        var passwords = CreateEmptyPasswords();

        service.AddCustomUserColors(
        [
            new NewCustomUserColorRequest { ColorName = "Blue", ColorCode = "#FF010203" },
            new NewCustomUserColorRequest { ColorName = "Green", ColorCode = "#FF040506" },
            new NewCustomUserColorRequest { ColorName = "Red", ColorCode = "#FF070809" }
        ], passwords);

        var blueId = passwords.CustomColors.Single(color => color.ColorName == "Blue").Id;
        var greenId = passwords.CustomColors.Single(color => color.ColorName == "Green").Id;
        var redId = passwords.CustomColors.Single(color => color.ColorName == "Red").Id;

        service.DeleteCustomUserColors([blueId, redId], passwords);

        MSTestAssert.HasCount(1, passwords.CustomColors);
        MSTestAssert.AreEqual(greenId, passwords.CustomColors[0].Id);
        CollectionAssert.AreEquivalent(
            new[] { blueId, redId },
            passwords.DeletedCustomColors.Select(deleted => deleted.Id).ToArray());
        passwords.VerifyIntegrity();
    }


    [TestMethod]
    public void DeleteCustomUserColors_NonExistingColor_ThrowsWithoutDeletingExistingColors()
    {
        var service = new CustomUserColorService();
        var passwords = CreateEmptyPasswords();

        service.AddCustomUserColor(new NewCustomUserColorRequest
        {
            ColorName = "Existing",
            ColorCode = "#FF123456"
        }, passwords);

        var existingColorId = passwords.CustomColors[0].Id;
        var originalHash = passwords.IntegrityHash.ToArray();

        ExpectThrows<CustomUserColorNotFoundException>(() =>
        {
            service.DeleteCustomUserColors([existingColorId, Guid.NewGuid()], passwords);
        });

        MSTestAssert.HasCount(1, passwords.CustomColors);
        MSTestAssert.AreEqual(existingColorId, passwords.CustomColors[0].Id);
        MSTestAssert.IsEmpty(passwords.DeletedCustomColors);
        CollectionAssert.AreEqual(originalHash, passwords.IntegrityHash);
        passwords.VerifyIntegrity();
    }


    [TestMethod]
    public void ExportCustomUserColors_CopiesSelectedColorsWithNewIds()
    {
        var service = new CustomUserColorService();
        var sourcePasswords = CreateEmptyPasswords();
        var targetPasswords = CreateEmptyPasswords();

        service.AddCustomUserColor(new NewCustomUserColorRequest
        {
            ColorName = "Work",
            ColorCode = "#ff123456"
        }, sourcePasswords);

        var sourceColor = sourcePasswords.CustomColors[0];
        service.ExportCustomUserColors([sourceColor.Id], sourcePasswords, targetPasswords);

        MSTestAssert.HasCount(1, sourcePasswords.CustomColors);
        MSTestAssert.HasCount(1, targetPasswords.CustomColors);
        MSTestAssert.AreNotEqual(sourceColor.Id, targetPasswords.CustomColors[0].Id);
        MSTestAssert.AreEqual("Work", targetPasswords.CustomColors[0].ColorName);
        MSTestAssert.AreEqual("#FF123456", targetPasswords.CustomColors[0].ColorCode);
        sourcePasswords.VerifyIntegrity();
        targetPasswords.VerifyIntegrity();
    }


    [TestMethod]
    public void ExportCustomUserColors_DuplicateTargetName_ThrowsWithoutModifyingTarget()
    {
        var service = new CustomUserColorService();
        var sourcePasswords = CreateEmptyPasswords();
        var targetPasswords = CreateEmptyPasswords();

        service.AddCustomUserColor(new NewCustomUserColorRequest
        {
            ColorName = "Work",
            ColorCode = "#FF123456"
        }, sourcePasswords);
        service.AddCustomUserColor(new NewCustomUserColorRequest
        {
            ColorName = "work",
            ColorCode = "#FF654321"
        }, targetPasswords);

        var targetHash = targetPasswords.IntegrityHash.ToArray();
        ExpectThrows<DuplicateCustomUserColorNameException>(() =>
        {
            service.ExportCustomUserColors(
                [sourcePasswords.CustomColors[0].Id],
                sourcePasswords,
                targetPasswords);
        });

        MSTestAssert.HasCount(1, targetPasswords.CustomColors);
        MSTestAssert.AreEqual("work", targetPasswords.CustomColors[0].ColorName);
        CollectionAssert.AreEqual(targetHash, targetPasswords.IntegrityHash);
        sourcePasswords.VerifyIntegrity();
        targetPasswords.VerifyIntegrity();
    }


    [TestMethod]
    public void ConvertToCustomUserColorInfoResponses_ReturnsStableSortedResponses()
    {
        var service = new CustomUserColorService();
        var passwords = CreateEmptyPasswords();

        service.AddCustomUserColor(new NewCustomUserColorRequest
        {
            ColorName = "Work",
            ColorCode = "#FF000003"
        }, passwords);
        service.AddCustomUserColor(new NewCustomUserColorRequest
        {
            ColorName = "Personal",
            ColorCode = "#FF000002"
        }, passwords);

        var response = service.ConvertToCustomUserColorInfoResponses(passwords);

        MSTestAssert.HasCount(2, response);
        MSTestAssert.AreEqual("Personal", response[0].ColorName);
        MSTestAssert.AreEqual("Work", response[1].ColorName);
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
