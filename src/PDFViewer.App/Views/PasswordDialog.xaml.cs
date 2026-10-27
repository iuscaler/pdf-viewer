using System.Windows;

namespace PDFViewer.App.Views;

/// <summary>암호로 보호된 PDF를 열 때 암호를 묻는 창.</summary>
public partial class PasswordDialog : Window
{
    private PasswordDialog(string message)
    {
        InitializeComponent();
        MessageText.Text = message;
    }

    /// <summary>암호를 묻는다. 취소하면 null.</summary>
    public static string? Ask(Window? owner, string message)
    {
        var dialog = new PasswordDialog(message) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.PasswordBox.Password : null;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
