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
    private SfrNumberWriter? numberWriter;
    private SfrCardInfo? cardInfo;
    private CancellationTokenSource? assignmentCancellation;
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
        catch (SfrProtectedCardException error)
        {
            ShowResult(error.Message, true);
            QueueLog($"{error.Message} Ячейка 04: {error.HeaderHex}.");
        }
        catch (Exception error)
        {
            var unverified = error is SfrWriteUnverifiedException;
            ShowResult(unverified
                ? $"Запись не подтверждена. Номер мог измениться. Подключитесь заново и прочитайте чип. {error.Message}"
                : error.Message, true);
            QueueLog(error.ToString());
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
            numberWriter = new SfrNumberWriter(station);
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
        cardInfo = null;
        var oldStation = station;
        station = null;
        numberWriter = null;
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
        try
        {
            cardInfo = await Task.Run(activeStation.ReadCardInfo);
            if (cardInfo is null) presence.Observe(null);
            else if (cardInfo.Kind == SfrCardKind.Participant)
                presence.Observe(new SfrCardNumber(cardInfo.Number!.Value, cardInfo.MarkerHex));
            else presence.Invalidate();
        }
        catch (SfrCardNotReadyException)
        {
            // A partial read does not prove either a stable card or a confirmed removal.
            presence.Invalidate();
            cardInfo = null;
            QueueLog("Чип снят или перемещён во время чтения. Ожидание продолжается.");
        }
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
        if (!TryGetNewNumber(out var number)) return;
        await RunCardCommandAsync(number, ClearBeforeWriteBox.IsChecked == true);
    }

    private async void Clear_Click(object sender, RoutedEventArgs e) => await RunCardCommandAsync(null, true);

    private async Task RunCardCommandAsync(int? number, bool clearCard)
    {
        var action = number.HasValue
            ? (clearCard ? $"Очистить отметки и записать {number}" : $"Записать {number}")
            : "Очистить отметки, сохранив номер";
        await RunOperationAsync(async () =>
        {
            var writer = numberWriter ?? throw new InvalidOperationException("Станция не подключена.");
            using var cancellation = new CancellationTokenSource();
            assignmentCancellation = cancellation;
            var progress = new Progress<SfrAssignmentStage>(stage =>
            {
                if (!ReferenceEquals(assignmentCancellation, cancellation) || closing) return;
                ShowResult(stage switch
                {
                    SfrAssignmentStage.WaitingForRemoval => $"{action}. Уберите предыдущий чип, затем приложите следующий.",
                    SfrAssignmentStage.WaitingForCard => $"{action}. Приложите чип к станции.",
                    SfrAssignmentStage.Clearing => "Очищаем отметки и проверяем ячейки. Не убирайте чип до завершения.",
                    _ => $"Записываем {number} и проверяем. Не убирайте чип."
                });
                CancelAssignmentButton.IsEnabled = stage is SfrAssignmentStage.WaitingForCard or SfrAssignmentStage.WaitingForRemoval && !cancellation.IsCancellationRequested;
            });
            ShowResult($"{action}. Приложите чип к станции.");
            UpdateControls();
            try
            {
                var result = number.HasValue
                    ? await writer.AssignAsync(number.Value, cancellation.Token, progress, presence.RequiresRemoval, clearCard)
                    : await writer.ClearAsync(cancellation.Token, progress, presence.RequiresRemoval);
                presence.Observe(result.Card);
                cardInfo = new SfrCardInfo(SfrCardKind.Participant, result.Card.Number, result.Card.MarkerHex, result.CardCleared ? "05000000" : cardInfo?.HeaderHex ?? "");
                presence.MarkWriteAttempt();
                var completed = result.CardCleared
                    ? $"Отметки очищены, номер {result.Card.Number} проверен"
                    : $"Номер {result.Card.Number} проверен";
                ShowResult(result.SignalSent
                    ? $"{completed}, сигнал отправлен. Можно убрать чип."
                    : $"{completed}, но сигнал не отправлен: {result.SignalError}", !result.SignalSent);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                presence.Invalidate();
                ShowResult("Ожидание отменено. Данные чипа не изменялись.");
            }
            catch (SfrWriteUnverifiedException)
            {
                presence.MarkWriteAttempt();
                throw;
            }
            catch (SfrCardUpdateUnverifiedException)
            {
                presence.MarkWriteAttempt();
                throw;
            }
            finally
            {
                assignmentCancellation = null;
                UpdateControls();
            }
        });
    }

    private void CancelAssignment_Click(object sender, RoutedEventArgs e)
    {
        assignmentCancellation?.Cancel();
        CancelAssignmentButton.IsEnabled = false;
        ShowResult("Отменяем ожидание. Если изменение чипа уже началось, дождёмся результата проверки.");
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
        NewNumberBox.IsEnabled = !closing && assignmentCancellation is null;
        var valid = TryGetNewNumber(out _);
        var protectedCard = cardInfo?.Kind == SfrCardKind.Unknown;
        WriteButton.IsEnabled = available && connected && valid && !protectedCard;
        ClearButton.IsEnabled = available && connected && !protectedCard;
        ClearBeforeWriteBox.IsEnabled = available;
        CancelAssignmentButton.Visibility = assignmentCancellation is null ? Visibility.Collapsed : Visibility.Visible;
        CancelAssignmentButton.IsEnabled = assignmentCancellation is not null && !assignmentCancellation.IsCancellationRequested && !closing;
        ValidationText.Text = NewNumberBox.Text.Length > 0 && !valid ? "Введите целое число от 0 до 39999." : "";
        CardNumberText.Text = presence.Current?.Number.ToString(CultureInfo.InvariantCulture) ?? "—";
        CardHintText.Text = !connected ? "Подключите станцию"
            : protectedCard ? "Неизвестный формат — изменение запрещено"
            : cardInfo?.Kind == SfrCardKind.Service ? "Служебный чип — при записи или очистке станет обычным"
            : !monitoring ? "Чтение остановлено"
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
        var timestamp = DateTime.Now;
        // Never block the USB worker on the dispatcher while the window is closing.
        if (!Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(new Action(() => AppendLog(message, timestamp)));
    }

    private void AppendLog(string message, DateTime? timestamp = null)
    {
        logLines.Enqueue($"[{timestamp ?? DateTime.Now:HH:mm:ss.fff}] {message}");
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
        assignmentCancellation?.Cancel();
        pollTimer.Stop();
        UpdateControls();
        await operations.Pending;
        await DisconnectAsync();
        allowClose = true;
        Close();
    }
}
