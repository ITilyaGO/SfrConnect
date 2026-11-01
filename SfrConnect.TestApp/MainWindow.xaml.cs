using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SfrConnect.TestApp;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer pollTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly CardPresence presence = new();
    private readonly Queue<string> logLines = new();
    private SfrStation? station;
    private readonly StationOperations operations = new();
    private bool monitoring;
    private bool closing;
    private bool allowClose;

    public MainWindow()
    {
        InitializeComponent();
        operations.Changed += UpdateControls;
        pollTimer.Tick += async (_, _) =>
        {
            if (!operations.IsBusy && monitoring && !closing)
            {
                await RunOperationAsync(ReadCardAsync, background: true);
            }
        };
        Loaded += async (_, _) => await RunOperationAsync(RefreshDevicesAsync);
        UpdateControls();
    }

    private Task RunOperationAsync(Func<Task> operation, bool background = false)
    {
        if (closing) return Task.CompletedTask;
        var expectedStation = station;
        return operations.RunAsync(async () =>
        {
            // A failed poll may have closed the connection while a click was waiting.
            if (closing || !ReferenceEquals(station, expectedStation)) return;
            await CompleteOperationAsync(operation);
        }, background);
    }

    private async Task CompleteOperationAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (Exception error)
        {
            var unverified = error is SfrWriteUnverifiedException;
            ShowResult(unverified
                ? $"Запись не подтверждена. Номер мог измениться. Подключитесь заново и прочитайте чип. {error.Message}"
                : error.Message, true);
            AppendLog(error.ToString());
            await DisconnectAsync();
            ConnectionText.Text = "Обмен остановлен. Проверьте USB и подключитесь заново.";
        }
        finally
        {
            UpdateControls();
        }
    }

    private async Task RefreshDevicesAsync()
    {
        var selectedPath = (DevicesBox.SelectedItem as SfrDeviceInfo)?.DevicePath;
        var devices = await Task.Run(() => SfrStation.FindDevices());
        DevicesBox.ItemsSource = devices;
        DevicesBox.SelectedItem = devices.FirstOrDefault(item => item.DevicePath == selectedPath);
        if (DevicesBox.SelectedItem is null && devices.Count == 1) DevicesBox.SelectedIndex = 0;
        ConnectionText.Text = devices.Count == 0 ? "Станция не найдена. Подключите USB и нажмите «Обновить»."
            : devices.Count == 1 ? "Станция найдена. Нажмите «Подключить»." : "Выберите станцию для подключения.";
        AppendLog($"Найдено станций: {devices.Count}.");
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunOperationAsync(RefreshDevicesAsync);

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(async () =>
        {
            if (station is not null)
            {
                await DisconnectAsync();
                return;
            }
            if (DevicesBox.SelectedItem is not SfrDeviceInfo device) return;
            station = await Task.Run(() => SfrStation.Open(device.DevicePath, QueueLog));
            AppendLog($"Подключено: {device.DevicePath}");
            monitoring = true;
            ConnectionText.Text = "Подключено · чтение каждые 0,5 с";
            await ReadCardAsync();
            pollTimer.Start();
        });
    }

    private async Task DisconnectAsync()
    {
        pollTimer.Stop();
        monitoring = false;
        presence.Invalidate();
        var oldStation = station;
        station = null;
        if (oldStation is not null)
        {
            try { await Task.Run(oldStation.Dispose); }
            catch (Exception error) { AppendLog($"Закрытие соединения: {error.Message}"); }
        }
        ConnectionText.Text = "Не подключено";
    }

    private async Task ReadCardAsync()
    {
        var activeStation = station ?? throw new InvalidOperationException("Станция не подключена.");
        var card = await Task.Run(activeStation.ReadCardNumber);
        presence.Observe(card);
    }

    private async void Monitor_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(async () =>
        {
            monitoring = !monitoring;
            if (monitoring)
            {
                ConnectionText.Text = "Подключено · чтение каждые 0,5 с";
                await ReadCardAsync();
                pollTimer.Start();
            }
            else
            {
                pollTimer.Stop();
                presence.Invalidate();
                ConnectionText.Text = "Подключено · чтение остановлено";
            }
        });
    }

    private async void Beep_Click(object sender, RoutedEventArgs e) => await RunOperationAsync(async () =>
    {
        var activeStation = station ?? throw new InvalidOperationException("Станция не подключена.");
        await Task.Run(activeStation.Beep);
    });

    private async void Write_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetNewNumber(out var number) || !presence.CanWrite || !monitoring) return;
        await RunOperationAsync(async () =>
        {
            if (!presence.CanWrite || !monitoring) return;
            var activeStation = station ?? throw new InvalidOperationException("Станция не подключена.");
            presence.BeginWrite();
            ShowResult($"Записываем {number} и проверяем результат. Не убирайте чип.");
            var verified = await Task.Run(() => activeStation.WriteCardNumber(number));
            presence.Observe(verified);
            ShowResult($"Готово: номер {verified.Number} подтверждён чтением. Уберите чип перед следующей записью.");
        });
    }

    private bool TryGetNewNumber(out int number) => int.TryParse(NewNumberBox.Text.Trim(),
        NumberStyles.None, CultureInfo.InvariantCulture, out number) &&
        number <= SfrStation.MaximumWritableCardNumber;

    private void UpdateControls()
    {
        if (!IsInitialized) return;
        var connected = station is not null;
        var available = !operations.IsInteractiveBusy && !closing;
        RefreshButton.IsEnabled = available && !connected;
        DevicesBox.IsEnabled = available && !connected;
        ConnectButton.Content = connected ? "Отключить" : "Подключить";
        ConnectButton.IsEnabled = available && (connected || DevicesBox.SelectedItem is SfrDeviceInfo);
        MonitorButton.Content = monitoring ? "Остановить чтение" : "Начать чтение";
        MonitorButton.IsEnabled = available && connected;
        BeepButton.IsEnabled = available && connected;
        NewNumberBox.IsEnabled = !closing;
        var valid = TryGetNewNumber(out _);
        WriteButton.IsEnabled = available && connected && monitoring && presence.CanWrite && valid;
        ValidationText.Text = NewNumberBox.Text.Length > 0 && !valid ? "Введите целое число от 0 до 39999." : "";
        CardNumberText.Text = presence.Current?.Number.ToString(CultureInfo.InvariantCulture) ?? "—";
        CardHintText.Text = !connected ? "Подключите станцию" : !monitoring ? "Чтение остановлено"
            : presence.RequiresRemoval ? "Уберите чип перед следующей записью"
            : presence.Current is null ? "Приложите чип к станции" : "Чип доступен для записи";
    }

    private void Devices_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        DevicePathText.Text = (DevicesBox.SelectedItem as SfrDeviceInfo)?.DevicePath ?? "";
        DevicePathText.ToolTip = DevicePathText.Text;
        UpdateControls();
    }

    private void Number_TextChanged(object sender, TextChangedEventArgs e) => UpdateControls();

    private void ShowResult(string message, bool error = false)
    {
        ResultText.Text = message;
        ResultText.Foreground = error ? Brushes.Maroon : Brushes.DarkSlateBlue;
    }

    private void QueueLog(string message)
    {
        // Never block the USB worker on the dispatcher while the window is closing.
        if (!Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(new Action(() => AppendLog(message)));
    }

    private void AppendLog(string message)
    {
        logLines.Enqueue($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
        while (logLines.Count > 500) logLines.Dequeue();
        LogBox.Text = string.Join(Environment.NewLine, logLines);
        LogBox.ScrollToEnd();
    }

    private void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Текстовый журнал (*.txt)|*.txt", FileName = $"sfr-{DateTime.Now:yyyyMMdd-HHmmss}.txt" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, LogBox.Text); }
        catch (Exception error) { ShowResult($"Не удалось сохранить журнал: {error.Message}", true); }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        pollTimer.Stop();
        UpdateControls();
        await operations.Pending;
        await DisconnectAsync();
        allowClose = true;
        Close();
    }
}
