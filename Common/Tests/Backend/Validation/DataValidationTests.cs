using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Contracts.Validation;
using System.Text;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Validation;

[TestClass]
public sealed class DataValidationTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void IsValidEmail_AcceptsConventionalAddressAndRejectsMissingOrInvalidDomainSeparators()
    {
        MSTestAssert.IsTrue(DataValidation.IsValidEmail("user.name+tag@example-domain.com"));
        MSTestAssert.IsFalse(DataValidation.IsValidEmail("user@examplecom"));
        MSTestAssert.IsFalse(DataValidation.IsValidEmail("user@example#com"));
        MSTestAssert.IsFalse(DataValidation.IsValidEmail("user@.com"));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void IsValidUsername_EnforcesAllowedCharactersAndTrimmedLengthBoundaries()
    {
        MSTestAssert.IsTrue(DataValidation.IsValidUsername(" abc "));
        MSTestAssert.IsTrue(DataValidation.IsValidUsername(new string('a', DataLengthConstants.UsernameMaxLength)));
        MSTestAssert.IsFalse(DataValidation.IsValidUsername("ab"));
        MSTestAssert.IsFalse(DataValidation.IsValidUsername(new string('a', DataLengthConstants.UsernameMaxLength + 1)));
        MSTestAssert.IsFalse(DataValidation.IsValidUsername("user name"));
        MSTestAssert.IsFalse(DataValidation.IsValidUsername("user!"));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void NameValidation_AcceptsUnicodeLettersAndRejectsDigitsOrPunctuation()
    {
        MSTestAssert.IsTrue(DataValidation.IsValidFirstName("Árvíztűrő"));
        MSTestAssert.IsTrue(DataValidation.IsValidLastName("Őz"));
        MSTestAssert.IsFalse(DataValidation.IsValidFirstName("John2"));
        MSTestAssert.IsFalse(DataValidation.IsValidLastName("Smith-Jones"));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void IsValidARGBColor_RequiresHashAndExactlyEightHexDigits()
    {
        MSTestAssert.IsTrue(DataValidation.IsValidARGBColor("#00FF7FA0"));
        MSTestAssert.IsTrue(DataValidation.IsValidARGBColor("#ffffffff"));
        MSTestAssert.IsFalse(DataValidation.IsValidARGBColor("00FF7FA0"));
        MSTestAssert.IsFalse(DataValidation.IsValidARGBColor("#FFF"));
        MSTestAssert.IsFalse(DataValidation.IsValidARGBColor("#GGFF7FA0"));
    }


    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void CustomUserColorRequests_ValidateOptionalNameAndMandatoryArgbColor()
    {
        var valid = new NewCustomUserColorRequest
        {
            ColorName = null,
            ColorCode = "#FF112233"
        };

        MSTestAssert.IsTrue(valid.Validate(out var validErrors));
        MSTestAssert.IsEmpty(validErrors);

        var invalid = new NewCustomUserColorRequest
        {
            ColorName = string.Empty,
            ColorCode = "112233"
        };

        MSTestAssert.IsFalse(invalid.Validate(out var invalidErrors));
        CollectionAssert.AreEquivalent(new[] { "ColorName", "ColorCode" }, invalidErrors);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void PasswordDefaults_UseTealColor()
    {
        MSTestAssert.AreEqual(PasswordConstants.DefaultPasswordColor, new NewPasswordRequest().Color);
        MSTestAssert.AreEqual(PasswordConstants.DefaultPasswordColor, new SecurePassword().Color);
        MSTestAssert.AreEqual("#FF14B8A6", PasswordConstants.DefaultPasswordColor);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void UpdatePasswordRequest_AllowsEmptyDescriptionUpdate()
    {
        var request = new UpdatePasswordRequest
        {
            Id = Guid.NewGuid(),
            Description = string.Empty
        };

        var valid = request.Validate(out var errors);

        MSTestAssert.IsTrue(valid);
        MSTestAssert.IsEmpty(errors);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void RegistrationRequest_ReportsEveryInvalidFieldWithoutHidingLaterErrors()
    {
        var request = new RegistrationRequest
        {
            Username = "x",
            Password = [],
            FirstName = "123",
            LastName = "!",
            Email = "invalid"
        };

        var valid = request.Validate(out var errors);

        MSTestAssert.IsFalse(valid);
        CollectionAssert.AreEquivalent(
            new[] { "Email", "Username", "FirstName", "LastName", "Password" },
            errors);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void NewPasswordRequest_EnforcesAllConfiguredLengthBoundaries()
    {
        var request = new NewPasswordRequest
        {
            Name = new string('N', DataLengthConstants.PasswordNameMaxLength),
            Description = new string('D', DataLengthConstants.DescriptionMaxLength),
            Color = "#FF112233",
            Password = Encoding.UTF8.GetBytes("x")
        };

        MSTestAssert.IsTrue(request.Validate(out var validErrors));
        MSTestAssert.IsEmpty(validErrors);

        request.Name += "N";
        request.Description += "D";
        request.Password = new byte[DataLengthConstants.PasswordMaxLength + 1];

        MSTestAssert.IsFalse(request.Validate(out var invalidErrors));
        CollectionAssert.AreEquivalent(new[] { "Name", "Description", "Password" }, invalidErrors);
    }
}
