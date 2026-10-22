using PasswordManagerLocal.Common.Frontend.Information;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace PasswordManagerLocal.Common.Frontend.ViewModels.Information;

public sealed class HelpLinkItemViewModel : IDisposable
{
    public HelpLinkItemViewModel(InformationRoute route, string title, string summary, Action<InformationRoute> navigate)
    {
        Id = route.ContentId ?? route.Destination.ToString();
        Title = title;
        Summary = summary;
        OpenCommand = ReactiveCommand.Create(() => navigate(route));
    }

    public string Id { get; }
    public string Title { get; }
    public string Summary { get; }
    public ReactiveCommand<RxVoid, RxVoid> OpenCommand { get; }

    public void Dispose() => OpenCommand.Dispose();
}
