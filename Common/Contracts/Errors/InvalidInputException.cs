namespace PasswordManagerLocal.Common.Contracts.Errors;

public class InvalidInputException : ArgumentException
{
    public List<string> Errors = new List<string>();

    public InvalidInputException(List<string>? errors = null) : base("Invalid inputs")
    {
        if (errors is not null)
            Errors = errors;
    }
}
