using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NfcTagger.Core;

namespace NfcTagger.App;

public partial class MainWindow : Window
{
    private const int MaxLogLines = 2000;
    private INfcReader? _reader;
    private CardInfo? _card;
    private CardDump? _lastDump;
    private string? _memoryUid;
    private CancellationTokenSource? _dumpCts;
    private TaskCompletionSource<bool>? _dialog;
    private IInputElement? _focusBeforeDialog;
    private string _deviceSignature = "";
    // Armed at start-up and whenever a reader is plugged in or pulled out; any connect uses it up.
    private bool _autoConnect = true;
    // Look again a few times after a plug: a new port can be held (ModemManager probing it) or not yet opened up to
    // the user (udev still applying access) when it first shows up.
    private int _discoveryRetries;
    private IReadOnlyList<string> _deniedPorts = [];
    private bool _checkingDevices;
    private bool _busy;
    private int _scanFailures;
    private int _logLines;
    private string _page = "card";
    // Reader drivers are not thread-safe: user actions and background polls take turns through this.
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly Dictionary<string, Control> _pages;
    private readonly ObservableCollection<ApduEntry> _apdu = [];
    private readonly ObservableCollection<MemoryRow> _memory = [];
    // A detect costs 17–91 ms on ATNFC, so polling this often keeps up with the reader's own beep on card placement.
    private readonly DispatcherTimer _scanTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    // Plug and unplug show up as a change in the set of serial ports and PC/SC readers, checked the same way on both OSes.
    private readonly DispatcherTimer _deviceTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly string _settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
    private AppSettings _settings = new();

    public MainWindow()
    {
        InitializeComponent();
        Title = $"NFC Tagger {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}";
        _pages = new() { ["card"] = CardPage, ["ndef"] = NdefPage, ["memory"] = MemoryPage, ["apdu"] = ApduPage, ["log"] = LogPage };
        ApduHistory.ItemsSource = _apdu;
        MemoryGrid.ItemsSource = _memory;
        _apdu.CollectionChanged += (_, _) => ApduEmpty.IsVisible = _apdu.Count == 0;
        LinuxSetupPanel.IsVisible = OperatingSystem.IsLinux();
        NavList.SelectedIndex = 0;
        AddHandler(KeyDownEvent, Window_PreviewKeyDown, RoutingStrategies.Tunnel);
        _scanTimer.Tick += async (_, _) => await PollAsync();
        _deviceTimer.Tick += async (_, _) => await DevicesChangedAsync();
        // A bug in a handler is shown and logged instead of closing the app with the reader's settings left changed.
        Dispatcher.UIThread.UnhandledException += (_, e) => { e.Handled = true; ShowError(e.Exception); };
        Loaded += async (_, _) => {
            LoadSettings();
            UpdateNdefPreview();
            await RefreshDevicesAsync();
            _deviceTimer.Start();
        };
        Closed += (_, _) => {
            _scanTimer.Stop();
            _deviceTimer.Stop();
            _io.Wait(TimeSpan.FromSeconds(2)); // let an in-flight command finish before the reader restores its settings
            _reader?.Dispose();
            SaveSettings();
        };
    }

