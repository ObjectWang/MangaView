using System.Windows;
using System.Windows.Controls;
using MangaView.Core;

namespace MangaView.App;

public sealed class TextPromptDialog : Window
{
    private readonly TextBox _input;
    private string? _result;

    private TextPromptDialog(string title, string labelText, string initialValue)
    {
        Title = title;
        Width = 430;
        Height = 170;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var panel = new DockPanel { Margin = new Thickness(16) };
        var label = new TextBlock { Text = labelText, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(label, Dock.Top);
        panel.Children.Add(label);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);

        _input = new TextBox { Text = initialValue, MinWidth = 360 };
        _input.SelectAll();
        _input.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) Accept();
            if (e.Key == System.Windows.Input.Key.Escape) Close();
        };
        panel.Children.Add(_input);

        var ok = new Button { Content = "确定", Width = 76, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = "取消", Width = 76, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Content = panel;
        Loaded += (_, _) => _input.Focus();
    }

    public static string? Show(Window owner, string title, string label, string initialValue)
    {
        var dialog = new TextPromptDialog(title, label, initialValue) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog._result : null;
    }

    private void Accept()
    {
        _result = _input.Text.Trim();
        if (_result.Length == 0) return;
        DialogResult = true;
    }
}

public sealed class BookmarkManagerWindow : Window
{
    private readonly BookmarkStore _store;
    private readonly string _sourceKey;
    private readonly ListBox _list = new();

    public BookmarkManagerWindow(BookmarkStore store, string sourceKey)
    {
        _store = store;
        _sourceKey = sourceKey;
        Title = "管理书签";
        Width = 560;
        Height = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var root = new DockPanel { Margin = new Thickness(14) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var rename = new Button { Content = "重命名", Width = 86, Margin = new Thickness(0, 0, 8, 0) };
        rename.Click += (_, _) => RenameSelected();
        var delete = new Button { Content = "删除", Width = 86, Margin = new Thickness(0, 0, 8, 0) };
        delete.Click += (_, _) => DeleteSelected();
        var close = new Button { Content = "关闭", Width = 86, IsCancel = true };
        buttons.Children.Add(rename);
        buttons.Children.Add(delete);
        buttons.Children.Add(close);

        _list.DisplayMemberPath = nameof(BookmarkDisplay.Text);
        root.Children.Add(_list);
        Content = root;
        Refresh();
    }

    private void Refresh()
    {
        _list.ItemsSource = _store.Get(_sourceKey)
            .Select(b => new BookmarkDisplay(b, $"{b.Name}  ·  第 {b.PageIndex + 1} 页"))
            .ToArray();
    }

    private BookmarkDisplay? Selected => _list.SelectedItem as BookmarkDisplay;

    private void RenameSelected()
    {
        if (Selected is not { } selected) return;
        string? name = TextPromptDialog.Show(this, "重命名书签", "新名称：", selected.Bookmark.Name);
        if (name is null) return;
        _store.Rename(_sourceKey, selected.Bookmark.Id, name);
        Refresh();
    }

    private void DeleteSelected()
    {
        if (Selected is not { } selected) return;
        if (MessageBox.Show(this, $"删除书签“{selected.Bookmark.Name}”？", "MangaView",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _store.Delete(_sourceKey, selected.Bookmark.Id);
        Refresh();
    }

    private sealed record BookmarkDisplay(Bookmark Bookmark, string Text);
}
