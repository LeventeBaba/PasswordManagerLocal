using Avalonia.Interactivity;

namespace PasswordManagerLocal.Common.Frontend.Views.Controls;

public sealed class ContextHelpRequestedEventArgs : RoutedEventArgs
{
    public ContextHelpRequestedEventArgs(string? topicId, ContextHelpButton source)
        : base(ContextHelpButton.ContextHelpRequestedEvent, source) => TopicId = topicId;

    public string? TopicId { get; }
}
