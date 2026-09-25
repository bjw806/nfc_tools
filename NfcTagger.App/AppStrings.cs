using static NfcTagger.Core.Strings;

namespace NfcTagger.App;

// UI text in English and Korean. XAML uses these through {local:Tr Name}.
public static class AppStrings
{
    // Navigation and device bar
    public static string NavCard => T("Card", "카드 정보");
    public static string NavMemory => T("Memory", "메모리");
    public static string NavLog => T("Log", "진단 로그");
    public static string Language => T("Language", "언어");
    public static string NoReaders => T("No readers found", "감지된 리더 없음");
    public static string Reader => T("Reader", "리더");
    public static string Rescan => T("Find readers again", "리더 다시 찾기");
    public static string RescanTip => T("Find readers again (F5)", "리더 다시 찾기 (F5)");
    public static string Connect => T("Connect", "연결");
    public static string ConnectedHint => T("Connected · watching for cards", "연결됨 · 카드를 자동으로 감지합니다");
    public static string Disconnect => T("Disconnect", "연결 해제");
    public static string CopyUid => T("Copy UID", "UID 복사");
    public static string Close => T("Close", "닫기");
    public static string CloseNotice => T("Close notification", "알림 닫기");
    public static string Ready => T("Ready", "준비됨");
    public static string Cancel => T("Cancel", "취소");
    public static string Copy => T("Copy", "복사");
    public static string Clear => T("Clear", "지우기");

    // Card page
    public static string ScanNow => T("Scan now (F5)", "지금 스캔 (F5)");
    public static string CurrentCard => T("Current card", "현재 카드");
    public static string CardDetails => T("Card details", "카드 상세");
    public static string RawType => T("Raw type", "원시 타입");
    public static string Details => T("Details", "상세");
    public static string HexTip => T("Byte order as read by the reader (big-endian)", "리더가 읽은 바이트 순서 그대로 (빅엔디언)");
    public static string CopyHex => T("Copy HEX", "HEX 복사");
    public static string HexReversed => T("HEX reversed", "HEX 역순");
    public static string HexReversedTip => T("Byte order reversed (little-endian)", "바이트 순서를 뒤집은 값 (리틀엔디언)");
    public static string CopyHexReversed => T("Copy HEX reversed", "HEX 역순 복사");
    public static string Decimal => T("Decimal", "10진");
    public static string DecimalTip => T("HEX as a decimal number", "HEX를 10진수로 바꾼 값");
    public static string CopyDecimal => T("Copy decimal", "10진 복사");
    public static string DecimalReversed => T("Decimal reversed", "10진 역순");
    public static string DecimalReversedTip => T("HEX reversed as a decimal number", "HEX 역순을 10진수로 바꾼 값");
    public static string CopyDecimalReversed => T("Copy decimal reversed", "10진 역순 복사");
    public static string CardActions => T("What you can do with this card", "이 카드로 할 수 있는 작업");
    public static string NoCard => T("No card", "카드 없음");
    public static string NoReader => T("No reader", "리더 미연결");
    public static string PlaceCardHint => T("Place a card on the reader", "리더 위에 카드를 올려주세요");
    public static string ConnectReaderHint => T("Connect a reader first", "먼저 리더를 연결하세요");
    public static string PlaceCardTitle => T("Place a card on the reader", "카드를 리더 위에 올려주세요");
    public static string ConnectReaderTitle => T("Connect a reader", "리더를 연결하세요");
    public static string CardEmptyConnected => T("Cards are detected automatically\nand their type and UID are shown.",
        "카드를 올리면 자동으로 감지해\n종류와 UID를 보여줍니다.");
    public static string CardEmptyDisconnected => T("Plug in a reader and it connects automatically.\nTo pick one yourself, choose it from the list above and click Connect.",
        "리더를 USB에 꽂으면 자동으로 찾아 연결합니다.\n직접 고르려면 위쪽 목록에서 선택한 뒤 연결을 누르세요.");
    public static string NdefTile => T("Read and write text or URLs", "텍스트·URL을 읽고 씁니다");
    public static string MemoryTile => T("Read and write blocks, full dump", "블록 읽기·쓰기, 전체 덤프");
    public static string ApduTile => T("Send raw APDU commands", "원시 APDU 명령을 보냅니다");
    public static string NotSupported => T("Not supported by this card", "이 카드는 지원하지 않습니다");
    public static string NoticeNoReader => T("No reader is connected. Pick a reader above and connect.", "리더가 연결되지 않았습니다. 위쪽에서 리더를 고른 뒤 연결하세요.");
    public static string NoticeNoCard => T("No card detected. Place a card on the reader.", "카드가 감지되지 않았습니다. 리더 위에 카드를 올려주세요.");
    public static string NoticeNoNdef(string family) => T($"This card ({family}) doesn't support NDEF.", $"{family} 카드는 NDEF 읽기·쓰기를 지원하지 않습니다.");
    public static string NoticeNoMemory(string family) => T($"This card ({family}) doesn't support direct memory access.", $"{family} 카드는 직접 메모리 읽기·쓰기를 지원하지 않습니다.");
    public static string NoticeNoApdu => T("APDU works only with ISO14443-4 cards or ACR readers (ACR1552U, ACR122U).", "APDU는 ISO14443-4 카드나 ACR 리더(ACR1552U·ACR122U)에서만 쓸 수 있습니다.");

