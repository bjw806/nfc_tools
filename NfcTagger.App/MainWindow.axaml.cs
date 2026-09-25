using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
using S = NfcTagger.App.AppStrings;

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
    // Set at startup and on device changes, cleared by any connect.
    private bool _autoConnect = true;
    // Re-scan a few times after a device change. A new port can be busy (ModemManager probing it)
    // or not accessible yet (udev still applying the rule).
    private int _discoveryRetries;
    private IReadOnlyList<string> _deniedPorts = [];
    private bool _checkingDevices;
    private bool _closing, _closed;
    private bool _busy;
    private int _scanFailures;
    private int _logLines;
    private string _page = "card";
    // Readers aren't thread-safe, so UI actions and background polls take turns.
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly Dictionary<string, Control> _pages;
    private readonly ObservableCollection<ApduEntry> _apdu = [];
    private readonly ObservableCollection<MemoryRow> _memory = [];
    // A detect takes 17-91 ms on ATNFC; polling this often keeps up with the reader's own beep.
    private readonly DispatcherTimer _scanTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    // Detects plug/unplug by comparing the port and reader list, the same way on both OSes.
    private readonly DispatcherTimer _deviceTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly string _settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
    private AppSettings _settings = new();

    public MainWindow()
    {
        LoadSettings();
        Strings.Korean = _settings.Language is { } language ? language == "ko" : SystemIsKorean();
        InitializeComponent();
        LanguageCombo.SelectedIndex = Strings.Korean ? 1 : 0;
        var ndefLanguage = _settings.NdefLanguage ?? (Strings.Korean ? "ko" : "en");
        NdefLanguage.SelectedItem = NdefLanguage.Items.OfType<ComboBoxItem>().FirstOrDefault(x => Equals(x.Tag, ndefLanguage)) ?? NdefLanguage.Items[0];
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
        // Show and log handler errors instead of crashing, which would leave the reader settings changed.
        Dispatcher.UIThread.UnhandledException += (_, e) => { e.Handled = true; ShowError(e.Exception); };
        Loaded += async (_, _) => {
            UpdateNdefPreview();
            await RefreshDevicesAsync();
            _deviceTimer.Start();
        };
        Closing += async (_, e) => {
            if (_closed) return;
            e.Cancel = true; // restore the reader settings (ATNFC URC and beep) first, then close for real
            if (_closing) return;
            _closing = true;
            _scanTimer.Stop();
            _deviceTimer.Stop();
            _dumpCts?.Cancel();
            try {
                await _io.WaitAsync(TimeSpan.FromSeconds(5)); // let a running command finish
                if (_reader is { } reader) { _reader = null; await Task.Run(reader.Dispose); }
                SaveSettings();
            } finally {
                _closed = true;
                Close();
            }
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

    // The UI follows the OS language until the user picks one. CultureInfo can't tell (InvariantGlobalization).
    private static bool SystemIsKorean()
    {
        if (OperatingSystem.IsWindows()) return (GetUserDefaultUILanguage() & 0x3FF) == 0x12; // LANG_KOREAN
        var locale = new[] { "LANGUAGE", "LC_ALL", "LC_MESSAGES", "LANG" }
            .Select(Environment.GetEnvironmentVariable).FirstOrDefault(x => !string.IsNullOrEmpty(x));
        return locale?.StartsWith("ko", StringComparison.OrdinalIgnoreCase) == true;
    }

    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();

    private void Language_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var korean = LanguageCombo.SelectedIndex == 1;
        if (korean == Strings.Korean) return;
        Strings.Korean = korean; // XAML text follows through TrExtension
        _settings = _settings with { Language = korean ? "ko" : "en" };
        SaveSettings();
        InfoBar.IsVisible = false;
        if (!_busy) FooterStatus.Text = S.Ready;
        UpdateState();
        UpdateNdefPreview();
        _ = UpdateLinuxStatusAsync();
    }

    private void Log(string message)
    {
        var text = (LogBox.Text ?? "") + $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        if (++_logLines > MaxLogLines) { text = text[(text.IndexOf('\n') + 1)..]; _logLines--; } // the app may stay open for days
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
        FooterStatus.Text = S.Error(e.Message);
        Log(S.Error(e.Message));
        Notify(e.Message, error: true);
    }

    private async Task<T> RunAsync<T>(string label, Func<T> action)
    {
        if (_busy) throw new InvalidOperationException(S.Busy);
        _busy = true;
        InfoBar.IsVisible = false;
        FooterStatus.Text = S.Working(label);
        BusyBar.IsIndeterminate = true;
        ProgressText.Text = "";
        BusyPanel.IsVisible = true;
        UpdateState();
        await _io.WaitAsync();
        var started = Stopwatch.StartNew();
        try {
            var result = await Task.Run(action);
            FooterStatus.Text = S.Finished(label, started.Elapsed.TotalSeconds);
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

    private INfcReader RequireReader() => _reader ?? throw new IOException(S.ConnectReaderFirst);
    private CardInfo RequireCard() => _card ?? throw new IOException(S.DetectCardFirst);

    // Lists the connected readers and connects by itself when auto-connect is armed.
    private async Task RefreshDevicesAsync()
    {
        if (_busy || _reader is not null) return;
        _busy = true;
        FooterStatus.Text = S.SearchingReaders;
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
            var note = (busyPorts.Count == 0 ? "" : S.PortsInUse(string.Join(", ", busyPorts))) +
                (deniedPorts.Count == 0 ? "" : S.PortsDenied(string.Join(", ", deniedPorts)));
            Log(S.ReaderSearchLog(started.Elapsed.TotalSeconds, found.Count == 0 ? Strings.None : string.Join(", ", found.Select(x => x.DisplayName))) + note);
            FooterStatus.Text = (found.Count > 0 ? S.FoundReaders(found.Count)
                : OperatingSystem.IsLinux() ? S.NoReadersLinux : S.NoReadersWindows) + note;
            if (deniedPorts.Count > 0 && found.Count == 0)
                Notify(S.PortsDeniedNotice(string.Join(", ", deniedPorts)), error: true);
            listed = true;
        } catch (Exception e) { ShowError(e); }
        finally { _busy = false; UpdateState(); }
        await UpdateLinuxStatusAsync();
        if (listed && _autoConnect && ReaderCombo.SelectedItem is ReaderChoice choice) {
            Log(S.AutoConnecting(choice.DisplayName));
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
                // A reader was plugged in or pulled out. While connected, auto-connect waits for the next
                // disconnect, so swapping readers moves over to the new one.
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
            _autoConnect = false; // manual disconnect, don't reconnect
            await DisconnectAsync(S.ReaderDisconnectedLog);
            FooterStatus.Text = S.ReaderDisconnected;
            return;
        }
        if (ReaderCombo.SelectedItem is ReaderChoice choice) await ConnectAsync(choice);
    }

    private async Task ConnectAsync(ReaderChoice choice)
    {
        _autoConnect = false; // one try, avoids reconnect loops
        try {
            _reader = await RunAsync(S.ConnectingReader, () => {
                var reader = ReaderDiscovery.Create(choice);
                try { reader.Open(); return reader; }
                catch { reader.Dispose(); throw; }
            });
            _settings = _settings with { Kind = choice.Kind, DeviceId = choice.DeviceId };
            SaveSettings();
            Log(S.ConnectedLog(choice.DisplayName));
            _scanTimer.Start();
            UpdateState();
            await ScanAsync();
        } catch (Exception ex) { ShowError(ex); }
    }

    private async Task DisconnectAsync(string reason)
    {
        if (_reader is not { } reader) return;
        _scanTimer.Stop();
        _reader = null; // in-flight polls drop their result
        _card = null;
        _scanFailures = 0;
        KeyBox.Text = "";
        _busy = true;   // keep Connect disabled until the port is closed
        UpdateState();
        await _io.WaitAsync();
        try { await Task.Run(reader.Dispose); } // may send commands to restore reader settings
        finally { _io.Release(); _busy = false; }
        Log(reason);
        UpdateState();
        await RefreshDevicesAsync(); // devices aren't re-listed while connected, so the list may be stale
    }

    private async Task ScanAsync()
    {
        var reader = _reader;
        if (_busy || reader is null) return;
        try {
            var card = await RunAsync(S.DetectingCard, () => reader.Detect());
            _scanFailures = 0;
            ApplyCard(card);
            if (card is null) FooterStatus.Text = S.PlaceCard;
        } catch (Exception ex) { ShowError(ex); }
    }

    private async void Scan_Click(object? sender, RoutedEventArgs e) => await ScanAsync();

    // Background card detection. Skips while a user action holds the reader.
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
            if (_scanFailures > 0) FooterStatus.Text = S.Ready;
            _scanFailures = 0;
            ApplyCard(card);
        } else if (++_scanFailures < 8) { // about 2.4 s of failures before giving up
            FooterStatus.Text = S.DetectRetry;
            Log(S.DetectErrorLog(_scanFailures, error.Message));
        } else {
            // Set first, so an auto-connect to another reader during the disconnect can replace it.
            FooterStatus.Text = S.ReaderLost;
            Notify(S.ReaderLostNotice, error: true);
            await DisconnectAsync(S.ReaderLostLog(error.Message));
        }
    }

    private void ApplyCard(CardInfo? card)
    {
        if (card == _card) return;
        if (card?.Uid != _card?.Uid) {
            KeyBox.Text = "";
            Log(card is null ? S.CardRemovedLog : S.CardDetectedLog(card.DisplayFamily, card.Uid));
            FooterStatus.Text = card is null ? S.CardRemoved : S.CardDetected(card.DisplayFamily);
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
        // ACR readers accept pseudo-APDUs (FF ..) with any card
        var apdu = card?.Family == CardFamily.Iso14443_4 || connected && _reader!.Kind is ReaderKind.Acr1552U or ReaderKind.Acr122U && card is not null;
        var idle = !_busy;

        DisconnectedPanel.IsVisible = !connected;
        ConnectedPanel.IsVisible = connected;
        ReaderNameText.Text = _reader?.Name;
        ReaderCombo.IsEnabled = RefreshButton.IsEnabled = DisconnectButton.IsEnabled = idle;
        ConnectButton.IsEnabled = idle && ReaderCombo.SelectedItem is not null;

        BarCardTitle.Text = card?.DisplayFamily ?? (connected ? S.NoCard : S.NoReader);
        BarCardUid.Text = card?.Uid;
        BarCardUid.IsVisible = card is not null;
        BarCardHint.Text = connected ? S.PlaceCardHint : S.ConnectReaderHint;
        BarCardHint.IsVisible = card is null;
        CopyUidButton.IsVisible = card is not null;
        CardBadge.Classes.Set("ok", card is not null);

        CardEmpty.IsVisible = card is null;
        CardPresent.IsVisible = card is not null;
        CardEmptyIcon.Data = this.FindResource(connected ? "IconPayment" : "IconUsbPlug") as Geometry;
        CardEmptyTitle.Text = connected ? S.PlaceCardTitle : S.ConnectReaderTitle;
        CardEmptyBody.Text = connected ? S.CardEmptyConnected : S.CardEmptyDisconnected; // explicit line breaks, since Hangul can wrap mid-word
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
        SetTile(NdefTile, NdefTileText, ndef, S.NdefTile);
        SetTile(MemoryTile, MemoryTileText, memory, S.MemoryTile);
        SetTile(ApduTile, ApduTileText, apdu, S.ApduTile);

        NdefReadButton.IsEnabled = NdefWriteButton.IsEnabled = ndef && idle;
        MemoryReadButton.IsEnabled = MemoryWriteButton.IsEnabled = DumpButton.IsEnabled = memory && idle;
        ExportDumpButton.IsEnabled = _lastDump is not null && idle;
        ApduSendButton.IsEnabled = apdu && idle;
        KeyRow.IsVisible = card?.Family == CardFamily.MifareClassic;

        var notice = _page is "card" or "log" ? null
            : !connected ? S.NoticeNoReader
            : card is null ? S.NoticeNoCard
            : _page == "ndef" && !ndef ? S.NoticeNoNdef(card.DisplayFamily)
            : _page == "memory" && !memory ? S.NoticeNoMemory(card.DisplayFamily)
            : _page == "apdu" && !apdu ? S.NoticeNoApdu
            : null;
        PageNoticeText.Text = notice;
        PageNotice.IsVisible = notice is not null;
    }

    private static void SetTile(Button tile, TextBlock text, bool available, string description)
    {
        tile.IsEnabled = available;
        text.Text = available ? description : S.NotSupported;
    }

    private void Nav_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // also raised during InitializeComponent
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
        Dispatcher.UIThread.Post(() => DialogCancel.Focus(), DispatcherPriority.Input); // default to Cancel
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

    private async void Copy(string? text, string copied)
    {
        if (string.IsNullOrEmpty(text)) return;
        try {
            await (Clipboard ?? throw new InvalidOperationException()).SetTextAsync(text);
            FooterStatus.Text = copied;
        } catch (Exception) { Notify(S.ClipboardUnavailable, error: true); }
    }

    private void CopyUid_Click(object? sender, RoutedEventArgs e) { if (_card is not null) Copy(_card.Uid, S.CopiedUid); }
    private void CopyUidFormat_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name, CommandParameter: string value }) Copy(value, S.CopiedUidFormat(name));
    }
    private void CopyNdef_Click(object? sender, RoutedEventArgs e) => Copy(NdefResult.Text, S.CopiedNdef);
    private void CopyLog_Click(object? sender, RoutedEventArgs e) => Copy(LogBox.Text, S.CopiedLog);

    private void NdefKind_Changed(object? sender, RoutedEventArgs e) { if (IsLoaded) UpdateNdefPreview(); }

    private string NdefLanguageCode => (NdefLanguage.SelectedItem as ComboBoxItem)?.Tag as string ?? "en";

    private void NdefLanguage_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _settings = _settings with { NdefLanguage = NdefLanguageCode };
        SaveSettings();
        UpdateNdefPreview();
    }
    private void NdefInput_TextChanged(object? sender, TextChangedEventArgs e) => UpdateNdefPreview();

    private void UpdateNdefPreview()
    {
        var uri = NdefUrlMode.IsChecked == true;
        var value = NdefInput.Text ?? "";
        NdefHint.Text = uri ? S.UrlExample : S.SavedAsUtf8;
        NdefLanguage.IsEnabled = !uri;
        try { NdefSize.Text = string.IsNullOrWhiteSpace(value) ? "" : S.Bytes((uri ? NdefCodec.Uri(value.Trim()) : NdefCodec.Text(value, NdefLanguageCode)).Length); }
        catch (ArgumentOutOfRangeException) { NdefSize.Text = S.TooLong; }
    }

    private void ShowNdef(NdefDocument doc, CardInfo card, string action)
    {
        NdefResult.Text = doc.Summary;
        NdefMeta.Text = $"{S.Bytes(doc.Length)} · {card.Uid} · {DateTime.Now:HH:mm:ss} {action}";
        NdefRawHex.Text = doc.Length == 0 ? S.Empty : Spaced(Convert.FromHexString(doc.RawHex));
        NdefResultEmpty.IsVisible = false;
        NdefResultPanel.IsVisible = true;
    }

    private async void NdefRead_Click(object? sender, RoutedEventArgs e)
    {
        try {
            var reader = RequireReader(); var card = RequireCard();
            var result = await RunAsync(S.ReadingNdef, () => NdefService.Read(reader, card));
            ShowNdef(result, card, S.ActionRead);
            Log(S.NdefReadLog(result.Length));
        } catch (Exception ex) { ShowError(ex); }
    }

    private async void NdefWrite_Click(object? sender, RoutedEventArgs e)
    {
        try {
            var reader = RequireReader(); var card = RequireCard();
            var value = NdefInput.Text ?? "";
            var uri = NdefUrlMode.IsChecked == true;
            var language = NdefLanguageCode;
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(Strings.EnterContent);
            if (!await ConfirmAsync(S.WriteNdefTitle, S.WriteNdefBody($"{card.DisplayFamily} · {card.Uid}", uri),
                    S.Write, new Run(uri ? value.Trim() : value))) return;
            var result = await RunAsync(S.WritingNdef, () => {
                _lastDump = NdefService.Backup(reader, card); // kept even if the write fails
                return NdefService.Write(reader, card, uri, value, language);
            });
            ShowNdef(result, card, S.ActionWritten);
            Log(S.NdefWrittenLog(result.Length));
            Notify(S.NdefWritten(result.Length));
        } catch (Exception ex) { ShowError(ex); }
    }

    private (string? Key, bool KeyB) KeyParameters()
    {
        var key = string.IsNullOrWhiteSpace(KeyBox.Text) ? null : KeyBox.Text.Trim();
        if (_card?.Family == CardFamily.MifareClassic && key is null)
            throw new ArgumentException(S.EnterMifareKeyAB);
        return (key, KeyB.IsChecked == true);
    }

    private static int ParseAddress(TextBox box, string name) =>
        int.TryParse(box.Text, out var value) && value is >= 0 and <= 255 ? value : throw new ArgumentException(S.AddressRange(name));

    private void ShowMemory(IEnumerable<MemoryRow> rows, CardInfo card, string what)
    {
        _memory.Clear();
        foreach (var row in rows) _memory.Add(row);
        // TableView has no auto width. Fit HEX/ASCII to the longest block so 16-byte rows aren't clipped.
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
            var address = ParseAddress(MemoryAddress, S.StartAddress);
            if (!int.TryParse(MemoryCount.Text, out var count) || count is < 1 or > 64 || address + count > 256)
                throw new ArgumentException(S.CountRange);
            var (key, keyB) = KeyParameters();
            var units = await RunAsync(S.ReadingMemory, () => CardWorkflows.ReadRange(reader, card, address, count, key, keyB));
            ShowMemory(units.Select(x => MemoryRow.From(x)), card, S.RangeSource(address, count));
            Log(S.MemoryReadLog(address, count));
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
            _lastDump = await RunAsync(S.ReadingAllMemory, () => CardWorkflows.Dump(reader, card, key, keyB, token, progress));
            ShowMemory(_lastDump.Units.Select(x => MemoryRow.From(x)), card, S.AllSource(_lastDump.Units.Count));
            UpdateState();
            Log(S.DumpLog(_lastDump.Units.Count));
        } catch (OperationCanceledException) { FooterStatus.Text = S.DumpCancelled; Log(S.DumpCancelledLog); }
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
            if (!StorageProvider.CanSave) throw new NotSupportedException(S.NoSaveDialog);
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
                SuggestedFileName = $"nfc-{dump.Card.Uid}-{dump.CapturedAt:yyyyMMdd-HHmmss}.json",
                DefaultExtension = "json",
                FileTypeChoices = [new FilePickerFileType(S.JsonFile) { Patterns = ["*.json"] }]
            });
            if (file is null) return;
            await using (var stream = await file.OpenWriteAsync()) {
                stream.SetLength(0); // in case the old file was longer
                await stream.WriteAsync(new UTF8Encoding(false).GetBytes(CardWorkflows.ToJson(dump)));
            }
            Log(S.DumpExportedLog);
            Notify(S.SavedJson(file.TryGetLocalPath() ?? file.Name));
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
            var address = ParseAddress(WriteAddress, S.WriteAddress);
            var value = Hex.Parse(MemoryWriteHex.Text ?? "");
            var (key, keyB) = KeyParameters();
            if (card.Family == CardFamily.FelicaLiteS && address == 0)
                throw new InvalidOperationException(S.FelicaAttributeBlock);
            WriteGuard.Validate(card, address, value.Length);
            var before = await RunAsync(S.ReadingCurrentData, () => reader.ReadUnit(card, address, key, keyB));
            if (!await ConfirmAsync(S.WriteMemoryTitle, S.WriteMemoryBody($"{card.DisplayFamily} · {card.Uid}", address),
                    S.Write, DiffInlines(before, value))) return;
            // Replace the backup only after confirmation, so cancelling keeps an earlier full dump.
            _lastDump = new(reader.Name, card, DateTimeOffset.Now, before.Length, [new MemoryUnit(address, Hex.Format(before), null)]);
            var result = await RunAsync(S.WritingMemory, () => CardWorkflows.WriteVerified(reader, card, address, value, key, keyB));
            var row = MemoryRow.From(new(result.Address, result.AfterHex, null), verified: true);
            var shown = _memoryUid == card.Uid ? _memory.FirstOrDefault(x => x.Address == address) : null;
            if (shown is not null) _memory[_memory.IndexOf(shown)] = row;
            else ShowMemory([row], card, S.WriteSource(address));
            Log(S.MemoryWrittenLog(address));
            Notify(S.MemoryWritten(address));
        } catch (Exception ex) { ShowError(ex); }
    }

    // Old and new value on separate lines so the bytes line up, with changed bytes highlighted.
    private Inline[] DiffInlines(byte[] before, byte[] after)
    {
        var mono = (FontFamily)this.FindResource("MonoFont")!;
        var accent = this.FindResource("SystemControlHighlightAccentBrush") as IBrush;
        var inlines = new List<Inline> { new Run(S.CurrentValue), new LineBreak(), new Run(Spaced(before)) { FontFamily = mono }, new LineBreak(), new LineBreak(), new Run(S.NewValue), new LineBreak() };
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
            var response = await RunAsync(S.SendingApdu, () => reader.TransmitApdu(command));
            var hasSw = response.Length >= 2;
            var sw = hasSw ? Hex.Format(response[^2..]) : "—";
            var body = hasSw ? response[..^2] : response;
            _apdu.Insert(0, new(time, Spaced(command), body.Length == 0 ? S.NoData : Spaced(body), sw, sw == "9000" || sw.StartsWith("61")));
            Log(S.ApduLog(command.Length, response.Length));
        } catch (Exception ex) {
            _apdu.Insert(0, new(time, Spaced(command), ex.Message, S.ErrorShort, false));
            FooterStatus.Text = S.ApduFailed;
            Log(S.ApduErrorLog(ex.Message));
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
        if (!await ConfirmAsync(S.LinuxSetupTitle, S.LinuxSetupConfirm, S.SetUp, new Run(S.LinuxSetupSummary))) return;
        LinuxSetupButton.IsEnabled = false;
        FooterStatus.Text = S.LinuxSetupRunning;
        try {
            var (ok, output) = await LinuxSetup.RunAsync();
            Log(S.LinuxSetupLog(ok, output.Length == 0 ? "" : ": " + output));
            if (ok) {
                FooterStatus.Text = S.LinuxSetupDone;
                // a failed libpcsclite load sticks until restart
                Notify(S.LinuxSetupDoneNotice + (ReaderDiscovery.PcscProblem() is null ? "" : S.RestartForAcr));
            } else {
                FooterStatus.Text = S.LinuxSetupFailed;
                Notify(S.LinuxSetupFailedNotice + (output.Length == 0 ? "" : "\n" + output), error: true);
            }
        } finally { LinuxSetupButton.IsEnabled = true; }
        if (_reader is null) await RefreshDevicesAsync();
        else await UpdateLinuxStatusAsync();
    }

    private void LinuxCopyCommand_Click(object? sender, RoutedEventArgs e)
    {
        Copy(LinuxSetup.TerminalCommand, S.CopiedTerminalCommand);
        Notify(S.TerminalCommandNotice);
    }

    private void LinuxAddToMenu_Click(object? sender, RoutedEventArgs e)
    {
        try {
            var entry = LinuxSetup.AddToMenu();
            Log(S.AddedToMenuLog(entry));
            Notify(S.AddedToMenu);
        } catch (Exception ex) { ShowError(ex); }
    }

    internal static string Spaced(byte[] bytes) => BitConverter.ToString(bytes).Replace('-', ' ');
}

public sealed record AppSettings(ReaderKind? Kind = null, string? DeviceId = null, string? Language = null, string? NdefLanguage = null);

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
        return new(unit.Address, MainWindow.Spaced(bytes), ascii, verified ? S.Verified : null, verified ? true : null);
    }
}

public sealed record ApduEntry(string Time, string Command, string Data, string Sw, bool Ok);
