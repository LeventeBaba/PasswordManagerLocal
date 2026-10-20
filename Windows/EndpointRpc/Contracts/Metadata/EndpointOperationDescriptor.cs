using PasswordManagerLocal.Windows.EndpointRpc.Contracts;

namespace PasswordManagerLocal.Windows.EndpointRpc.Metadata;

public sealed record EndpointOperationDescriptor(
    EndpointOperationId OperationId,
    string MethodName,
    Type RequestType,
    Type ResponseType,
    EndpointMutationCommitModel CommitModel,
    int MaximumRequestPayloadSize,
    int MaximumResponsePayloadSize,
    int MaximumLargeResponsePayloadSize = 0)
{
    public bool MutatesState => CommitModel != EndpointMutationCommitModel.NoDurableMutation;
    public bool CanPartiallyCommit => CommitModel is EndpointMutationCommitModel.CommitThenFollowUp or
        EndpointMutationCommitModel.MultiStageRecoverableCommit;
    public bool SupportsLargeResponse => MaximumLargeResponsePayloadSize > 0;
    public int MaximumLogicalResponsePayloadSize => SupportsLargeResponse
        ? MaximumLargeResponsePayloadSize
        : MaximumResponsePayloadSize;
}
