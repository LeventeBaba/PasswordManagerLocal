namespace PasswordManagerLocal.Windows.Ipc.Client;

internal enum IpcRequestSubmissionState
{
    Created,
    Queued,
    Sending,
    Sent,
    Completed
}