    // NDEF page
    public static string ReadTag => T("Read tag", "태그 읽기");
    public static string Read => T("Read", "읽기");
    public static string NdefEmptyHint => T("Click Read tag to show the tag's content here.", "태그 읽기를 누르면 여기에 표시됩니다.");
    public static string CopyContent => T("Copy content", "내용 복사");
    public static string CopyNdefContent => T("Copy NDEF content", "NDEF 내용 복사");
    public static string RawHex => T("Raw HEX", "원시 HEX");
    public static string Write => T("Write", "쓰기");
    public static string TextKind => T("Text", "텍스트");
    public static string ContentToWrite => T("Content to write", "쓸 내용");
    public static string NdefLanguage => T("Language code of the text", "텍스트의 언어 코드");
    public static string WriteToCard => T("Write to card", "카드에 쓰기");
    public static string UrlExample => T("e.g. https://example.com", "예: https://example.com");
    public static string SavedAsUtf8 => T("Saved as UTF-8 text.", "UTF-8 텍스트로 저장합니다.");
    public static string Bytes(int count) => T($"{count} bytes", $"{count}바이트");
    public static string TooLong => T("Too long", "내용이 너무 깁니다");
    public static string Empty => T("(empty)", "(비어 있음)");
    public static string ActionRead => T("read", "읽음");
    public static string ActionWritten => T("written and verified", "쓰고 검증함");
    public static string ReadingNdef => T("Reading NDEF", "NDEF 읽기");
    public static string NdefReadLog(int length) => T($"NDEF read: {length} bytes", $"NDEF 읽기: {length}바이트");
    public static string WriteNdefTitle => T("Write NDEF", "NDEF 쓰기");
    public static string WriteNdefBody(string card, bool uri) => T(
        $"{card}\nThis replaces the card's NDEF with the {(uri ? "URL" : "text")} below.\nThe current content is backed up first.",
        $"{card}\n이 카드의 NDEF를 아래 {(uri ? "URL" : "텍스트")}로 덮어씁니다.\n기존 내용은 쓰기 전에 백업합니다.");
    public static string WritingNdef => T("Backing up, writing and verifying NDEF", "NDEF 백업·쓰기·검증");
    public static string NdefWrittenLog(int length) => T($"NDEF written and verified: {length} bytes", $"NDEF 쓰기 검증 완료: {length}바이트");
    public static string NdefWritten(int length) => T(
        $"Written and verified ({length} bytes).\nThe backup made before writing can be saved with Export JSON on the Memory page.",
        $"쓰기와 재읽기 검증을 마쳤습니다 ({length}바이트).\n쓰기 전 백업은 메모리 화면의 JSON 내보내기로 저장할 수 있습니다.");

