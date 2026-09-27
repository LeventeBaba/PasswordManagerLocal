namespace PasswordManagerLocal.Windows.EndpointRpc.Client;

public sealed class EndpointRpcClientOptions
{
    public const int DefaultMaximumConcurrentOperations = 32;
    public const int MaximumConfigurableConcurrentOperations = 128;

    public EndpointRpcClientOptions(
        int maximumConcurrentOperations = DefaultMaximumConcurrentOperations)
    {
        if (maximumConcurrentOperations <= 0 ||
            maximumConcurrentOperations > MaximumConfigurableConcurrentOperations)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumConcurrentOperations),
                maximumConcurrentOperations,
                $"The endpoint operation limit must be between 1 and {MaximumConfigurableConcurrentOperations}.");
        }

        MaximumConcurrentOperations = maximumConcurrentOperations;
    }

    public int MaximumConcurrentOperations { get; }
}
