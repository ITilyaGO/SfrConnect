using System.Globalization;
using System.Text;
using SfrConnect;

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("SfrConnect — проверка USB HID станции SFR");
Console.WriteLine($"ОС: {Environment.OSVersion}; .NET: {Environment.Version}; процесс: {(Environment.Is64BitProcess ? "x64" : "x86")}");

if (args.Length > 0)
{
    return Execute(args);
}

PrintHelp();
while (true)
{
    Console.Write("\nsfr> ");
    var line = Console.ReadLine();
    if (line is null || line.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase))
    {
        return 0;
    }
    var command = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (command.Length > 0)
    {
        Execute(command);
    }
}

static int Execute(string[] command)
{
    try
    {
        var verb = command[0].ToLowerInvariant();
        if (verb is "help" or "--help" or "-h")
        {
            PrintHelp();
            return 0;
        }

        var listAll = verb == "list" && command.Length == 2 && command[1] == "--all";
        int? deviceNumber = null;
        int? expectedNumber = null;
        int newNumber = 0;
        if (verb == "write")
        {
            if (command.Length is < 2 or > 4)
            {
                throw new ArgumentException("Формат: write <новый номер> или write <текущий номер> <новый номер> [номер устройства].");
            }
            int newNumberIndex = 1;
            if (command.Length >= 3)
            {
                if (!int.TryParse(command[1], NumberStyles.None, CultureInfo.InvariantCulture, out var current) ||
                    current > 7999999)
                {
                    throw new ArgumentException("Ожидаемый текущий номер должен быть от 0 до 7999999.");
                }
                expectedNumber = current;
                newNumberIndex = 2;
            }
            if (!int.TryParse(command[newNumberIndex], NumberStyles.None, CultureInfo.InvariantCulture, out newNumber) ||
                newNumber > SfrStation.MaximumWritableCardNumber)
            {
                throw new ArgumentException("Новый номер должен быть от 0 до 39999.");
            }
            if (command.Length == 4)
            {
                if (!int.TryParse(command[3], NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 1)
                {
                    throw new ArgumentException("Номер устройства должен быть положительным.");
                }
                deviceNumber = index;
            }
        }
        else if (verb is "open" or "beep" or "read")
        {
            if (command.Length > 2 || (command.Length == 2 &&
                (!int.TryParse(command[1], NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 1)))
            {
                throw new ArgumentException("Укажите положительный номер устройства, например: beep 1.");
            }
            if (command.Length == 2)
            {
                deviceNumber = int.Parse(command[1], CultureInfo.InvariantCulture);
            }
        }
        else if (verb != "list" || (command.Length != 1 && !listAll))
        {
            throw new ArgumentException("Неизвестная команда или параметры. Введите help.");
        }

        var devices = SfrStation.FindDevices(listAll);
        ShowDevices(devices);
        if (verb == "list")
        {
            return 0;
        }
        if (devices.Count == 0)
        {
            throw new IOException("SFR с VID=2047, PID=0301 не найдена. Команда list --all покажет остальные HID-устройства.");
        }
        if (deviceNumber is null && devices.Count != 1)
        {
            throw new ArgumentException("Найдено несколько интерфейсов. Укажите номер из списка, например: open 2.");
        }
        var selectedIndex = (deviceNumber ?? 1) - 1;
        if (selectedIndex >= devices.Count)
        {
            throw new ArgumentException("Устройства с таким номером нет в текущем списке.");
        }

        var selected = devices[selectedIndex];
        Log($"Открытие: {selected.DevicePath}");
        using (var station = SfrStation.Open(selected.DevicePath, Log))
        {
            Log("Соединение открыто.");
            if (verb == "beep")
            {
                station.Beep();
                Console.WriteLine("Проверьте сигнал/индикатор на станции. Отсутствие ошибки записи само по себе не подтверждает сигнал.");
            }
            else if (verb == "read")
            {
                var card = station.ReadCardNumber();
                if (card is null)
                {
                    Console.WriteLine("Чип не обнаружен или недоступен для чтения (ответ 2A 06). Приложите чип и повторите read.");
                }
                else
                {
                    Console.WriteLine($"Номер чипа: {card.Number}");
                    Console.WriteLine($"Ячейка 01: {card.MarkerHex} (не уникальный серийный номер).");
                    if (card.Number == 0)
                    {
                        Console.WriteLine("В чипе записан нулевой номер.");
                    }
                }
            }
            else if (verb == "write")
            {
                var card = expectedNumber.HasValue
                    ? station.WriteCardNumber(expectedNumber.Value, newNumber)
                    : station.WriteCardNumber(newNumber);
                Console.WriteLine($"Номер проверен чтением: {card.Number}.");
            }
        }
        Log("Соединение закрыто.");
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"[{DateTimeOffset.Now:O}] ОШИБКА: {error}");
        Console.Error.WriteLine("Проверьте USB-подключение. Закройте другие программы, работающие со станцией, и повторите поиск.");
        return 1;
    }
}

static void ShowDevices(IReadOnlyList<SfrDeviceInfo> devices)
{
    Console.WriteLine($"Найдено HID-интерфейсов: {devices.Count}");
    for (var index = 0; index < devices.Count; index++)
    {
        var device = devices[index];
        Console.WriteLine($"[{index + 1}] {device.ProductName} — VID={device.VendorId:X4}, PID={device.ProductId:X4}{(device.IsSfr ? " [SFR]" : "")}");
        Console.WriteLine($"    Input={device.InputReportLength?.ToString() ?? "?"}, Output={device.OutputReportLength?.ToString() ?? "?"} байт (с Report ID)");
        Console.WriteLine($"    {device.DevicePath}");
        if (device.InspectionError is not null)
        {
            Console.WriteLine($"    Не удалось прочитать свойства: {device.InspectionError}");
        }
    }
}

static void Log(string message) => Console.WriteLine($"[{DateTimeOffset.Now:O}] {message}");

static void PrintHelp()
{
    Console.WriteLine("""
        list          — найти станции SFR (VID=2047, PID=0301)
        list --all    — показать все HID-интерфейсы, без отправки команд
        open [номер]  — открыть и закрыть станцию, без отправки команд
        beep [номер]  — открыть станцию, отправить один сигнал, закрыть
        read [номер]  — прочитать номер приложенного чипа, без записи
        write <новый> — записать номер 0–39999, текущий номер знать не нужно
        write <текущий> <новый> [номер устройства] — запись с проверкой старого номера
        help          — показать команды
        exit          — завершить интерактивную проверку
        Номер берётся из списка SFR команды list, не из list --all.
        Без номера устройства команды работают только при одном найденном интерфейсе.
        Чип для проверки сигнала не нужен.
        Для read/write держите один чип на станции до окончания команды.
        Пример: write 123 — записать номер 123 без очистки отметок.
        Короткая команда write <новый> требует одного подключённого интерфейса SFR.
        """);
}
