using PasswordManagerLocal.Common.Frontend.Information;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace PasswordManagerLocal.Common.Frontend.ViewModels.Information;

public sealed class HelpLinkItemViewModel : IDisposable
{
    public HelpLinkItemViewModel(InformationRoute route, string title, string summary, Action<InformationRoute> navigate,
        string categoryLabel = "")
    {
        Id = route.ContentId ?? route.Destination.ToString();
        Title = title;
        Summary = summary;
        CategoryLabel = categoryLabel;
        OpenCommand = ReactiveCommand.Create(() => navigate(route));
    }

    public string Id { get; }
    public string Title { get; }
    public string Summary { get; }
    public string CategoryLabel { get; }
    public bool HasCategoryLabel => CategoryLabel.Length != 0;
    public ReactiveCommand<RxVoid, RxVoid> OpenCommand { get; }

    public void Dispose() => OpenCommand.Dispose();
}