    private void LoadSettings()
    {
        try { if (File.Exists(_settingsPath)) _settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath)) ?? new(); }
        catch { _settings = new(); }
    }

    private void SaveSettings()
    {
        try { File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true })); }
        catch { /* Read-only portable folder: app remains usable. */ }
    }

    private void Log(string message)
    {
        var text = (LogBox.Text ?? "") + $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        if (++_logLines > MaxLogLines) { text = text[(text.IndexOf('\n') + 1)..]; _logLines--; } // field PCs run for days
        LogBox.Text = text;
        LogBox.CaretIndex = text.Length;
    }

    private void Notify(string message, bool error = false)
    {
        InfoBar.Classes.Set("error", error);
        InfoText.Text = message;
        InfoBar.IsVisible = true;
    }

    private void CloseInfo_Click(object? sender, RoutedEventArgs e) => InfoBar.IsVisible = false;

    private void ShowError(Exception e)
    {
        FooterStatus.Text = "오류: " + e.Message;
        Log("오류: " + e.Message);
        Notify(e.Message, error: true);
    }

    private async Task<T> RunAsync<T>(string label, Func<T> action)
    {
        if (_busy) throw new InvalidOperationException("다른 작업이 진행 중입니다.");
        _busy = true;
        InfoBar.IsVisible = false;
        FooterStatus.Text = label + " 중…";
        BusyBar.IsIndeterminate = true;
        ProgressText.Text = "";
        BusyPanel.IsVisible = true;
        UpdateState();
        await _io.WaitAsync();
        var started = Stopwatch.StartNew();
        try {
            var result = await Task.Run(action);
            FooterStatus.Text = $"{label} 완료 ({started.Elapsed.TotalSeconds:0.00}초)";
            Log(FooterStatus.Text);
            return result;
        } finally {
            _io.Release();
            _busy = false;
            BusyBar.IsIndeterminate = false;
            BusyPanel.IsVisible = false;
            UpdateState();
        }
    }

    private INfcReader RequireReader() => _reader ?? throw new IOException("먼저 리더를 연결하세요.");
    private CardInfo RequireCard() => _card ?? throw new IOException("먼저 카드를 감지하세요.");

    // Asks every serial port which reader it is and lists what answered, plus ACR PC/SC readers; connects by itself
    // when auto-connect is armed.
    private async Task RefreshDevicesAsync()
    {
        if (_busy || _reader is not null) return;
        _busy = true;
        FooterStatus.Text = "리더를 찾는 중…";
        UpdateState();
        var listed = false;
        try {
            var busyPorts = new List<string>();
            var deniedPorts = new List<string>();
            var started = Stopwatch.StartNew();
            var (found, signature) = await Task.Run(() => (ReaderDiscovery.List(busyPorts, deniedPorts), DeviceSignature()));
            _deviceSignature = signature;
            _deniedPorts = deniedPorts;
            if (busyPorts.Count + deniedPorts.Count == 0) _discoveryRetries = 0;
            var previous = (ReaderCombo.SelectedItem as ReaderChoice)?.DeviceId ?? _settings.DeviceId;
            ReaderCombo.ItemsSource = found;
            ReaderCombo.SelectedItem = found.FirstOrDefault(x => x.DeviceId == previous) ?? found.FirstOrDefault();
            var note = (busyPorts.Count == 0 ? "" : $" · 다른 프로그램이 사용 중: {string.Join(", ", busyPorts)}") +
                (deniedPorts.Count == 0 ? "" : $" · 권한 없음: {string.Join(", ", deniedPorts)}");
            Log($"리더 검색 ({started.Elapsed.TotalSeconds:0.0}초): {(found.Count == 0 ? "없음" : string.Join(", ", found.Select(x => x.DisplayName)))}{note}");
            FooterStatus.Text = (found.Count > 0 ? $"리더 {found.Count}대를 찾았습니다."
                : OperatingSystem.IsLinux() ? "인식된 리더가 없습니다. USB 연결과 진단 로그 화면의 리눅스 장치 설정을 확인하세요."
                : "인식된 리더가 없습니다. USB 연결과 드라이버를 확인하세요.") + note;
            if (deniedPorts.Count > 0 && found.Count == 0)
                Notify($"권한이 없어 열지 못한 포트가 있습니다({string.Join(", ", deniedPorts)}). 진단 로그 화면의 리눅스 장치 설정에서 자동 설정을 누르세요.", error: true);
            listed = true;
        } catch (Exception e) { ShowError(e); }
        finally { _busy = false; UpdateState(); }
        await UpdateLinuxStatusAsync();
        if (listed && _autoConnect && ReaderCombo.SelectedItem is ReaderChoice choice) {
            Log($"자동 연결: {choice.DisplayName}");
            await ConnectAsync(choice);
        }
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e) => await RefreshDevicesAsync();

    private void Reader_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateState();

    private async Task DevicesChangedAsync()
    {
        if (_busy || _checkingDevices) return;
        _checkingDevices = true;
        try {
            if (await Task.Run(DeviceSignature) != _deviceSignature) {
                // A reader was plugged in or pulled out: arm auto-connect. While connected it waits for the next
                // disconnect, so pulling the connected reader and plugging another one moves over to the new one.
                _autoConnect = true;
                _discoveryRetries = 5;
            } else if (_reader is not null || _discoveryRetries == 0) return;
            else _discoveryRetries--;
            if (_reader is null) await RefreshDevicesAsync();
        } finally { _checkingDevices = false; }
    }

    private static string DeviceSignature() =>
        string.Join('|', ReaderDiscovery.SerialPorts().Concat(ReaderDiscovery.PcscReaders().Select(x => x.DeviceId)));

    private async void Connect_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_reader is not null) {
            _autoConnect = false; // the user asked to disconnect: do not reconnect behind their back
            await DisconnectAsync("리더 연결 해제");
            FooterStatus.Text = "리더 연결을 해제했습니다.";
            return;
        }
        if (ReaderCombo.SelectedItem is ReaderChoice choice) await ConnectAsync(choice);
    }

    private async Task ConnectAsync(ReaderChoice choice)
    {
        _autoConnect = false; // used up: a reader that fails while plugged in is not reconnected in a loop
        try {
            _reader = await RunAsync("리더 연결", () => {
                var reader = ReaderDiscovery.Create(choice);
                try { reader.Open(); return reader; }
                catch { reader.Dispose(); throw; }
            });
            _settings = new(choice.Kind, choice.DeviceId);
            SaveSettings();
            Log($"연결됨: {choice.DisplayName}");
            _scanTimer.Start();
            UpdateState();
            await ScanAsync();
        } catch (Exception ex) { ShowError(ex); }
    }

    private async Task DisconnectAsync(string reason)
    {
        if (_reader is not { } reader) return;
        _scanTimer.Stop();
        _reader = null; // an in-flight poll sees this and drops its result
        _card = null;
        _scanFailures = 0;
        KeyBox.Text = "";
        _busy = true;   // keep 연결 disabled until the port is really closed
        UpdateState();
        await _io.WaitAsync();
        try { await Task.Run(reader.Dispose); } // may send a last command to restore reader settings
        finally { _io.Release(); _busy = false; }
        Log(reason);
        UpdateState();
        await RefreshDevicesAsync(); // device changes are only watched for auto-connect while connected, so the list may be stale
    }

    private async Task ScanAsync()
    {
        var reader = _reader;
        if (_busy || reader is null) return;
        try {
            var card = await RunAsync("카드 감지", () => reader.Detect());
            _scanFailures = 0;
            ApplyCard(card);
            if (card is null) FooterStatus.Text = "카드를 리더 위에 올려주세요.";
        } catch (Exception ex) { ShowError(ex); }
    }

    private async void Scan_Click(object? sender, RoutedEventArgs e) => await ScanAsync();

    // Background card detection: never touches the busy UI, and skips a tick while a user action owns the reader.
    private async Task PollAsync()
    {
        var reader = _reader;
        if (_busy || reader is null || !_io.Wait(0)) return;
        CardInfo? card = null;
        Exception? error = null;
        try { card = await Task.Run(reader.Detect); }
        catch (Exception ex) { error = ex; }
        finally { _io.Release(); }
        if (reader != _reader) return;
        if (error is null) {
            if (_scanFailures > 0) FooterStatus.Text = "준비됨";
            _scanFailures = 0;
            ApplyCard(card);
        } else if (++_scanFailures < 8) { // ~2.4 s of failed polls before the reader counts as gone
            FooterStatus.Text = "카드 감지 오류. 다시 시도합니다.";
            Log($"카드 감지 오류 ({_scanFailures}회째): {error.Message}");
        } else {
            // Told first: if another reader was plugged in meanwhile, the disconnect auto-connects and replaces this.
            FooterStatus.Text = "리더 연결이 끊겼습니다.";
            Notify("리더 연결이 끊겼습니다. 케이블을 확인한 뒤 다시 연결하세요.", error: true);
            await DisconnectAsync("리더 연결 끊김: " + error.Message);
        }
    }

    private void ApplyCard(CardInfo? card)
    {
        if (card == _card) return;
        if (card?.Uid != _card?.Uid) {
            KeyBox.Text = "";
            Log(card is null ? "카드 제거됨" : $"카드 감지: {card.DisplayFamily} · {card.Uid}");
            FooterStatus.Text = card is null ? "카드가 제거되었습니다." : $"{card.DisplayFamily} 카드를 감지했습니다.";
        }
        _card = card;
        UpdateState();
    }

    private void UpdateState()
    {
        var connected = _reader is not null;
        var card = _card;
        var memory = card?.Family is CardFamily.Ntag or CardFamily.MifareClassic or CardFamily.Iso15693 or CardFamily.FelicaLiteS;
        var ndef = card?.Family is CardFamily.Ntag or CardFamily.Iso15693 or CardFamily.FelicaLiteS;
        // ACS PC/SC readers also take reader commands (FF …) with any card on them.
        var apdu = card?.Family == CardFamily.Iso14443_4 || connected && _reader!.Kind is ReaderKind.Acr1552U or ReaderKind.Acr122U && card is not null;
        var idle = !_busy;

        DisconnectedPanel.IsVisible = !connected;
        ConnectedPanel.IsVisible = connected;
        ReaderNameText.Text = _reader?.Name;
        ReaderCombo.IsEnabled = RefreshButton.IsEnabled = DisconnectButton.IsEnabled = idle;
        ConnectButton.IsEnabled = idle && ReaderCombo.SelectedItem is not null;

        BarCardTitle.Text = card?.DisplayFamily ?? (connected ? "카드 없음" : "리더 미연결");
        BarCardUid.Text = card?.Uid;
        BarCardUid.IsVisible = card is not null;
        BarCardHint.Text = connected ? "리더 위에 카드를 올려주세요" : "먼저 리더를 연결하세요";
        BarCardHint.IsVisible = card is null;
        CopyUidButton.IsVisible = card is not null;
        CardBadge.Classes.Set("ok", card is not null);

        CardEmpty.IsVisible = card is null;
        CardPresent.IsVisible = card is not null;
        CardEmptyIcon.Data = this.FindResource(connected ? "IconPayment" : "IconUsbPlug") as Geometry;
        CardEmptyTitle.Text = connected ? "카드를 리더 위에 올려주세요" : "리더를 연결하세요";
        // Explicit line breaks: Hangul may otherwise wrap mid-word.
        CardEmptyBody.Text = connected
            ? "카드를 올리면 자동으로 감지해\n종류와 UID를 보여줍니다."
            : "리더를 USB에 꽂으면 자동으로 찾아 연결합니다.\n직접 고르려면 위쪽 목록에서 선택한 뒤 연결을 누르세요.";
        ScanButton.IsVisible = connected;
        ScanButton.IsEnabled = idle;
        if (card is not null) {
            CardFamilyText.Text = card.DisplayFamily;
            CardUidText.Text = card.Uid;
            CardRawText.Text = card.RawType;
            CardDetailsText.Text = card.Details;
            CardReaderText.Text = _reader?.Name;
            var uid = UidText.Formats(card.Uid);
            UidHexText.Text = uid?.Hex ?? card.Uid;
            UidHexReversedText.Text = uid?.HexReversed ?? "—";
            UidDecText.Text = uid?.Dec ?? "—";
            UidDecReversedText.Text = uid?.DecReversed ?? "—";
        }
        SetTile(NdefTile, NdefTileText, ndef, "텍스트·URL을 읽고 씁니다");
        SetTile(MemoryTile, MemoryTileText, memory, "블록 읽기·쓰기, 전체 덤프");
        SetTile(ApduTile, ApduTileText, apdu, "원시 APDU 명령을 보냅니다");

        NdefReadButton.IsEnabled = NdefWriteButton.IsEnabled = ndef && idle;
        MemoryReadButton.IsEnabled = MemoryWriteButton.IsEnabled = DumpButton.IsEnabled = memory && idle;
        ExportDumpButton.IsEnabled = _lastDump is not null && idle;
        ApduSendButton.IsEnabled = apdu && idle;
        KeyRow.IsVisible = card?.Family == CardFamily.MifareClassic;

        var notice = _page is "card" or "log" ? null
            : !connected ? "리더가 연결되지 않았습니다. 위쪽에서 리더를 고른 뒤 연결하세요."
            : card is null ? "카드가 감지되지 않았습니다. 리더 위에 카드를 올려주세요."
            : _page == "ndef" && !ndef ? $"{card.DisplayFamily} 카드는 NDEF 읽기·쓰기를 지원하지 않습니다."
            : _page == "memory" && !memory ? $"{card.DisplayFamily} 카드는 직접 메모리 읽기·쓰기를 지원하지 않습니다."
            : _page == "apdu" && !apdu ? "APDU는 ISO14443-4 카드나 ACR 리더(ACR1552U·ACR122U)에서만 쓸 수 있습니다."
            : null;
        PageNoticeText.Text = notice;
        PageNotice.IsVisible = notice is not null;
    }

    private static void SetTile(Button tile, TextBlock text, bool available, string description)
    {
        tile.IsEnabled = available;
        text.Text = available ? description : "이 카드는 지원하지 않습니다";
    }

    private void Nav_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Also raised while the XAML loads, before the pages are known.
        if (_pages is null || NavList.SelectedItem is not ListBoxItem { Tag: string page }) return;
        _page = page;
        InfoBar.IsVisible = false; // notices belong to the page that raised them
        foreach (var (key, element) in _pages) element.IsVisible = key == page;
        UpdateState();
    }

    private void Tile_Click(object? sender, RoutedEventArgs e) =>
        NavList.SelectedItem = NavList.Items.OfType<ListBoxItem>().First(x => Equals(x.Tag, ((Button)sender!).Tag));

    private async void Window_PreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_dialog is not null) {
            if (e.Key == Key.Escape) { e.Handled = true; CloseDialog(false); }
            return;
        }
        if (e.Key != Key.F5) return;
        e.Handled = true;
        if (_reader is null) await RefreshDevicesAsync();
        else await ScanAsync();
    }

    private Task<bool> ConfirmAsync(string title, string body, string confirm, params Inline[] detail)
    {
        DialogTitle.Text = title;
        DialogBody.Text = body;
        DialogConfirm.Content = confirm;
        var inlines = new InlineCollection();
        inlines.AddRange(detail);
        DialogDetail.Inlines = inlines;
        _focusBeforeDialog = FocusManager?.GetFocusedElement();
        DialogLayer.IsVisible = true;
        Dispatcher.UIThread.Post(() => DialogCancel.Focus(), DispatcherPriority.Input); // safe default for destructive actions
        _dialog = new();
        return _dialog.Task;
    }

    private void CloseDialog(bool confirmed)
    {
        DialogLayer.IsVisible = false;
        _dialog?.TrySetResult(confirmed);
        _dialog = null;
        _focusBeforeDialog?.Focus();
    }

    private void DialogConfirm_Click(object? sender, RoutedEventArgs e) => CloseDialog(true);
    private void DialogCancel_Click(object? sender, RoutedEventArgs e) => CloseDialog(false);

    private async void Copy(string? text, string what)
    {
        if (string.IsNullOrEmpty(text)) return;
        try {
            await (Clipboard ?? throw new InvalidOperationException()).SetTextAsync(text);
            FooterStatus.Text = what + " 복사했습니다.";
        } catch (Exception) { Notify("클립보드를 사용할 수 없습니다. 잠시 후 다시 시도하세요.", error: true); }
    }

    private void CopyUid_Click(object? sender, RoutedEventArgs e) { if (_card is not null) Copy(_card.Uid, "UID를"); }
    private void CopyUidFormat_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name, CommandParameter: string value }) Copy(value, $"UID {name} 값을");
    }
    private void CopyNdef_Click(object? sender, RoutedEventArgs e) => Copy(NdefResult.Text, "NDEF 내용을");
    private void CopyLog_Click(object? sender, RoutedEventArgs e) => Copy(LogBox.Text, "로그를");

    private void NdefKind_Changed(object? sender, RoutedEventArgs e) { if (IsLoaded) UpdateNdefPreview(); }
    private void NdefInput_TextChanged(object? sender, TextChangedEventArgs e) => UpdateNdefPreview();

    private void UpdateNdefPreview()
    {
        var uri = NdefUrlMode.IsChecked == true;
        var value = NdefInput.Text ?? "";
        NdefHint.Text = uri ? "예: https://example.com" : "UTF-8 텍스트로 저장합니다.";
        try { NdefSize.Text = string.IsNullOrWhiteSpace(value) ? "" : $"{(uri ? NdefCodec.Uri(value.Trim()) : NdefCodec.Text(value)).Length}바이트"; }
        catch (ArgumentOutOfRangeException) { NdefSize.Text = "내용이 너무 깁니다"; }
    }

    private void ShowNdef(NdefDocument doc, CardInfo card, string action)
    {
        NdefResult.Text = doc.Summary;
        NdefMeta.Text = $"{doc.Length}바이트 · {card.Uid} · {DateTime.Now:HH:mm:ss} {action}";
        NdefRawHex.Text = doc.Length == 0 ? "(비어 있음)" : Spaced(Convert.FromHexString(doc.RawHex));
        NdefResultEmpty.IsVisible = false;
        NdefResultPanel.IsVisible = true;
    }

    private async void NdefRead_Click(object? sender, RoutedEventArgs e)
    {
        try {
            var reader = RequireReader(); var card = RequireCard();
            var result = await RunAsync("NDEF 읽기", () => NdefService.Read(reader, card));
            ShowNdef(result, card, "읽음");
            Log($"NDEF 읽기: {result.Length}바이트");
        } catch (Exception ex) { ShowError(ex); }
    }

    private async void NdefWrite_Click(object? sender, RoutedEventArgs e)
    {
        try {
            var reader = RequireReader(); var card = RequireCard();
            var value = NdefInput.Text ?? "";
            var uri = NdefUrlMode.IsChecked == true;
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("쓸 내용을 입력하세요.");
            if (!await ConfirmAsync("NDEF 쓰기",
                    $"{card.DisplayFamily} · {card.Uid}\n이 카드의 NDEF를 아래 {(uri ? "URL" : "텍스트")}로 덮어씁니다.\n기존 내용은 쓰기 전에 백업합니다.",
                    "쓰기", new Run(uri ? value.Trim() : value))) return;
            var result = await RunAsync("NDEF 백업·쓰기·검증", () => {
                _lastDump = NdefService.Backup(reader, card); // kept even if the write below fails
                return NdefService.Write(reader, card, uri, value);
            });
            ShowNdef(result, card, "쓰고 검증함");
            Log($"NDEF 쓰기 검증 완료: {result.Length}바이트");
            Notify($"쓰기와 재읽기 검증을 마쳤습니다 ({result.Length}바이트).\n쓰기 전 백업은 메모리 화면의 JSON 내보내기로 저장할 수 있습니다.");
        } catch (Exception ex) { ShowError(ex); }
    }

    private (string? Key, bool KeyB) KeyParameters()
    {
        var key = string.IsNullOrWhiteSpace(KeyBox.Text) ? null : KeyBox.Text.Trim();
        if (_card?.Family == CardFamily.MifareClassic && key is null)
            throw new ArgumentException("MIFARE Classic Key A 또는 Key B를 입력하세요.");
        return (key, KeyB.IsChecked == true);
    }

    private static int ParseAddress(TextBox box, string name) =>
        int.TryParse(box.Text, out var value) && value is >= 0 and <= 255 ? value : throw new ArgumentException($"{name}는 0~255 사이 숫자여야 합니다.");

    private void ShowMemory(IEnumerable<MemoryRow> rows, CardInfo card, string what)
    {
        _memory.Clear();
        foreach (var row in rows) _memory.Add(row);
        // TableView has no auto width: fit HEX and ASCII to the longest block so 16-byte MIFARE rows are not clipped.
        MemoryGrid.Columns[1].Width = new GridLength(Math.Max(60, _memory.Max(x => x.Hex.Length) * 8.6 + 24));
        MemoryGrid.Columns[2].Width = new GridLength(Math.Max(60, _memory.Max(x => x.Ascii.Length) * 8.6 + 24));
        MemorySource.Text = $"{what} · {card.Uid} · {DateTime.Now:HH:mm:ss}";
        MemoryEmpty.IsVisible = false;
        _memoryUid = card.Uid;
    }

    private async void MemoryRead_Click(object? sender, RoutedEventArgs e)
    {
        try {
            var reader = RequireReader(); var card = RequireCard();
            var address = ParseAddress(MemoryAddress, "시작 주소");
            if (!int.TryParse(MemoryCount.Text, out var count) || count is < 1 or > 64 || address + count > 256)
                throw new ArgumentException("개수는 1~64이며 주소 255를 넘을 수 없습니다.");
            var (key, keyB) = KeyParameters();
            var units = await RunAsync("메모리 읽기", () => CardWorkflows.ReadRange(reader, card, address, count, key, keyB));
            ShowMemory(units.Select(x => MemoryRow.From(x)), card, $"주소 {address}부터 {count}개");
            Log($"메모리 읽기: {address}부터 {count}개");
        } catch (Exception ex) { ShowError(ex); }
    }

    private async void Dump_Click(object? sender, RoutedEventArgs e)
    {
        try {
            var reader = RequireReader(); var card = RequireCard(); var (key, keyB) = KeyParameters();
            _dumpCts = new CancellationTokenSource();
            var token = _dumpCts.Token;
            var progress = new Progress<(int Done, int Total)>(p => {
                if (!_busy) return;
                BusyBar.IsIndeterminate = false;
                BusyBar.Maximum = p.Total;
                BusyBar.Value = p.Done;
                ProgressText.Text = $"{p.Done}/{p.Total}";
            });
            DumpButton.IsVisible = false;
            CancelDumpButton.IsVisible = true;
            _lastDump = await RunAsync("전체 메모리 읽기", () => CardWorkflows.Dump(reader, card, key, keyB, token, progress));
            ShowMemory(_lastDump.Units.Select(x => MemoryRow.From(x)), card, $"전체 {_lastDump.Units.Count}개 주소");
            UpdateState();
            Log($"전체 읽기: {_lastDump.Units.Count}개 주소");
        } catch (OperationCanceledException) { FooterStatus.Text = "전체 읽기를 취소했습니다."; Log("전체 읽기 취소"); }
        catch (Exception ex) { ShowError(ex); }
        finally {
            DumpButton.IsVisible = true;
            CancelDumpButton.IsVisible = false;
            _dumpCts?.Dispose();
            _dumpCts = null;
        }
    }

    private void CancelDump_Click(object? sender, RoutedEventArgs e) => _dumpCts?.Cancel();

    private async void ExportDump_Click(object? sender, RoutedEventArgs e)
    {
        if (_lastDump is not { } dump) return;
        try {
            if (!StorageProvider.CanSave) throw new NotSupportedException("이 환경에서는 저장 대화상자를 열 수 없습니다.");
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
                SuggestedFileName = $"nfc-{dump.Card.Uid}-{dump.CapturedAt:yyyyMMdd-HHmmss}.json",
                DefaultExtension = "json",
                FileTypeChoices = [new FilePickerFileType("JSON 파일") { Patterns = ["*.json"] }]
            });
            if (file is null) return;
            await using (var stream = await file.OpenWriteAsync()) {
                stream.SetLength(0); // overwriting a longer file must not leave its tail behind
                await stream.WriteAsync(new UTF8Encoding(false).GetBytes(CardWorkflows.ToJson(dump)));
            }
            Log("덤프 내보내기 완료");
            Notify("JSON으로 저장했습니다: " + (file.TryGetLocalPath() ?? file.Name));
        } catch (Exception ex) { ShowError(ex); }
    }

    private void MemoryGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (MemoryGrid.SelectedItem is not MemoryRow row) return;
        WriteAddress.Text = row.Address.ToString();
        MemoryWriteHex.Text = row.Hex;
    }

    private async void MemoryWrite_Click(object? sender, RoutedEventArgs e)
    {
        try {
            var reader = RequireReader(); var card = RequireCard();
            var address = ParseAddress(WriteAddress, "쓰기 주소");
            var value = Hex.Parse(MemoryWriteHex.Text ?? "");
            var (key, keyB) = KeyParameters();
            if (card.Family == CardFamily.FelicaLiteS && address == 0)
                throw new InvalidOperationException("FeliCa NDEF 속성 블록은 직접 쓰지 마세요. NDEF 화면을 사용하세요.");
            WriteGuard.Validate(card, address, value.Length);
            var before = await RunAsync("기존 데이터 확인", () => reader.ReadUnit(card, address, key, keyB));
            if (!await ConfirmAsync("메모리 쓰기", $"{card.DisplayFamily} · {card.Uid}\n주소 {address}에 쓰고, 다시 읽어 검증합니다.",
                    "쓰기", DiffInlines(before, value))) return;
            // Replace the exportable backup only once the write is confirmed, so cancelling keeps an earlier full dump.
            _lastDump = new(reader.Name, card, DateTimeOffset.Now, before.Length, [new MemoryUnit(address, Hex.Format(before), null)]);
            var result = await RunAsync("메모리 쓰기·검증", () => CardWorkflows.WriteVerified(reader, card, address, value, key, keyB));
            var row = MemoryRow.From(new(result.Address, result.AfterHex, null), verified: true);
            var shown = _memoryUid == card.Uid ? _memory.FirstOrDefault(x => x.Address == address) : null;
            if (shown is not null) _memory[_memory.IndexOf(shown)] = row;
            else ShowMemory([row], card, $"주소 {address} 쓰기");
            Log($"메모리 쓰기 검증 완료: 주소 {address}");
            Notify($"주소 {address}에 쓰고 재읽기 검증을 마쳤습니다.");
        } catch (Exception ex) { ShowError(ex); }
    }

    // Old and new value on separate lines so the hex columns line up; changed bytes are highlighted.
    private Inline[] DiffInlines(byte[] before, byte[] after)
    {
        var mono = (FontFamily)this.FindResource("MonoFont")!;
        var accent = this.FindResource("SystemControlHighlightAccentBrush") as IBrush;
        var inlines = new List<Inline> { new Run("기존 값"), new LineBreak(), new Run(Spaced(before)) { FontFamily = mono }, new LineBreak(), new LineBreak(), new Run("새 값"), new LineBreak() };
        for (var i = 0; i < after.Length; i++) {
            var run = new Run((i > 0 ? " " : "") + after[i].ToString("X2")) { FontFamily = mono };
            if (i >= before.Length || before[i] != after[i]) {
                run.FontWeight = FontWeight.Bold;
                run.Foreground = accent;
            }
            inlines.Add(run);
        }
        return [.. inlines];
    }

    private async void ApduSend_Click(object? sender, RoutedEventArgs e)
    {
        INfcReader reader;
        byte[] command;
        try { reader = RequireReader(); RequireCard(); command = Hex.Parse(ApduInput.Text ?? ""); }
        catch (Exception ex) { ShowError(ex); return; }
        var time = DateTime.Now.ToString("HH:mm:ss");
        try {
            var response = await RunAsync("APDU 전송", () => reader.TransmitApdu(command));
            var hasSw = response.Length >= 2;
            var sw = hasSw ? Hex.Format(response[^2..]) : "—";
            var body = hasSw ? response[..^2] : response;
            _apdu.Insert(0, new(time, Spaced(command), body.Length == 0 ? "(데이터 없음)" : Spaced(body), sw, sw == "9000" || sw.StartsWith("61")));
            Log($"APDU 전송: {command.Length}바이트, 응답 {response.Length}바이트");
        } catch (Exception ex) {
            _apdu.Insert(0, new(time, Spaced(command), ex.Message, "오류", false));
            FooterStatus.Text = "APDU 전송 실패";
            Log("APDU 오류: " + ex.Message);
        }
    }

    private void ApduInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !ApduSendButton.IsEnabled) return;
        e.Handled = true;
        ApduSend_Click(sender, e);
    }

    private void ApduHistory_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ApduHistory.SelectedItem is ApduEntry entry) ApduInput.Text = entry.Command;
    }

    private void ClearApdu_Click(object? sender, RoutedEventArgs e) => _apdu.Clear();

    private void ClearLog_Click(object? sender, RoutedEventArgs e) { LogBox.Text = ""; _logLines = 0; }

    private async Task UpdateLinuxStatusAsync()
    {
        if (!OperatingSystem.IsLinux()) return;
        var denied = _deniedPorts;
        LinuxStatus.Text = string.Join('\n', await Task.Run(() => LinuxSetup.Check(denied)));
    }

    private async void LinuxCheck_Click(object? sender, RoutedEventArgs e)
    {
        if (_reader is null) await RefreshDevicesAsync(); // also re-checks the setup
        else await UpdateLinuxStatusAsync();
    }

    private async void LinuxSetup_Click(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("리눅스 장치 설정", "관리자 권한으로 아래 설정을 합니다. 이어서 뜨는 창에 관리자 암호를 입력하세요.",
                "설정", new Run(LinuxSetup.Summary))) return;
        LinuxSetupButton.IsEnabled = false;
        FooterStatus.Text = "리눅스 장치 설정 중…";
        try {
            var (ok, output) = await LinuxSetup.RunAsync();
            Log($"리눅스 장치 설정 {(ok ? "완료" : "실패")}{(output.Length == 0 ? "" : ": " + output)}");
            if (ok) {
                FooterStatus.Text = "리눅스 장치 설정을 마쳤습니다.";
                // A PC/SC library that failed to load stays failed for this process.
                Notify("설정을 마쳤습니다. ACR122U는 한 번 뽑았다 다시 꽂으세요." +
                    (ReaderDiscovery.PcscProblem() is null ? "" : "\nACR 리더가 계속 안 보이면 앱을 다시 시작하세요."));
            } else {
                FooterStatus.Text = "리눅스 장치 설정을 하지 못했습니다.";
                Notify("자동 설정을 하지 못했습니다(암호 입력 취소, 관리자 인증 창 없음 등). '터미널 명령 복사'를 눌러 터미널에서 실행하세요." +
                    (output.Length == 0 ? "" : "\n" + output), error: true);
            }
        } finally { LinuxSetupButton.IsEnabled = true; }
        if (_reader is null) await RefreshDevicesAsync();
        else await UpdateLinuxStatusAsync();
    }

    private void LinuxCopyCommand_Click(object? sender, RoutedEventArgs e)
    {
        Copy(LinuxSetup.TerminalCommand, "터미널 명령을");
        Notify("터미널에 붙여 넣고 Enter를 누른 뒤 관리자 암호를 입력하세요. 끝나면 '다시 점검'을 누르세요.");
    }

    private void LinuxAddToMenu_Click(object? sender, RoutedEventArgs e)
    {
        try {
            var entry = LinuxSetup.AddToMenu();
            Log("앱 메뉴 등록: " + entry);
            Notify("앱 메뉴에 추가했습니다. 실행 파일을 다른 폴더로 옮기면 다시 누르세요.");
        } catch (Exception ex) { ShowError(ex); }
    }

    internal static string Spaced(byte[] bytes) => BitConverter.ToString(bytes).Replace('-', ' ');
}

public sealed record AppSettings(ReaderKind? Kind = null, string? DeviceId = null);

public sealed record MemoryRow(int Address, string Hex, string Ascii, string? Status, bool? Ok)
{
    public string AddressText => $"{Address,3} (0x{Address:X2})";
    public bool Verified => Ok == true;
    public bool Failed => Ok == false;

    public static MemoryRow From(MemoryUnit unit, bool verified = false)
    {
        if (unit.Hex is null) return new(unit.Address, "", "", unit.Error, false);
        var bytes = Convert.FromHexString(unit.Hex);
        var ascii = new string(bytes.Select(b => b is >= 0x20 and < 0x7F ? (char)b : '.').ToArray());
        return new(unit.Address, MainWindow.Spaced(bytes), ascii, verified ? "검증 완료" : null, verified ? true : null);
    }
}

public sealed record ApduEntry(string Time, string Command, string Data, string Sw, bool Ok);
