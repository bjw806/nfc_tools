using System.Reflection;
using NfcTagger.App;
using NfcTagger.Core;

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception($"실패: {name}");
    Console.WriteLine($"통과: {name}");
}

// Every text has an English and a Korean version, passed to T in that order.
static bool HasHangul(string s) => s.Any(c => c is >= '가' and <= '힣');
var texts = new[] { typeof(Strings), typeof(AppStrings) }
    .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
    .Where(m => m.ReturnType == typeof(string) && m.Name != nameof(Strings.T)).ToList();
var wrong = texts.Where(m => {
    var arguments = m.GetParameters().Select(p => p.ParameterType == typeof(string) ? "x"
        : p.ParameterType == typeof(bool) ? true : p.ParameterType == typeof(double) ? 1.0 : (object)1).ToArray();
    Strings.Korean = false;
    var en = (string)m.Invoke(null, arguments)!;
    Strings.Korean = true;
    var ko = (string)m.Invoke(null, arguments)!;
    return en.Length == 0 || HasHangul(en) || !HasHangul(ko);
}).Select(m => m.Name).ToList();
Check(texts.Count > 200 && wrong.Count == 0, $"영어·한국어 문구 {texts.Count}개 {string.Join(", ", wrong)}");

var text = NdefCodec.Text("안녕하세요 NFC");
Check(NdefCodec.Describe(text).Summary == Strings.NdefText("안녕하세요 NFC"), "NDEF UTF-8 텍스트 왕복");
var uri = NdefCodec.Uri("https://example.com/tag");
Check(NdefCodec.Describe(uri).Summary == "URL: https://example.com/tag", "NDEF URL 왕복");
var tlv = NdefCodec.FindTlv(Hex.Parse("0103A00C340300FE"));
Check(tlv.Offset == 5 && tlv.Length == 0, "Lock Control TLV 뒤의 빈 NDEF");
var frame = Pn532Frames.Encode(Hex.Parse("D402"));
Check(Pn532Frames.Decode(frame).SequenceEqual(Hex.Parse("D402")), "PN532 프레임 왕복");
frame[^2]++;
try { Pn532Frames.Decode(frame); throw new Exception("잘못된 체크섬을 허용했습니다."); }
catch (IOException) { Console.WriteLine("통과: PN532 체크섬 오류 검출"); }
Check(AcrReader.ClassifyAtr(Hex.Parse("3B8F8001804F0CA000000306030001000000006A")) == CardFamily.MifareClassic,
    "ACR MIFARE ATR 분류");
Check(AcrReader.ClassifyAtr(Hex.Parse("3B8F8001804F0CA0000003060B00350000000056")) == CardFamily.Iso15693,
    "ACR ISO15693 ATR 분류");
var felicaIdm = "0112233445566778";
var felicaBlock = Hex.Parse("000102030405060708090A0B0C0D0E0F");
Check(FelicaFrames.ReadData(Hex.Parse($"1D07{felicaIdm}000001{Hex.Format(felicaBlock)}"), felicaIdm).SequenceEqual(felicaBlock),
    "FeliCa 읽기 응답 상태와 블록 파싱");
FelicaFrames.CheckWrite(Hex.Parse($"0C09{felicaIdm}0000"), felicaIdm);
Console.WriteLine("통과: FeliCa 쓰기 응답 상태 파싱");
try { WriteGuard.Validate(new("00", CardFamily.Ntag, "", ""), 2, 4); throw new Exception("잠금 페이지 쓰기를 허용했습니다."); }
catch (InvalidOperationException) { Console.WriteLine("통과: NTAG 잠금 페이지 보호"); }
try { WriteGuard.Validate(new("00", CardFamily.MifareClassic, "", ""), 7, 16); throw new Exception("섹터 트레일러 쓰기를 허용했습니다."); }
catch (InvalidOperationException) { Console.WriteLine("통과: MIFARE 섹터 트레일러 보호"); }
try { WriteGuard.Validate(new("00", CardFamily.FelicaLiteS, "", ""), 0x82, 16); throw new Exception("FeliCa 시스템 블록 쓰기를 허용했습니다."); }
catch (InvalidOperationException) { Console.WriteLine("통과: FeliCa 시스템 블록 보호"); }