    // Memory page
    public static string StartAddress => T("Start address", "시작 주소");
    public static string CountMax => T("Count (max 64)", "개수 (최대 64)");
    public static string Count => T("Count", "개수");
    public static string MifareKeyLabel => T("MIFARE key (cleared when the card changes)", "MIFARE 키 (카드가 바뀌면 지워짐)");
    public static string MifareKey => T("MIFARE key", "MIFARE 키");
    public static string MifareKeyTip => T("6 bytes in HEX (e.g. FFFFFFFFFFFF)", "6바이트 HEX (예: FFFFFFFFFFFF)");
    public static string ReadRange => T("Read range", "범위 읽기");
    public static string ReadAll => T("Read all", "전체 읽기");
    public static string CancelRead => T("Cancel read", "읽기 취소");
    public static string ExportJson => T("Export JSON", "JSON 내보내기");
    public static string ExportJsonTip => T("Saves the last full read or the backup made before a write as a JSON file", "마지막 전체 읽기 또는 쓰기 전 백업을 JSON 파일로 저장합니다");
    public static string ReadResults => T("Read results", "읽기 결과");
    public static string Address => T("Address", "주소");
    public static string Status => T("Status", "상태");
    public static string MemoryEmptyHint => T("Click Read range or Read all to show the results here.", "범위 읽기나 전체 읽기를 누르면 결과가 여기에 표시됩니다.");
    public static string WriteAddress => T("Write address", "쓰기 주소");
    public static string NewDataLabel => T("New data (HEX) · pick a row in the table to fill in its current value", "새 데이터 (HEX) · 표에서 행을 고르면 현재 값이 채워집니다");
    public static string NewData => T("New data", "새 데이터");
    public static string ReviewAndWrite => T("Review and write", "검토 후 쓰기");
    public static string ReviewAndWriteTip => T("Protected areas (manufacturer, lock, sector trailers) are blocked automatically", "보호 영역(제조사·잠금·섹터 트레일러)은 자동으로 막습니다");
    public static string EnterMifareKeyAB => T("Enter the MIFARE Classic key A or key B.", "MIFARE Classic Key A 또는 Key B를 입력하세요.");
    public static string AddressRange(string name) => T($"{name} must be a number from 0 to 255.", $"{name}는 0~255 사이 숫자여야 합니다.");
    public static string CountRange => T("Count must be 1 to 64 and can't go past address 255.", "개수는 1~64이며 주소 255를 넘을 수 없습니다.");
    public static string RangeSource(int address, int count) => T($"{count} from address {address}", $"주소 {address}부터 {count}개");
    public static string AllSource(int count) => T($"All {count} addresses", $"전체 {count}개 주소");
    public static string WriteSource(int address) => T($"Write to address {address}", $"주소 {address} 쓰기");
    public static string ReadingMemory => T("Reading memory", "메모리 읽기");
    public static string MemoryReadLog(int address, int count) => T($"Memory read: {count} from address {address}", $"메모리 읽기: {address}부터 {count}개");
    public static string ReadingAllMemory => T("Reading all memory", "전체 메모리 읽기");
    public static string DumpLog(int count) => T($"Full read: {count} addresses", $"전체 읽기: {count}개 주소");
    public static string DumpCancelled => T("Full read cancelled.", "전체 읽기를 취소했습니다.");
    public static string DumpCancelledLog => T("Full read cancelled", "전체 읽기 취소");
    public static string NoSaveDialog => T("A save dialog can't be opened here.", "이 환경에서는 저장 대화상자를 열 수 없습니다.");
    public static string JsonFile => T("JSON file", "JSON 파일");
    public static string DumpExportedLog => T("Dump exported", "덤프 내보내기 완료");
    public static string SavedJson(string path) => T($"Saved as JSON: {path}", $"JSON으로 저장했습니다: {path}");
    public static string FelicaAttributeBlock => T("Don't write the FeliCa NDEF attribute block directly. Use the NDEF page.", "FeliCa NDEF 속성 블록은 직접 쓰지 마세요. NDEF 화면을 사용하세요.");
    public static string ReadingCurrentData => T("Checking the current data", "기존 데이터 확인");
    public static string WriteMemoryTitle => T("Write memory", "메모리 쓰기");
    public static string WriteMemoryBody(string card, int address) => T(
        $"{card}\nWrites to address {address}, then reads it back to verify.",
        $"{card}\n주소 {address}에 쓰고, 다시 읽어 검증합니다.");
    public static string WritingMemory => T("Writing and verifying memory", "메모리 쓰기·검증");
    public static string MemoryWrittenLog(int address) => T($"Memory written and verified: address {address}", $"메모리 쓰기 검증 완료: 주소 {address}");
    public static string MemoryWritten(int address) => T($"Wrote address {address} and verified it.", $"주소 {address}에 쓰고 재읽기 검증을 마쳤습니다.");
    public static string CurrentValue => T("Current value", "기존 값");
    public static string NewValue => T("New value", "새 값");
    public static string Verified => T("Verified", "검증 완료");

