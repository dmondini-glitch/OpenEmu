using Avalonia.Markup.Xaml;
using OpenEmu.Core.Localization;

namespace OpenEmu.App.Services;

/// <summary>XAML markup extension: Text="{s:T game.play}".</summary>
public sealed class TExtension : MarkupExtension
{
    public string Key { get; set; } = "";
    public TExtension() { }
    public TExtension(string key) => Key = key;
    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Key);
}
