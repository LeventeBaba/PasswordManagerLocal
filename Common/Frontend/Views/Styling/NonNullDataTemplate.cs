using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Metadata;

namespace PasswordManagerLocal.Common.Frontend.Views.Styling;

/// <summary>
/// An explicit ContentTemplate can be built for null content. Guard the inner
/// template so inactive shell sections create no controls, even if measured.
/// </summary>
public sealed class NonNullDataTemplate : IDataTemplate
{
    [Content]
    public IDataTemplate? Template { get; set; }

    // Claim null as well, so a template lookup does not fall back to another
    // template: this template handles it by producing no child.
    public bool Match(object? data) => data is null || Template?.Match(data) == true;

    public Control? Build(object? data) => data is null ? null : Template?.Build(data);
}
