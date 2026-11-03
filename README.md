# SfrConnect

Библиотека C# для работы с USB HID станцией сопряжения SFR: чтение номера чипа,
присвоение нового номера и очистка отметок. Предназначена для встраивания в
настольную программу, например OrientCalculator.

Основной сценарий: **передать номер → дождаться чипа → записать → проверить →
подать сигнал → вернуть результат вызывающей программе**.

Библиотека не связывает чип с участником в базе данных. Это делает вызывающая
программа после подтверждённого результата. Интеграция в OrientCalculator пока
не выполнена; в репозитории есть самостоятельное тестовое окно.

## Документация

- [Руководство по внедрению](docs/integration.md) — подключение, API, результаты,
  отмена, настройки и работа из WPF.
- [Диагностика и проверка на станции](docs/diagnostics.md) — тестовое окно,
  консоль, журнал пакетов и длительность этапов.
- [Протокол и ограничения](docs/protocol.md) — формат памяти, команды и
  подтверждённые особенности оборудования.

## Возможности

| Действие | API |
|---|---|
| Найти станции, выбрать и открыть соединение | `SfrStation.FindDevices()`, `Open(...)` |
| Прочитать тип и номер чипа | `station.ReadCardInfo()` |
| Дождаться чипа и присвоить номер | `writer.AssignAsync(number)` |
| Присвоить номер с очисткой отметок | `writer.AssignAsync(number, clearCard: true)` |
| Очистить отметки, сохранив номер | `writer.ClearAsync()` |
| Подать сигнал отдельно | `station.Beep()` |

При записи обычного чипа без очистки меняется только номер. **Служебный чип
автоматически становится обычным с очисткой**, даже при `clearCard: false`.
Неизвестный формат не изменяется.

## Требования

- Windows и совместимая USB HID станция SFR: текущий профиль — VID `2047`, PID `0301`.
- Библиотека собрана для .NET 8; тестовое окно — WPF `net8.0-windows`.
- Зависимость: HidSharp `2.6.4`, восстанавливается через NuGet.
- Новый номер: **0–39999**. Очистка поддерживает профиль памяти на 128 ячеек.

Это управляемая .NET DLL, а не COM-компонент и не библиотека для `DllImport`.
Совместимость со всеми моделями SFR не заявляется.

## Сборка и проверка

Из корня репозитория:

```powershell
dotnet restore SfrConnect.sln
dotnet build SfrConnect.sln -c Release
dotnet test SfrConnect.Tests/SfrConnect.Tests.csproj -c Release
dotnet run --project SfrConnect.TestApp -c Release
```

Либо откройте `SfrConnect.sln` в Visual Studio с поддержкой .NET 8 и WPF,
назначьте `SfrConnect.TestApp` запускаемым проектом и нажмите Ctrl+F5.
Автоматические тесты используют имитацию транспорта и не обращаются к станции.

## Быстрый старт из своей программы

Подключите `SfrConnect/SfrConnect.csproj` через `ProjectReference`.
Пример записывает номер 123 в один чип; текущий номер указывать не нужно.
Первоначальный поиск ограничен токеном на 30 секунд. После начала записи отмена
не прерывает проверку результата.

```csharp
using SfrConnect;

var devices = SfrStation.FindDevices();
if (devices.Count != 1)
{
    Console.WriteLine("Подключите одну станцию или выберите DevicePath из списка.");
    return;
}

using var station = SfrStation.Open(devices[0].DevicePath, options: new SfrStationOptions
{
    VerificationReturnTimeoutMs = 5000,
    VerificationRetryIntervalMs = 200
});
var writer = new SfrNumberWriter(station);
using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));

try
{
    var result = await writer.AssignAsync(123, cancellation.Token);
    if (result.Success)
    {
        Console.WriteLine($"Номер подтверждён: {result.Card.Number}");
        if (!result.SignalSent)
            Console.WriteLine($"Сигнал не отправлен: {result.SignalError}");
    }
    else
    {
        Console.WriteLine(result.CardMayHaveChanged
            ? $"Результат не подтверждён: {result.Error}"
            : $"Чип не изменён: {result.Error}");
        if (result.RequiresReconnect)
            Console.WriteLine("Перед следующей операцией откройте соединение заново.");
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("Ожидание отменено до изменения чипа.");
}
```

Это минимальный пример. Ошибки открытия, USB и неизвестные форматы также нужно
обрабатывать на границе приложения — см. [контракт результатов и исключений](docs/integration.md).

## Что важно при внедрении

- Проверяйте `Success` до обращения к `Card`. `false` не всегда означает,
  что запись не произошла: смотрите `CardMayHaveChanged`.
- Один `SfrNumberWriter` на соединение. Не запускайте параллельно опрос,
  присвоение, очистку и сигнал.
- При кратковременной недоступности чипа подготовка/проверка ждёт его возврата
  до настроенного срока. Повторяется чтение, а не отправленная запись.
- `RequiresReconnect` определяет необходимость переподключения.
  Обычное снятие чипа не равно потере USB-соединения.
- До окончания команды держите на станции один и тот же чип. Маркер памяти
  не является уникальным серийным номером.
- При закрытии программы отмените ожидание, дождитесь текущей задачи и только
  затем вызовите `Dispose()` станции.

## Структура репозитория

| Каталог | Назначение |
|---|---|
| `SfrConnect/` | Библиотека и публичный API |
| `SfrConnect.TestApp/` | WPF-пример управления станцией и журналами |
| `SfrConnect.Diagnostics/` | Консоль для проверки соединения и протокола |
| `SfrConnect.Tests/` | Тесты обмена, восстановления и состояния интерфейса |
| `docs/` | Руководство по внедрению, диагностика, описание протокола |
