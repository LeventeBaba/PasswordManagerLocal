using PasswordManagerLocal.Common.Contracts.Constants;
using static PasswordManagerLocal.Common.Contracts.Constants.DataLengthConstants;
using static PasswordManagerLocal.Common.Contracts.Validation.ValidationPatterns;

namespace PasswordManagerLocal.Common.Contracts.Validation;

public static class DataValidation
{
    public static bool IsValidEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return false;

        return EmailRegex.IsMatch(email);
    }

    public static bool IsValidUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return false;

        var u = username.Trim();
        return UsernameRegex.IsMatch(u);
    }

    public static bool IsValidFirstName(string firstName)
    {
        if (string.IsNullOrWhiteSpace(firstName))
            return false;

        var n = firstName.Trim();
        return FirstNameRegex.IsMatch(firstName);
    }

    public static bool IsValidLastName(string lastName)
    {
        if (string.IsNullOrWhiteSpace(lastName))
            return false;

        var n = lastName.Trim();
        return LastNameRegex.IsMatch(lastName);
    }

    public static bool IsValidPassword(byte[] password) =>
        password.Length >= PasswordMinLength && password.Length <= PasswordMaxLength;


    public static bool IsValidPasswordName(string passwordName) =>
        passwordName.Length >= PasswordNameMinLength && passwordName.Length <= PasswordNameMaxLength;


    public static bool IsValidDescription(string description) =>
        description.Length >= DescriptionMinLength && description.Length <= DescriptionMaxLength;


    public static bool IsValidUserDeviceName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var n = name.Trim();
        return n.Length >= UserDeviceNameMinLength && n.Length <= UserDeviceNameMaxLength;
    }


    public static bool IsValidCustomUserColorName(string? colorName)
    {
        if (colorName is null)
            return true;

        var n = colorName.Trim();
        return n.Length > 0 && n.Length <= CustomUserColorNameMaxLength;
    }


    public static bool IsValidPasswordTagName(string tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName))
            return false;

        var n = tagName.Trim();
        return n.Length > 0 && n.Length <= PasswordTagNameMaxLength;
    }


    public static bool IsValidARGBColor(string color)
    {
        if (color.Length != ARGBColorLength)
            return false;

        if (color[0] != '#')
            return false;

        return uint.TryParse(color.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out _);
    }
}