    // APDU page
    public static string CommandHex => T("Command (HEX)", "명령 (HEX)");
    public static string ApduCommand => T("APDU command", "APDU 명령");
    public static string Send => T("Send", "전송");
    public static string ApduHint => T("Press Enter to send. Use this only if you know the command format for your card and reader.", "Enter로 전송합니다. 카드·리더별 명령 형식을 아는 경우에만 사용하세요.");
    public static string ClearHistory => T("Clear history", "기록 지우기");
    public static string History => T("History", "기록");
    public static string ApduEmptyHint => T("Sent commands and responses appear here. Click an entry to reuse its command.", "보낸 명령과 응답이 여기에 쌓입니다. 항목을 누르면 명령을 다시 입력합니다.");
    public static string SendingApdu => T("Sending APDU", "APDU 전송");
    public static string NoData => T("(no data)", "(데이터 없음)");
    public static string ApduLog(int sent, int received) => T($"APDU sent: {sent} bytes, response {received} bytes", $"APDU 전송: {sent}바이트, 응답 {received}바이트");
    public static string ErrorShort => T("Error", "오류");
    public static string ApduFailed => T("APDU failed", "APDU 전송 실패");
    public static string ApduErrorLog(string message) => T($"APDU error: {message}", $"APDU 오류: {message}");

