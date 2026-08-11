using System.Windows;

namespace StagePlayout.App;

/// <summary>Diálogo simples de texto (dark theme da app).</summary>
public partial class InputDialog : Window
{
    private InputDialog(string title, string current)
    {
        InitializeComponent();
        TxtTitle.Text = title;
        Box.Text = current;
        Loaded += (_, _) =>
        {
            Box.SelectAll();
            Box.Focus();
        };
    }

    public string Value => Box.Text.Trim();

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    public static string? Show(Window owner, string title, string current)
    {
        var dlg = new InputDialog(title, current) { Owner = owner };
        return dlg.ShowDialog() == true && dlg.Value.Length > 0 ? dlg.Value : null;
    }
}