var type5Card = new CardInfo("E004015011223344", CardFamily.Iso15693, "05", "test");
using (var fake = new FakeReader(type5Card, 4, 33)) {
    fake.Set(0, Hex.Parse("E1401000"));
    fake.Set(1, Hex.Parse("0300FE00"));
    Check(NdefService.Read(fake, type5Card).Length == 0, "Type 5 빈 NDEF 읽기");
    Check(NdefService.Backup(fake, type5Card).Units.Count == 32, "Type 5 사용자 영역 백업");
    Check(NdefService.Write(fake, type5Card, false, "Type 5 테스트").Summary == Strings.NdefText("Type 5 테스트"), "Type 5 NDEF 쓰기·재읽기");
    Check(fake.Writes.First().Address == 1 && fake.Writes.First().Data[1] == 0 &&
        fake.Writes.Last().Address == 1 && fake.Writes.Last().Data[1] > 0, "NDEF 길이 마지막 확정");
}
using (var fake = new FakeReader(type5Card, 4, 34)) {
    fake.Set(0, Hex.Parse("E2400000"));
    fake.Set(1, Hex.Parse("00000010"));
    fake.Set(2, Hex.Parse("0300FE00"));
    Check(NdefService.Read(fake, type5Card).Length == 0, "Type 5 확장 CC 시작 주소");
}

var type3Card = new CardInfo("0112233445566778", CardFamily.FelicaLiteS, "06", "88B4");
using (var fake = new FakeReader(type3Card, 16, 14)) {
    var attr = new byte[16];
    attr[0] = 0x10; attr[1] = 0x01; attr[2] = 0x01; attr[4] = 13; attr[10] = 0x01;
    var sum = attr[..14].Sum(x => x); attr[14] = (byte)(sum >> 8); attr[15] = (byte)sum;
    fake.Set(0, attr);
    Check(NdefService.Read(fake, type3Card).Length == 0, "Type 3 빈 NDEF 읽기");
    Check(NdefService.Write(fake, type3Card, false, "FeliCa 테스트").Summary == Strings.NdefText("FeliCa 테스트"), "Type 3 NDEF 쓰기·재읽기");
}

var ntagCard = new CardInfo("04AABBCCDDEEFF", CardFamily.Ntag, "02", "00 · 4400");
using (var fake = new FakeReader(ntagCard, 4, 40)) {
    fake.Set(3, Hex.Parse("E1101200"));
    fake.Set(4, Hex.Parse("0300FE00"));
    var dump = CardWorkflows.Dump(fake, ntagCard, null, false);
    Check(dump.Units.Count == 40 && CardWorkflows.ToJson(dump).Contains("04AABBCCDDEEFF"), "주소별 NTAG 덤프 JSON");
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    try { CardWorkflows.Dump(fake, ntagCard, null, false, cancelled.Token); throw new Exception("취소된 덤프를 실행했습니다."); }
    catch (OperationCanceledException) { Console.WriteLine("통과: 덤프 취소"); }
    var range = CardWorkflows.ReadRange(fake, ntagCard, 36, 8, null, false);
    Check(range.Count == 8 && range[3].Hex is not null && range[4] is { Address: 40, Hex: null, Error: not null }, "끝을 넘는 범위 읽기의 주소별 오류");
}

Check(UidText.Formats("04A1B2C3") is { Hex: "04A1B2C3", HexReversed: "C3B2A104", Dec: "77705923", DecReversed: "3283263748" },
    "UID 16진·10진 정순/역순");
Check(UidText.Formats("04A1B") is null, "UID 형식 변환 입력 검증");
Check(ReaderDiscovery.FromPcscName("ACS ACR1552 1S CL Reader PICC 0")?.Kind == ReaderKind.Acr1552U &&
    ReaderDiscovery.FromPcscName("ACS ACR1552 1S CL Reader [ACR1552 1S CL Reader PICC] 00 00")?.Kind == ReaderKind.Acr1552U &&
    ReaderDiscovery.FromPcscName("ACS ACR1552 CL Reader 00 00")?.Kind == ReaderKind.Acr1552U &&
    ReaderDiscovery.FromPcscName("ACS ACR122 0")?.Kind == ReaderKind.Acr122U &&
    ReaderDiscovery.FromPcscName("ACS ACR122U PICC Interface 0")?.Kind == ReaderKind.Acr122U &&
    ReaderDiscovery.FromPcscName("ACS ACR122U 00 00")?.Kind == ReaderKind.Acr122U &&
    ReaderDiscovery.FromPcscName("ACS ACR1552 1S CL Reader SAM 0") is null &&
    ReaderDiscovery.FromPcscName("ACS ACR1552 1S CL Reader [ACR1552 1S CL Reader SAM] 01 00") is null &&
    ReaderDiscovery.FromPcscName("Microsoft UICC ISO Reader 2ca24328 0") is null, "PC/SC 리더 이름으로 종류 판별 (Windows·리눅스 드라이버)");
