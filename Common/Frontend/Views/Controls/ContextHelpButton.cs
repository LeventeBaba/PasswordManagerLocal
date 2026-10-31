using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PasswordManagerLocal.Common.Frontend.Views.Controls;

/// <summary>A navigation-only help action. Carries a public topic ID, never form values.</summary>
public sealed class ContextHelpButton : Button
{
    public ContextHelpButton()
    {
        Classes.Add("contextHelpButton");
    }

    // Reuse the application's Button theme and Button.secondary styles.
    protected override Type StyleKeyOverride => typeof(Button);

    public static readonly StyledProperty<string?> TopicIdProperty =
        AvaloniaProperty.Register<ContextHelpButton, string?>(nameof(TopicId));

    public static readonly RoutedEvent<ContextHelpRequestedEventArgs> ContextHelpRequestedEvent =
        RoutedEvent.Register<ContextHelpButton, ContextHelpRequestedEventArgs>(
            nameof(ContextHelpRequested), RoutingStrategies.Bubble);

    public string? TopicId
    {
        get => GetValue(TopicIdProperty);
        set => SetValue(TopicIdProperty, value);
    }

    public event EventHandler<ContextHelpRequestedEventArgs> ContextHelpRequested
    {
        add => AddHandler(ContextHelpRequestedEvent, value);
        remove => RemoveHandler(ContextHelpRequestedEvent, value);
    }

    protected override void OnClick()
    {
        base.OnClick();
        RaiseEvent(new ContextHelpRequestedEventArgs(TopicId, this));
    }
}
