using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace PasswordManagerLocal.Windows.Ipc.Serialization;

public sealed class WindowsIpcSerializer
{
    public byte[] Serialize<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);

        try
        {
            return JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        }
        catch (JsonException exception)
        {
            throw new IpcPayloadException("The IPC payload could not be serialized.", exception);
        }
        catch (NotSupportedException exception)
        {
            throw new IpcPayloadException("The IPC payload type is not registered.", exception);
        }
    }

    public T Deserialize<T>(ReadOnlySpan<byte> payload, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);

        try
        {
            return JsonSerializer.Deserialize(payload, typeInfo)
                ?? throw new IpcPayloadException("The IPC payload is empty.");
        }
        catch (IpcPayloadException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new IpcPayloadException("The IPC payload is malformed.", exception);
        }
        catch (NotSupportedException exception)
        {
            throw new IpcPayloadException("The IPC payload type is not registered.", exception);
        }
    }
}
