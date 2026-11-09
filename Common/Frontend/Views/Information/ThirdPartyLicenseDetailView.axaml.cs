using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using PasswordManagerLocal.Common.Frontend.ViewModels.Information;
using PasswordManagerLocal.Common.Frontend.Views.Controls;
using PasswordManagerLocal.Common.Frontend.Views.Styling;

namespace PasswordManagerLocal.Common.Frontend.Views.Information;

public partial class ThirdPartyLicenseDetailView : UserControl
{
    private ThirdPartyLicenseDetailViewModel? _observedViewModel;

    public ThirdPartyLicenseDetailView()
    {
        InitializeComponent();
        ScrollViewerStyleHelper.ApplyComfortableScrollerStyle(PageScrollViewer);
        DataContextChanged += (_, _) => AttachViewModel(DataContext as ThirdPartyLicenseDetailViewModel);
        AttachedToVisualTree += (_, _) => AttachViewModel(DataContext as ThirdPartyLicenseDetailViewModel);
        DetachedFromVisualTree += (_, _) => AttachViewModel(null);
        AttachViewModel(DataContext as ThirdPartyLicenseDetailViewModel);
    }

    private void AttachViewModel(ThirdPartyLicenseDetailViewModel? viewModel)
    {
        if (ReferenceEquals(_observedViewModel, viewModel))
            return;

        if (_observedViewModel is not null)
            _observedViewModel.PropertyChanged -= HandleViewModelPropertyChanged;

        _observedViewModel = viewModel;
        if (_observedViewModel is not null)
            _observedViewModel.PropertyChanged += HandleViewModelPropertyChanged;

        RebuildPackageLinks();
    }

    private void HandleViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null ||
            e.PropertyName == nameof(ThirdPartyLicenseDetailViewModel.PackageLinks) ||
            e.PropertyName == nameof(ThirdPartyLicenseDetailViewModel.OpenPackagePageLabel) ||
            e.PropertyName == nameof(ThirdPartyLicenseDetailViewModel.OpenPackageSourceLabel) ||
            e.PropertyName == nameof(ThirdPartyLicenseDetailViewModel.OpenSourcePageLabel))
        {
            RebuildPackageLinks();
        }
    }

    private void RebuildPackageLinks()
    {
        PackageLinksPanel.Children.Clear();
        if (_observedViewModel is null)
            return;

        foreach (var package in _observedViewModel.PackageLinks)
        {
            var row = new StackPanel { Spacing = 4 };
            row.Children.Add(new SelectableTextBlock
            {
                Text = package.DisplayName,
                TextWrapping = TextWrapping.Wrap
            });

            var links = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
            links.Children.Add(CreateLinkButton(
                package.PackagePageUri,
                _observedViewModel.OpenPackagePageLabel));
            links.Children.Add(CreateLinkButton(
                package.SourceUri,
                _observedViewModel.OpenPackageSourceLabel));
            row.Children.Add(links);
            PackageLinksPanel.Children.Add(row);
        }
    }

    private static ExternalLinkButton CreateLinkButton(Uri uri, string label)
    {
        var button = new ExternalLinkButton
        {
            ExternalUri = uri,
            Content = CreateLinkContent(label),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 8, 6)
        };
        button.Classes.Add("secondary");
        return button;
    }

    private static StackPanel CreateLinkContent(string label)
    {
        var labelText = new TextBlock
        {
            Text = label,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        labelText.Classes.Add("descriptionLink");

        var externalLinkMark = new TextBlock
        {
            Text = "↗",
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0)
        };
        externalLinkMark.Classes.Add("descriptionLink");

        var content = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 2
        };
        content.Children.Add(labelText);
        content.Children.Add(externalLinkMark);
        return content;
    }
}
