using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace PasswordManagerLocal.Windows.EndpointRpc.Serialization;

public sealed class EndpointRpcSerializer
{
    private readonly Action<Type>? _serializationObserver;
    private readonly Func<Type, byte[]?>? _serializedPayloadOverride;

    public EndpointRpcSerializer()
    {
    }

    internal EndpointRpcSerializer(Action<Type> serializationObserver) =>
        _serializationObserver = serializationObserver
            ?? throw new ArgumentNullException(nameof(serializationObserver));

    internal EndpointRpcSerializer(
        Action<Type>? serializationObserver,
        Func<Type, byte[]?> serializedPayloadOverride)
    {
        _serializationObserver = serializationObserver;
        _serializedPayloadOverride = serializedPayloadOverride
            ?? throw new ArgumentNullException(nameof(serializedPayloadOverride));
    }

    public byte[] Serialize<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(typeInfo);
        _serializationObserver?.Invoke(typeof(T));
        var overriddenPayload = _serializedPayloadOverride?.Invoke(typeof(T));
        if (overriddenPayload is not null)
            return overriddenPayload;

        try
        {
            return JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new EndpointRpcPayloadException("The endpoint RPC payload could not be serialized.", exception);
        }
    }

    public T Deserialize<T>(ReadOnlySpan<byte> payload, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        try
        {
            return JsonSerializer.Deserialize(payload, typeInfo)
                ?? throw new EndpointRpcPayloadException("The endpoint RPC payload is empty.");
        }
        catch (EndpointRpcPayloadException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new EndpointRpcPayloadException("The endpoint RPC payload is malformed.", exception);
        }
    }
}
