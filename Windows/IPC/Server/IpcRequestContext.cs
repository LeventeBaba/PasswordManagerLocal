using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Validation;
using System.Text.Json.Serialization.Metadata;

namespace PasswordManagerLocal.Windows.Ipc.Server;

public sealed class IpcRequestContext
{
    private readonly WindowsIpcSerializer _serializer;
    private readonly WindowsIpcContractValidator _contractValidator;

    public IpcRequestContext(
        IpcConnectionContext connection,
        IpcRequestEnvelope request,
        WindowsIpcSerializer serializer,
        WindowsIpcContractValidator? contractValidator = null)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        Request = request ?? throw new ArgumentNullException(nameof(request));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _contractValidator = contractValidator ?? new WindowsIpcContractValidator();
    }

    public IpcConnectionContext Connection { get; }
    public IpcRequestEnvelope Request { get; }

    public void EnsureNoPayload()
    {
        if (Request.Payload is not null)
            throw new IpcPayloadException("The IPC request must not contain a payload.");
    }

    public T GetRequiredPayload<T>(JsonTypeInfo<T> typeInfo)
    {
        if (Request.Payload is null)
            throw new IpcPayloadException("The IPC request payload is required.");

        var result = _serializer.Deserialize(Request.Payload, typeInfo);
        _contractValidator.ValidateForTransport(result);
        return result;
    }

    public T? GetOptionalPayload<T>(JsonTypeInfo<T> typeInfo)
    {
        if (Request.Payload is null)
            return default;

        var result = _serializer.Deserialize(Request.Payload, typeInfo);
        _contractValidator.ValidateForTransport(result);
        return result;
    }

    public IpcResponseEnvelope Success<T>(T result, JsonTypeInfo<T> typeInfo)
    {
        var response = IpcResponseEnvelope.Success(
            Request.CorrelationId,
            _serializer.Serialize(result, typeInfo));
        _contractValidator.Validate(response);
        return response;
    }

    public IpcResponseEnvelope Success() =>
        IpcResponseEnvelope.Success(Request.CorrelationId);
}
