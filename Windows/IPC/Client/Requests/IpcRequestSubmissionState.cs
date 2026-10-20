namespace PasswordManagerLocal.Windows.Ipc.Client.Requests;

internal enum IpcRequestSubmissionState
{
    Created,
    Queued,
    Sending,
    Sent,
    Completed
}
