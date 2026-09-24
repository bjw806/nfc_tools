using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NfcTagger.Core;

namespace NfcTagger.App;

public partial class MainWindow : Window
{
    private INfcReader? _reader;
    private CardInfo? _card;
    private CardDump? _lastDump;
    private CancellationTokenSource? _dumpCts;
    private bool _busy;
    private int _scanFailures;
    private readonly DispatcherTimer _scanTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly string _settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
    private AppSettings _settings = new();

    public MainWindow()
    {
        InitializeComponent();
        _scanTimer.Tick += async (_, _) => { if (!_busy && _reader is not null) await ScanAsync(true); };
        Loaded += async (_, _) => {
            LoadSettings();
            await RefreshDevicesAsync();
            SelectPage("overview");
            UpdateState();
        };
        Closed += (_, _) => { _scanTimer.Stop(); _reader?.Dispose(); SaveSettings(); };
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
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    private void ShowError(Exception e)
    {
        FooterStatus.Text = "오류: " + e.Message;
        Log("오류: " + e.Message);
        MessageBox.Show(this, e.Message, "NFC Tagger", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async Task<T> BusyAsync<T>(string label, Func<T> action)
    {
        if (_busy) throw new InvalidOperationException("다른 작업이 진행 중입니다.");
        _busy = true;
        _scanTimer.Stop();
        FooterStatus.Text = label + " 중…";
        UpdateState();
        try {
            var result = await Task.Run(action);
            FooterStatus.Text = label + " 완료";
            return result;
        } finally {
            _busy = false;
            UpdateState();
            if (_reader is not null) _scanTimer.Start();
        }
    }

    private INfcReader RequireReader() => _reader ?? throw new IOException("먼저 리더를 연결하세요.");
    private CardInfo RequireCard() => _card ?? throw new IOException("먼저 카드를 감지하세요.");

    private async Task RefreshDevicesAsync()
    {
        if (_busy) return;
        try {
            var list = await Task.Run(ReaderDiscovery.List);
            ReaderCombo.ItemsSource = list;
            ReaderCombo.SelectedItem = list.FirstOrDefault(x => x.Kind == _settings.Kind && x.DeviceId == _settings.DeviceId);
            Log($"장치 목록 갱신: {list.Count}개 선택 항목");
            FooterStatus.Text = list.Count == 0 ? "인식된 리더가 없습니다. 연결과 드라이버를 확인하세요." : "리더를 선택해 연결하세요.";
        } catch (Exception e) { ShowError(e); }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshDevicesAsync();

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_reader is not null) {
            _scanTimer.Stop(); _reader.Dispose(); _reader = null; _card = null; _lastDump = null;
            Log("리더 연결 해제"); UpdateState(); return;
        }
        if (ReaderCombo.SelectedItem is not ReaderChoice choice) { ShowError(new IOException("리더를 선택하세요.")); return; }
        try {
            _reader = await BusyAsync("리더 연결", () => {
                var reader = ReaderDiscovery.Create(choice);
                try { reader.Open(); return reader; }
                catch { reader.Dispose(); throw; }
            });
            _settings = new(choice.Kind, choice.DeviceId);
            SaveSettings();
            Log($"연결됨: {choice.DisplayName}");
            UpdateState();
            await ScanAsync(false);
        } catch (Exception ex) { ShowError(ex); }
    }

    private async Task ScanAsync(bool silent)
    {
        if (_busy || _reader is null) return;
        try {
            var previous = _card?.Uid;
            var previousStatus = FooterStatus.Text;
            var card = await BusyAsync("카드 감지", () => _reader.Detect());
            _scanFailures = 0;
            _card = card;
            if (card is not null && card.Uid != previous) { KeyBox.Clear(); Log($"카드 감지: {card.DisplayFamily} · {card.Uid}"); }
            if (card is null && previous is not null) { KeyBox.Clear(); Log("카드 제거됨"); }
            UpdateState();
            if (silent && card?.Uid == previous) FooterStatus.Text = previousStatus;
            if (!silent && card is null) FooterStatus.Text = "카드를 리더 위에 올려주세요.";
        } catch (Exception ex) {
            _card = null; UpdateState();
            if (silent) {
                _scanFailures++;
                if (_scanFailures >= 3) {
                    _scanTimer.Stop(); _reader?.Dispose(); _reader = null;
                    Log("리더 연결 끊김: " + ex.Message);
                    FooterStatus.Text = "리더 연결이 끊겼습니다. 다시 연결하세요.";
                    UpdateState();
                } else FooterStatus.Text = "카드 감지 오류. 다시 시도합니다.";
            }
            else ShowError(ex);
        }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync(false);

    private void UpdateState()
    {
        var connected = _reader is not null;
        var card = _card;
        var memory = card?.Family is CardFamily.Ntag or CardFamily.MifareClassic or CardFamily.Iso15693 or CardFamily.FelicaLiteS;
        var ndef = card?.Family is CardFamily.Ntag or CardFamily.Iso15693 or CardFamily.FelicaLiteS;
        var apdu = card?.Family == CardFamily.Iso14443_4 || connected && _reader!.Kind == ReaderKind.Acr1552U && card is not null;
        ReaderCombo.IsEnabled = !connected && !_busy;
        ConnectButton.IsEnabled = !_busy;
        ConnectButton.Content = connected ? "연결 해제" : "연결";
        ConnectionPill.Text = connected ? "●  " + _reader!.Name : "○  리더 미연결";
        ConnectionBadge.Background = new SolidColorBrush(connected ? Color.FromRgb(234, 247, 241) : Color.FromRgb(238, 242, 247));
        ConnectionPill.Foreground = new SolidColorBrush(connected ? Color.FromRgb(33, 117, 82) : Color.FromRgb(100, 116, 139));
        CardTitle.Text = card?.DisplayFamily ?? "카드를 올려주세요";
        UidText.Text = card is null ? "UID  —" : "UID  " + card.Uid;
        CardDetails.Text = card?.Details ?? "리더를 연결하고 카드를 감지하면 정보가 표시됩니다.";
        CapabilitiesText.Text = card is null ? "카드 감지 후 표시됩니다." :
            $"메모리 {(memory ? "사용 가능" : "지원 안 함")}   ·   NDEF {(ndef ? "사용 가능" : "지원 안 함")}   ·   APDU {(apdu ? "사용 가능" : "지원 안 함")}";
        NdefReadButton.IsEnabled = ndef && !_busy;
        NdefWriteButton.IsEnabled = ndef && !_busy;
        MemoryReadButton.IsEnabled = memory && !_busy;
        MemoryWriteButton.IsEnabled = memory && !_busy;
        DumpButton.IsEnabled = memory && !_busy;
        ExportDumpButton.IsEnabled = _lastDump is not null && !_busy;
        ApduSendButton.IsEnabled = apdu && !_busy;
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string page) SelectPage(page);
    }

    private void SelectPage(string page)
    {
        var pages = new Dictionary<string, (StackPanel Panel, Button Nav, string Title, string Subtitle)> {
            ["overview"] = (OverviewPage, OverviewNav, "개요", "리더와 카드를 연결해 작업을 시작하세요."),
            ["ndef"] = (NdefPage, NdefNav, "NDEF", "태그의 텍스트와 URL을 읽고 씁니다."),
            ["memory"] = (MemoryPage, MemoryNav, "메모리", "블록을 살펴보고 허용된 데이터 영역을 수정합니다."),
            ["apdu"] = (ApduPage, ApduNav, "APDU / 전문가", "카드와 원시 APDU를 주고받습니다."),
            ["log"] = (LogPage, LogNav, "진단 로그", "연결과 작업 상태를 확인합니다.")
        };
        foreach (var entry in pages.Values) {
            entry.Panel.Visibility = Visibility.Collapsed;
            entry.Nav.Background = Brushes.Transparent;
            entry.Nav.Foreground = new SolidColorBrush(Color.FromRgb(88, 103, 126));
        }
        if (!pages.TryGetValue(page, out var selected)) return;
        selected.Panel.Visibility = Visibility.Visible;
        selected.Nav.Background = new SolidColorBrush(Color.FromRgb(234, 240, 255));
        selected.Nav.Foreground = new SolidColorBrush(Color.FromRgb(36, 82, 203));
        PageTitle.Text = selected.Title; PageSubtitle.Text = selected.Subtitle;
    }

    private async void NdefRead_Click(object sender, RoutedEventArgs e)
    {
        try {
            var reader = RequireReader(); var card = RequireCard();
            var result = await BusyAsync("NDEF 읽기", () => NdefService.Read(reader, card));
            NdefResult.Text = $"{result.Summary}\n\n{result.Length}바이트\n{result.RawHex}";
            Log($"NDEF 읽기: {result.Length}바이트");
        } catch (Exception ex) { ShowError(ex); }
    }

    private async void NdefWrite_Click(object sender, RoutedEventArgs e)
    {
        try {
            var reader = RequireReader(); var card = RequireCard();
            var value = NdefInput.Text;
            var uri = NdefKind.SelectedIndex == 1;
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("쓸 내용을 입력하세요.");
            if (MessageBox.Show(this, $"카드 {card.Uid}에 아래 {(uri ? "URL" : "텍스트")} 내용을 쓰시겠습니까?\n\n{value}",
                    "NDEF 쓰기 확인", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _lastDump = await BusyAsync("쓰기 전 NDEF 백업", () => NdefService.Backup(reader, card));
            UpdateState();
            var result = await BusyAsync("NDEF 쓰기·검증", () => NdefService.Write(reader, card, uri, value));
            NdefResult.Text = $"{result.Summary}\n\n검증 완료 · {result.Length}바이트";
            Log($"NDEF 쓰기 검증 완료: {result.Length}바이트");
        } catch (Exception ex) { ShowError(ex); }
    }

    private (int Address, int Count, string? Key, bool KeyB) MemoryParameters()
    {
        if (!int.TryParse(MemoryAddress.Text, out var address) || address is < 0 or > 255)
            throw new ArgumentException("시작 주소는 0~255여야 합니다.");
        if (!int.TryParse(MemoryCount.Text, out var count) || count is < 1 or > 64 || address + count > 256)
            throw new ArgumentException("읽기 개수는 1~64이며 주소 범위를 넘을 수 없습니다.");
        var key = string.IsNullOrWhiteSpace(KeyBox.Password) ? null : KeyBox.Password.Trim();
        if (_card?.Family == CardFamily.MifareClassic && key is null)
            throw new ArgumentException("MIFARE Classic Key A 또는 Key B를 입력하세요.");
        return (address, count, key, KeyB.IsChecked == true);
    }

    private async void MemoryRead_Click(object sender, RoutedEventArgs e)
    {
        try {
            var reader = RequireReader(); var card = RequireCard(); var p = MemoryParameters();
            var units = await BusyAsync("메모리 읽기", () => {
                var output = new List<MemoryUnit>();
                for (var i = 0; i < p.Count; i++) {
                    try { output.Add(new(p.Address + i, Hex.Format(reader.ReadUnit(card, p.Address + i, p.Key, p.KeyB)), null)); }
                    catch (Exception ex) { output.Add(new(p.Address + i, null, ex.Message)); }
                }
                return output;
            });
            MemoryGrid.ItemsSource = units;
            Log($"메모리 읽기: {p.Address}부터 {p.Count}개");
        } catch (Exception ex) { ShowError(ex); }
    }

    private async void Dump_Click(object sender, RoutedEventArgs e)
    {
        try {
            var reader = RequireReader(); var card = RequireCard(); var p = MemoryParameters();
            _dumpCts = new CancellationTokenSource();
            var token = _dumpCts.Token;
            CancelDumpButton.Visibility = Visibility.Visible;
            _lastDump = await BusyAsync("전체 메모리 읽기", () => CardWorkflows.Dump(reader, card, p.Key, p.KeyB, token));
            MemoryGrid.ItemsSource = _lastDump.Units;
            UpdateState();
            Log($"전체 읽기: {_lastDump.Units.Count}개 주소");
        } catch (OperationCanceledException) { FooterStatus.Text = "전체 읽기가 취소되었습니다."; Log("전체 읽기 취소"); }
        catch (Exception ex) { ShowError(ex); }
        finally { CancelDumpButton.Visibility = Visibility.Collapsed; _dumpCts?.Dispose(); _dumpCts = null; }
    }

    private void CancelDump_Click(object sender, RoutedEventArgs e) => _dumpCts?.Cancel();

    private void ExportDump_Click(object sender, RoutedEventArgs e)
    {
        if (_lastDump is null) return;
        var dialog = new SaveFileDialog { Filter = "JSON 파일 (*.json)|*.json", FileName = $"nfc-{_lastDump.Card.Uid}-{DateTime.Now:yyyyMMdd-HHmmss}.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, CardWorkflows.ToJson(_lastDump), new UTF8Encoding(false)); Log("덤프 내보내기 완료"); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void MemoryWrite_Click(object sender, RoutedEventArgs e)
    {
        try {
            var reader = RequireReader(); var card = RequireCard(); var p = MemoryParameters();
            var value = Hex.Parse(MemoryWriteHex.Text);
            if (card.Family == CardFamily.FelicaLiteS && p.Address == 0)
                throw new InvalidOperationException("FeliCa NDEF 속성 블록은 직접 쓰지 마세요. NDEF 화면을 사용하세요.");
            WriteGuard.Validate(card, p.Address, value.Length);
            var before = await BusyAsync("기존 데이터 확인", () => reader.ReadUnit(card, p.Address, p.Key, p.KeyB));
            _lastDump = new(reader.Name, card, DateTimeOffset.Now, before.Length,
                new[] { new MemoryUnit(p.Address, Hex.Format(before), null) });
            UpdateState();
            if (MessageBox.Show(this, $"카드 {card.Uid} · 주소 {p.Address}\n기존: {Hex.Format(before)}\n새 값: {Hex.Format(value)}\n\n쓰고 재읽기 검증을 진행할까요?",
                    "메모리 쓰기 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var result = await BusyAsync("메모리 쓰기·검증", () => CardWorkflows.WriteVerified(reader, card, p.Address, value, p.Key, p.KeyB));
            MemoryGrid.ItemsSource = new[] { new MemoryUnit(result.Address, result.AfterHex, "검증 완료") };
            Log($"메모리 쓰기 검증 완료: 주소 {p.Address}");
        } catch (Exception ex) { ShowError(ex); }
    }

    private async void ApduSend_Click(object sender, RoutedEventArgs e)
    {
        try {
            var reader = RequireReader(); RequireCard();
            var command = Hex.Parse(ApduInput.Text);
            var result = await BusyAsync("APDU 전송", () => reader.TransmitApdu(command));
            ApduResult.Text = Hex.Format(result);
            Log($"APDU 전송: {command.Length}바이트, 응답 {result.Length}바이트");
        } catch (Exception ex) { ShowError(ex); }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogBox.Clear();
}

public sealed record AppSettings(ReaderKind? Kind = null, string? DeviceId = null);
