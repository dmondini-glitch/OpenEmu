using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OpenEmu.Core.Localization;

namespace OpenEmu.App.Services;

/// <summary>Small modal dialogs built in code (message, confirm, prompt, choose, progress).</summary>
public static class Dialogs
{
    private static Window Shell(string title, Control body, params Button[] buttons)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 16, 0, 0) };
        foreach (var b in buttons) { b.MinWidth = 90; bar.Children.Add(b); }
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 8, MinWidth = 380, MaxWidth = 640 };
        panel.Children.Add(body); panel.Children.Add(bar);
        return new Window { Title = title, Content = panel, SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, ShowInTaskbar = false };
    }

    public static Task Message(Window owner, string title, string text)
    {
        var ok = new Button { Content = L.T("common.ok"), IsDefault = true };
        var w = Shell(title, new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, ok);
        ok.Click += (_, _) => w.Close();
        return w.ShowDialog(owner);
    }

    public static async Task<bool> Confirm(Window owner, string title, string text, string? yes = null, string? no = null)
    {
        var result = false;
        var y = new Button { Content = yes ?? L.T("common.yes"), IsDefault = true };
        var n = new Button { Content = no ?? L.T("common.no"), IsCancel = true };
        var w = Shell(title, new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, n, y);
        y.Click += (_, _) => { result = true; w.Close(); };
        n.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
        return result;
    }

    public static async Task<string?> Prompt(Window owner, string title, string label, string initial = "")
    {
        string? result = null;
        var tb = new TextBox { Text = initial, MinWidth = 320 };
        var ok = new Button { Content = L.T("common.ok"), IsDefault = true };
        var cancel = new Button { Content = L.T("common.cancel"), IsCancel = true };
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(new TextBlock { Text = label }); body.Children.Add(tb);
        var w = Shell(title, body, cancel, ok);
        ok.Click += (_, _) => { result = tb.Text; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        w.Opened += (_, _) => { tb.Focus(); tb.SelectAll(); };
        await w.ShowDialog(owner);
        return result;
    }

    public static async Task<T?> Choose<T>(Window owner, string title, string label, IReadOnlyList<T> items, Func<T, string> display) where T : class
    {
        T? result = null;
        var lb = new ListBox { ItemsSource = items.Select(display).ToList(), MinWidth = 320, MaxHeight = 360, SelectedIndex = 0 };
        var ok = new Button { Content = L.T("common.ok"), IsDefault = true };
        var cancel = new Button { Content = L.T("common.cancel"), IsCancel = true };
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }); body.Children.Add(lb);
        var w = Shell(title, body, cancel, ok);
        ok.Click += (_, _) => { if (lb.SelectedIndex >= 0) result = items[lb.SelectedIndex]; w.Close(); };
        lb.DoubleTapped += (_, _) => { if (lb.SelectedIndex >= 0) result = items[lb.SelectedIndex]; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
        return result;
    }

    public static async Task<T> RunWithProgress<T>(Window owner, string title, Func<IProgress<double>, Task<T>> work)
    {
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Value = 0, MinWidth = 360, IsIndeterminate = true };
        var label = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap };
        var body = new StackPanel { Spacing = 10 }; body.Children.Add(label); body.Children.Add(bar);
        var w = Shell(title, body);
        var progress = new Progress<double>(p => { bar.IsIndeterminate = false; bar.Value = p; });
        var task = work(progress);
        _ = task.ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(w.Close));
        await w.ShowDialog(owner);
        return await task;
    }

    public static Task RunWithProgress(Window owner, string title, Func<IProgress<double>, Task> work)
        => RunWithProgress<bool>(owner, title, async p => { await work(p); return true; });
}