    // Log page and Linux setup
    public static string LogHint => T("Command data and MIFARE keys are never logged. The log is cleared when the app closes.", "명령 내용과 MIFARE 키는 남기지 않습니다. 앱을 닫으면 지워집니다.");
    public static string CheckAgain => T("Check again", "다시 점검");
    public static string LinuxSetupTitle => T("Linux device setup", "리눅스 장치 설정");
    public static string LinuxSetupIntro => T("A fresh Ubuntu install can't use the readers yet. Enter the admin password once and the app sets up everything it needs.",
        "우분투 기본 상태에서는 리더를 바로 쓸 수 없습니다. 관리자 암호를 한 번 입력하면 필요한 설정을 모두 합니다.");
    public static string AutoSetup => T("Auto setup (admin password)", "자동 설정 (관리자 암호)");
    public static string CopyTerminalCommand => T("Copy terminal command", "터미널 명령 복사");
    public static string CopyTerminalCommandTip => T("Copies a command that runs the same setup in a terminal", "같은 설정을 터미널에서 직접 실행하는 명령을 복사합니다");
    public static string AddToMenu => T("Add to app menu", "앱 메뉴에 추가");
    public static string AddToMenuTip => T("Adds this executable to the Ubuntu app list. Click again if you move the folder", "이 실행 파일을 우분투 앱 목록에 등록합니다. 폴더를 옮기면 다시 누르세요");
    public static string LinuxSetupSummary => T("""
        • Install pcscd and libccid (for ACR1552U and ACR122U, only if missing)
        • Access to the serial readers (ATNFC, PCR532) and ModemManager exclusion: /etc/udev/rules.d/70-nfc-tagger.rules
        • Block the kernel NFC driver (pn533_usb) that claims the ACR122U: /etc/modprobe.d/nfc-tagger-blacklist.conf
        • Reload the udev rules and start pcscd
        """, """
        • pcscd·libccid 설치 (ACR1552U·ACR122U용, 없을 때만)
        • 직렬 리더(ATNFC·PCR532) 사용 권한과 ModemManager 제외: /etc/udev/rules.d/70-nfc-tagger.rules
        • ACR122U를 가로채는 커널 NFC 드라이버(pn533_usb) 차단: /etc/modprobe.d/nfc-tagger-blacklist.conf
        • udev 규칙 다시 읽기, pcscd 켜기
        """);
    public static string LinuxSetupConfirm => T("The following is set up with admin rights. Enter the admin password in the window that opens next.",
        "관리자 권한으로 아래 설정을 합니다. 이어서 뜨는 창에 관리자 암호를 입력하세요.");
    public static string SetUp => T("Set up", "설정");
    public static string LinuxSetupRunning => T("Setting up Linux devices…", "리눅스 장치 설정 중…");
    public static string LinuxSetupLog(bool ok, string output) => T($"Linux device setup {(ok ? "done" : "failed")}{output}", $"리눅스 장치 설정 {(ok ? "완료" : "실패")}{output}");
    public static string LinuxSetupDone => T("Linux device setup finished.", "리눅스 장치 설정을 마쳤습니다.");
    public static string LinuxSetupDoneNotice => T("Setup finished. Unplug the ACR122U and plug it back in.", "설정을 마쳤습니다. ACR122U는 한 번 뽑았다 다시 꽂으세요.");
    public static string RestartForAcr => T("\nIf the ACR readers still don't show up, restart the app.", "\nACR 리더가 계속 안 보이면 앱을 다시 시작하세요.");
    public static string LinuxSetupFailed => T("Linux device setup failed.", "리눅스 장치 설정을 하지 못했습니다.");
    public static string LinuxSetupFailedNotice => T("Auto setup failed (password prompt cancelled, no admin prompt available, etc.). Click Copy terminal command and run it in a terminal.",
        "자동 설정을 하지 못했습니다(암호 입력 취소, 관리자 인증 창 없음 등). '터미널 명령 복사'를 눌러 터미널에서 실행하세요.");
    public static string TerminalCommandNotice => T("Paste it into a terminal, press Enter and enter the admin password. When it's done, click Check again.",
        "터미널에 붙여 넣고 Enter를 누른 뒤 관리자 암호를 입력하세요. 끝나면 '다시 점검'을 누르세요.");
    public static string AddedToMenuLog(string entry) => T($"Added to app menu: {entry}", $"앱 메뉴 등록: {entry}");
    public static string AddedToMenu => T("Added to the app menu. If you move the executable to another folder, click it again.", "앱 메뉴에 추가했습니다. 실행 파일을 다른 폴더로 옮기면 다시 누르세요.");
    public static string PcscOk => T("✓ Connected to PC/SC (pcscd). ACR1552U and ACR122U can be used.", "✓ PC/SC(pcscd)에 연결됩니다. ACR1552U·ACR122U를 쓸 수 있습니다.");
    public static string RulesInstalled => T("✓ The access rule for serial readers (ATNFC, PCR532) is installed.", "✓ 직렬 리더(ATNFC·PCR532) 권한 규칙이 설치되어 있습니다.");
    public static string RulesMissing => T("✗ The access rule for serial readers (ATNFC, PCR532) is missing.", "✗ 직렬 리더(ATNFC·PCR532) 권한 규칙이 없습니다.");
    public static string Pn533Loaded => T("✗ The kernel NFC driver (pn533_usb) is loaded, so the ACR122U can't be used over PC/SC.", "✗ 커널 NFC 드라이버(pn533_usb)가 올라와 있어 ACR122U를 PC/SC로 쓸 수 없습니다.");
    public static string PortsDeniedLine(string ports) => T($"✗ Ports that couldn't be opened for lack of permission: {ports}", $"✗ 권한이 없어 열지 못한 포트: {ports}");
    public static string BrlttyLine => T("✗ A PCR532 (CH340) is plugged in but has no port. The braille display service brltty may have claimed it. If you don't use a braille display, run sudo systemctl mask brltty-udev.service in a terminal and plug it in again.",
        "✗ PCR532(CH340)가 꽂혀 있는데 포트가 없습니다. 점자 단말기 프로그램 brltty가 가로챘을 수 있습니다. 점자 단말기를 쓰지 않는다면 터미널에서 sudo systemctl mask brltty-udev.service 를 실행하고 다시 꽂으세요.");
    public static string PkexecMissing => T("pkexec not found.", "pkexec을 찾을 수 없습니다.");
    public static string ExePathUnknown => T("Can't find the executable path.", "실행 파일 경로를 알 수 없습니다.");

