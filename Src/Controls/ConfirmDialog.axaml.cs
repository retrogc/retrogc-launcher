using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RetroGameCard.Controls;

public partial class ConfirmDialog : Window
{
    private bool _result;

    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public static async Task<bool> ShowAsync(Window owner, string title, string message)
    {
        var dialog = new ConfirmDialog { Title = title };
        dialog.FindControl<TextBlock>("MessageText")!.Text = message;
        await dialog.ShowDialog(owner);
        return dialog._result;
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        _result = true;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _result = false;
        Close();
    }
}