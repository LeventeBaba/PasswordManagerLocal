using PasswordManagerLocal.Common.Frontend.Information;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace PasswordManagerLocal.Common.Frontend.ViewModels.Information;

public sealed class ThirdPartyLicenseItemViewModel : DisposableReactiveObject
{
    private readonly Func<ThirdPartyPlatform, string> _platformTextFactory;

    public ThirdPartyLicenseItemViewModel(
        ThirdPartyNotice notice,
        Func<ThirdPartyPlatform, string> platformTextFactory,
        Action<ThirdPartyNotice> open)
    {
        Notice = notice ?? throw new ArgumentNullException(nameof(notice));
        _platformTextFactory = platformTextFactory ??
            throw new ArgumentNullException(nameof(platformTextFactory));
        ArgumentNullException.ThrowIfNull(open);
        OpenCommand = Own(ReactiveCommand.Create(() => open(Notice)));
    }

    public ThirdPartyNotice Notice { get; }
    public string Name => Notice.Name;
    public string Version => Notice.Version;
    public string LicenseExpression => Notice.LicenseExpression;
    public string PlatformText => _platformTextFactory(Notice.Platforms);
    public string SearchText => string.Join(
        " ",
        new[] { Notice.Name, Notice.Version, Notice.LicenseExpression }
            .Concat(Notice.Packages.Select(package => package.Name)));
    public ReactiveCommand<RxVoid, RxVoid> OpenCommand { get; }

    public void RefreshLocalization() =>
        this.RaisePropertyChanged(nameof(PlatformText));
}
