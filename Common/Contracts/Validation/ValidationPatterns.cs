using PasswordManagerLocal.Common.Contracts.Constants;
using System.Text.RegularExpressions;
using static PasswordManagerLocal.Common.Contracts.Constants.DataLengthConstants;

namespace PasswordManagerLocal.Common.Contracts.Validation;

public static class ValidationPatterns
{
    public static readonly Regex EmailRegex = new(
    $@"^(?=.{{{EmailMinLength},{EmailMaxLength}}}$)[a-z0-9._%+-]+@[a-z0-9-]+(?:\.[a-z0-9-]+)*\.[a-z]{{2,}}$",
    RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );


    public static readonly Regex UsernameRegex = new(
        $@"^(?=.{{{UsernameMinLength},{UsernameMaxLength}}}$)[A-Za-z0-9_\-']+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant
    );


    public static readonly Regex FirstNameRegex = new(
        $@"^(?=.{{{FirstNameMinLength},{FirstNameMaxLength}}}$)\p{{L}}+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant
    );


    public static readonly Regex LastNameRegex = new(
        $@"^(?=.{{{LastNameMinLength},{LastNameMaxLength}}}$)\p{{L}}+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant
    );


}
