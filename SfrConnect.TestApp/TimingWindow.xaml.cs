using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace SfrConnect.TestApp;

public partial class TimingWindow : Window
{
    private readonly Func<string> snapshot;

    public TimingWindow(Func<string> snapshot)
    {
        InitializeComponent();
        this.snapshot = snapshot;
        SetLog(snapshot());
    }

    internal void SetLog(string text)
    {
        TimingText.Text = text;
        TimingText.ScrollToEnd();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Текстовый журнал (*.txt)|*.txt",
            FileName = $"sfr-timing-{DateTime.Now:yyyyMMdd-HHmmss}.txt"
        };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, snapshot()); }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Не удалось сохранить этапы", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