    // Status bar, log and notices
    public static string Error(string message) => T($"Error: {message}", $"오류: {message}");
    public static string Busy => T("Another operation is in progress.", "다른 작업이 진행 중입니다.");
    public static string Working(string label) => T($"{label}…", $"{label} 중…");
    public static string Finished(string label, double seconds) => T($"{label}: done ({seconds:0.00} s)", $"{label} 완료 ({seconds:0.00}초)");
    public static string ConnectReaderFirst => T("Connect a reader first.", "먼저 리더를 연결하세요.");
    public static string DetectCardFirst => T("Place a card on the reader first.", "먼저 카드를 감지하세요.");
    public static string SearchingReaders => T("Looking for readers…", "리더를 찾는 중…");
    public static string PortsInUse(string ports) => T($" · In use by another program: {ports}", $" · 다른 프로그램이 사용 중: {ports}");
    public static string PortsDenied(string ports) => T($" · No permission: {ports}", $" · 권한 없음: {ports}");
    public static string ReaderSearchLog(double seconds, string found) => T($"Reader search ({seconds:0.0} s): {found}", $"리더 검색 ({seconds:0.0}초): {found}");
    public static string FoundReaders(int count) => T(count == 1 ? "Found 1 reader." : $"Found {count} readers.", $"리더 {count}대를 찾았습니다.");
    public static string NoReadersLinux => T("No readers found. Check the USB connection and the Linux device setup on the Log page.", "인식된 리더가 없습니다. USB 연결과 진단 로그 화면의 리눅스 장치 설정을 확인하세요.");
    public static string NoReadersWindows => T("No readers found. Check the USB connection and drivers.", "인식된 리더가 없습니다. USB 연결과 드라이버를 확인하세요.");
    public static string PortsDeniedNotice(string ports) => T(
        $"Some ports couldn't be opened for lack of permission ({ports}). Click Auto setup under Linux device setup on the Log page.",
        $"권한이 없어 열지 못한 포트가 있습니다({ports}). 진단 로그 화면의 리눅스 장치 설정에서 자동 설정을 누르세요.");
    public static string AutoConnecting(string name) => T($"Auto-connect: {name}", $"자동 연결: {name}");
    public static string ConnectingReader => T("Connecting to the reader", "리더 연결");
    public static string ConnectedLog(string name) => T($"Connected: {name}", $"연결됨: {name}");
    public static string ReaderDisconnectedLog => T("Reader disconnected", "리더 연결 해제");
    public static string ReaderDisconnected => T("Disconnected from the reader.", "리더 연결을 해제했습니다.");
    public static string DetectingCard => T("Detecting the card", "카드 감지");
    public static string PlaceCard => T("Place a card on the reader.", "카드를 리더 위에 올려주세요.");
    public static string DetectRetry => T("Card detection failed. Retrying.", "카드 감지 오류. 다시 시도합니다.");
    public static string DetectErrorLog(int attempt, string message) => T($"Card detection error (attempt {attempt}): {message}", $"카드 감지 오류 ({attempt}회째): {message}");
    public static string ReaderLost => T("The reader was disconnected.", "리더 연결이 끊겼습니다.");
    public static string ReaderLostNotice => T("The reader was disconnected. Check the cable and connect again.", "리더 연결이 끊겼습니다. 케이블을 확인한 뒤 다시 연결하세요.");
    public static string ReaderLostLog(string message) => T($"Reader lost: {message}", $"리더 연결 끊김: {message}");
    public static string CardRemovedLog => T("Card removed", "카드 제거됨");
    public static string CardRemoved => T("Card removed.", "카드가 제거되었습니다.");
    public static string CardDetectedLog(string family, string uid) => T($"Card detected: {family} · {uid}", $"카드 감지: {family} · {uid}");
    public static string CardDetected(string family) => T($"{family} card detected.", $"{family} 카드를 감지했습니다.");
    public static string CopiedUid => T("Copied the UID.", "UID를 복사했습니다.");
    public static string CopiedUidFormat(string format) => T($"Copied the UID ({format}).", $"UID {format} 값을 복사했습니다.");
    public static string CopiedNdef => T("Copied the NDEF content.", "NDEF 내용을 복사했습니다.");
    public static string CopiedLog => T("Copied the log.", "로그를 복사했습니다.");
    public static string CopiedTerminalCommand => T("Copied the terminal command.", "터미널 명령을 복사했습니다.");
    public static string ClipboardUnavailable => T("The clipboard isn't available. Try again in a moment.", "클립보드를 사용할 수 없습니다. 잠시 후 다시 시도하세요.");
}