var serialPorts = ReaderDiscovery.SerialPorts();
Console.WriteLine($"정보: {ReaderDiscovery.PcscProblem() ?? "PC/SC 정상"} · 직렬 포트 {(serialPorts.Count == 0 ? "없음" : string.Join(", ", serialPorts))}");

if (args.Length >= 2 && (args[0] == "--live-at" || args[0] == "--live-at-write" || args[0] == "--live-at102" || args[0] == "--live-at102-write")) {
    var kind = args[0].StartsWith("--live-at102", StringComparison.Ordinal) ? ReaderKind.Atnfc102 : ReaderKind.Atnfc103;
    using var reader = new AtnfcReader(new(kind, args[1], $"{kind} · {args[1]}"));
    reader.Open();
    var card = reader.Detect();
    Check(card is not null, "실물 카드 감지");
    Console.WriteLine($"카드: {card!.DisplayFamily} {card.Uid} {card.Details}");
    if (card.Family == CardFamily.Ntag) {
        Console.WriteLine($"사용자 페이지 4: {Hex.Format(reader.ReadUnit(card, 4))}");
        Console.WriteLine($"NDEF: {NdefService.Read(reader, card).Summary}");
        if ((args[0] == "--live-at-write" || args[0] == "--live-at102-write") && args.Length == 3) {
            var result = NdefService.Write(reader, card, false, args[2]);
            Check(result.Summary == Strings.NdefText(args[2]), "실물 NDEF 쓰기 후 검증");
        }
    }
}

if (args.Length >= 1 && (args[0] == "--live-acr" || args[0] == "--live-acr-write")) {
    var choice = ReaderDiscovery.PcscReaders().FirstOrDefault()
        ?? throw new IOException("ACR1552U PICC 리더가 없습니다.");
    using var reader = ReaderDiscovery.Create(choice);
    reader.Open();
    var card = reader.Detect();
    Check(card is not null, "ACR1552U 카드 감지");
    Console.WriteLine($"카드: {card!.DisplayFamily} {card.Uid} {card.Details}");
    if (card.Family == CardFamily.Ntag) {
        Console.WriteLine($"사용자 페이지 4: {Hex.Format(reader.ReadUnit(card, 4))}");
        Console.WriteLine($"NDEF: {NdefService.Read(reader, card).Summary}");
        if (args[0] == "--live-acr-write" && args.Length == 2) {
            var result = NdefService.Write(reader, card, false, args[1]);
            Check(result.Summary == Strings.NdefText(args[1]), "ACR1552U NDEF 쓰기 후 검증");
        }
    }
}

if (args.Length >= 2 && (args[0] == "--live-pcr" || args[0] == "--live-pcr-write")) {
    using var reader = new Pn532Reader(new(ReaderKind.Pcr532, args[1], $"PCR532 · {args[1]}"));
    reader.Open();
    var card = reader.Detect();
    Check(card is not null, "PCR532 카드 감지");
    Console.WriteLine($"카드: {card!.DisplayFamily} {card.Uid} {card.Details}");
    if (card.Family == CardFamily.Ntag) {
        Console.WriteLine($"사용자 페이지 4: {Hex.Format(reader.ReadUnit(card, 4))}");
        Console.WriteLine($"NDEF: {NdefService.Read(reader, card).Summary}");
        if (args[0] == "--live-pcr-write" && args.Length == 3) {
            var result = NdefService.Write(reader, card, false, args[2]);
            Check(result.Summary == Strings.NdefText(args[2]), "PCR532 NDEF 쓰기 후 검증");
        }
    }
}

sealed class FakeReader(CardInfo card, int unitSize, int unitCount) : INfcReader
{
    private readonly Dictionary<int, byte[]> _units = Enumerable.Range(0, unitCount).ToDictionary(i => i, _ => new byte[unitSize]);
    public List<(int Address, byte[] Data)> Writes { get; } = [];
    public ReaderKind Kind => ReaderKind.Atnfc103;
    public string Name => "가짜 리더";
    public void Set(int address, byte[] value) => _units[address] = value.ToArray();
    public void Open() { }
    public CardInfo? Detect() => card;
    public byte[] ReadUnit(CardInfo _, int address, string? keyHex = null, bool keyB = false) =>
        _units.TryGetValue(address, out var unit) ? unit.ToArray() : throw new IOException($"주소 {address} 없음");
    public void WriteUnit(CardInfo _, int address, byte[] data, string? keyHex = null, bool keyB = false) {
        WriteGuard.Validate(card, address, data.Length);
        _units[address] = data.ToArray();
        Writes.Add((address, data.ToArray()));
    }
    public byte[] TransmitApdu(byte[] command) => throw new NotSupportedException();
    public void Dispose() { }
}
