namespace NfcTagger.Core;

// Current UI language and the messages Core shows to the user.
public static class Strings
{
    private static bool _korean;

    public static bool Korean
    {
        get => _korean;
        set { if (_korean == value) return; _korean = value; Changed?.Invoke(); }
    }

    public static event Action? Changed;

    public static string T(string en, string ko) => _korean ? ko : en;

    public static string UnknownCard => T("Unknown card", "알 수 없는 카드");
    public static string ReaderNotConnected => T("The reader is not connected.", "리더가 연결되지 않았습니다.");
    public static string NoCardDetected => T("No card detected.", "카드가 감지되지 않았습니다.");
    public static string CardSessionChanged => T("The card session changed. Detect the card again.", "카드 세션이 바뀌었습니다. 다시 감지하세요.");
    public static string CardLifted => T("The card was lifted off the reader. Place it again.", "카드가 리더에서 떨어졌습니다. 카드를 다시 올려 주세요.");
    public static string MemoryReadUnsupported => T("Direct memory reads aren't supported for this card.", "이 카드의 직접 메모리 읽기는 지원하지 않습니다.");
    public static string MifareKeyRequired => T("A MIFARE Classic key is required.", "MIFARE Classic 키가 필요합니다.");
    public static string MifareKeyLength => T("A MIFARE Classic key must be 6 bytes.", "MIFARE Classic 키는 6바이트여야 합니다.");
    public static string EnterMifareKey => T("Enter a 6-byte MIFARE Classic key.", "MIFARE Classic 6바이트 키를 입력하세요.");
    public static string EnterContent => T("Enter the content to write.", "쓸 내용을 입력하세요.");
    public static string HexFormat => T("HEX must be an even number of 0-9/A-F characters.", "HEX는 짝수 개의 0-9/A-F 문자여야 합니다.");

    // PC/SC
    public static string PcscLibraryMissing => T("The PC/SC library (libpcsclite) is missing.", "PC/SC 라이브러리(libpcsclite)가 없습니다.");
    public static string PcscServiceMissing => T("The PC/SC service (pcscd) is not installed or not running.", "PC/SC 서비스(pcscd)가 설치되어 있지 않거나 실행되지 않습니다.");
    public static string PcscAccessDenied => T("The PC/SC service denied access. Run the app from a local desktop session.", "PC/SC 서비스가 접근을 거부했습니다. 로컬 데스크톱 세션에서 실행하세요.");
    public static string PcscReaderMissing(string name) => T($"The PC/SC reader {name} is not connected.", $"{name} PC/SC 리더가 연결되어 있지 않습니다.");
    public static string PcscResponseShort => T("The PC/SC response is too short.", "PC/SC 응답이 짧습니다.");
    public static string CardCommandFailed(string response) => T($"Card/reader command failed: {response}", $"카드/리더 명령 실패: {response}");
    public static string Iso15693ReadFailed => T("ISO15693 block read failed", "ISO15693 블록 읽기 실패");
    public static string Iso15693WriteFailed => T("ISO15693 block write failed", "ISO15693 블록 쓰기 실패");

    // ATNFC
    public static string AtnfcNoResponse(string model) => T($"No ATNFC response on the selected port: {model}", $"선택한 포트에서 ATNFC 응답을 확인하지 못했습니다: {model}");
    public static string AtnfcWrongModel(string model) => T($"The selected model doesn't match the device: {model}", $"선택한 모델과 실제 장치가 다릅니다: {model}");
    public static string AtCommandTimeout(string command) => T($"AT command timed out: {command}", $"AT 명령 응답 시간 초과: {command}");
    public static string AtNoReply(string prefix) => T($"No {prefix} response.", $"{prefix} 응답이 없습니다.");
    public static string AtCardInfoFormat => T("Invalid card info format.", "카드 정보 형식이 잘못되었습니다.");
    public static string FelicaReadResponse => T("Invalid FeliCa read response or status flags.", "FeliCa 읽기 응답 또는 상태 플래그가 올바르지 않습니다.");
    public static string FelicaBlockShort => T("The FeliCa block data is too short.", "FeliCa 블록 데이터가 짧습니다.");
    public static string FelicaWriteResponse => T("Invalid FeliCa write response or status flags.", "FeliCa 쓰기 응답 또는 상태 플래그가 올바르지 않습니다.");

    // PN532
    public static string Pn532NoFirmware => T("No PN532 firmware response on the PCR532 port.", "PCR532 포트에서 PN532 펌웨어 응답을 확인하지 못했습니다.");
    public static string Pn532ResponseCode => T("Invalid PN532 response code.", "PN532 명령 응답 코드가 올바르지 않습니다.");
    public static string Pn532UidShort => T("The PN532 UID response is too short.", "PN532 UID 응답이 짧습니다.");
    public static string FelicaIdmShort => T("The FeliCa IDm response is too short.", "FeliCa IDm 응답이 짧습니다.");
    public static string FelicaSystemCode => T("System code 88B4", "시스템 코드 88B4");
    public static string Pn532ExchangeFailed(string status) => T($"PN532 card exchange failed (status {status})", $"PN532 카드 교환 실패 (상태 {status})");
    public static string None => T("none", "없음");
    public static string NtagReadShort => T("The NTAG read response is too short.", "NTAG 읽기 응답이 짧습니다.");
    public static string MifareBlockShort => T("The MIFARE block response is too short.", "MIFARE 블록 응답이 짧습니다.");
    public static string Pcr532MemoryUnsupported => T("The PCR532 can't read memory on this card.", "PCR532은 이 카드 메모리 읽기를 지원하지 않습니다.");
    public static string AuthUidShort => T("The UID for authentication is too short.", "인증용 UID가 짧습니다.");
    public static string Pn532FrameTooLong => T("A PN532 frame can be at most 255 bytes.", "PN532 프레임은 255바이트 이하입니다.");
    public static string Pn532FrameStart => T("Invalid PN532 frame start.", "PN532 프레임 시작이 올바르지 않습니다.");
    public static string Pn532FrameLength => T("Invalid PN532 frame length.", "PN532 프레임 길이가 올바르지 않습니다.");
    public static string Pn532Checksum => T("Invalid PN532 checksum.", "PN532 체크섬이 올바르지 않습니다.");
    public static string Pn532LengthError => T("PN532 frame length error", "PN532 프레임 길이 오류");
    public static string Pn532FrameTimeout => T("Timed out receiving a PN532 frame", "PN532 프레임 수신 시간 초과");
    public static string Pn532Timeout => T("The PN532 response timed out", "PN532 응답 시간 초과");

    // Writes and dumps
    public static string WriteUnit(int bytes) => T($"This card is written {bytes} bytes at a time.", $"이 카드의 쓰기 단위는 {bytes}바이트입니다.");
    public static string NtagProtected => T("Manufacturer, lock and CC pages can't be written.", "제조사·잠금·CC 페이지는 쓸 수 없습니다.");
    public static string MifareProtected => T("Manufacturer and MAD blocks and sector trailers can't be written.", "제조사·MAD 블록과 섹터 트레일러는 쓸 수 없습니다.");
    public static string Iso15693Protected => T("The ISO15693 CC (first block) can't be written directly.", "ISO15693 CC/첫 블록은 직접 쓸 수 없습니다.");
    public static string FelicaProtected => T("FeliCa system and configuration blocks can't be written.", "FeliCa 시스템·설정 블록은 쓸 수 없습니다.");
    public static string WriteUnsupported => T("Direct memory writes aren't supported for this card type.", "이 카드 종류의 직접 메모리 쓰기는 지원하지 않습니다.");
    public static string NtagOutOfRange => T("Outside the NTAG user data area.", "NTAG 사용자 데이터 영역을 벗어났습니다.");
    public static string CardChanged => T("The card changed. The write was cancelled.", "카드가 바뀌었습니다. 쓰기를 취소했습니다.");
    public static string BlockLengthMismatch => T("The data length doesn't match the block length.", "기존 블록 길이와 쓰기 데이터 길이가 다릅니다.");
    public static string VerifyFailed => T("Verification after writing failed.", "쓰기 후 재읽기 검증에 실패했습니다.");
    public static string DumpUnsupported => T("Memory dump isn't supported for this card.", "이 카드의 메모리 덤프는 지원하지 않습니다.");

    // NDEF
    public static string NdefEmpty => T("Empty NDEF", "NDEF 비어 있음");
    public static string NdefText(string text, string language) =>
        (language.Length == 0 ? T("Text: ", "텍스트: ") : T($"Text ({language}): ", $"텍스트 ({language}): ")) + text;
    public static string NdefOtherRecord(int tnf, string type, int length) => T($"TNF {tnf} / {type} record · {length} bytes", $"TNF {tnf} / {type} 레코드 · {length}바이트");
    public static string NdefTooShort => T("The NDEF message is too short.", "NDEF 메시지가 너무 짧습니다.");
    public static string NdefLengthShort => T("The NDEF length field is truncated.", "NDEF 길이 필드가 짧습니다.");
    public static string NdefIdLengthMissing => T("The NDEF ID length is missing.", "NDEF ID 길이가 없습니다.");
    public static string NdefRecordLength => T("Invalid NDEF record length.", "NDEF 레코드 길이가 잘못되었습니다.");
    public static string NdefTextRecord => T("Invalid NDEF text record.", "NDEF 텍스트 레코드가 잘못되었습니다.");
    public static string NdefNoTlv => T("No NDEF TLV found. Use a tag that is already NDEF formatted.", "NDEF TLV가 없습니다. 먼저 NDEF로 포맷된 테스트 태그를 사용하세요.");
    public static string NtagNoCc => T("No NTAG NDEF CC found.", "NTAG NDEF CC가 없습니다.");
    public static string NdefBackupUnsupported => T("NDEF backup isn't supported for this card.", "이 카드의 NDEF 백업을 지원하지 않습니다.");
    public static string NdefReadUnsupported => T("NDEF read isn't supported for this card. Use the Memory or APDU page.", "이 카드의 NDEF 읽기는 지원하지 않습니다. 메모리 또는 APDU 화면을 사용하세요.");
    public static string NdefWriteUnsupported => T("NDEF write isn't supported for this card.", "이 카드의 NDEF 쓰기는 지원하지 않습니다.");
    public static string NdefVerifyFailed => T("NDEF verification after writing failed.", "NDEF 쓰기 후 검증에 실패했습니다.");
    public static string NdefNoCc => T("No NDEF CC found.", "NDEF CC가 없습니다.");
    public static string Iso15693NoCc => T("No ISO15693 NDEF CC found.", "ISO15693 NDEF CC가 없습니다.");
    public static string Iso15693CcLength => T("Unexpected ISO15693 CC block length.", "ISO15693 CC 블록 길이가 다릅니다.");
    public static string Iso15693Capacity => T("Unknown ISO15693 NDEF capacity.", "ISO15693 NDEF 용량을 알 수 없습니다.");
    public static string BlockSizeInvalid => T("Invalid card block size.", "카드 블록 크기가 올바르지 않습니다.");
    public static string BlockSizeChanged => T("The card block size changed.", "카드 블록 크기가 변경되었습니다.");
    public static string NdefCcShort => T("The NDEF CC is too short.", "NDEF CC가 짧습니다.");
    public static string NdefAccessUnknown => T("Can't confirm the NDEF format or write access.", "NDEF 포맷 또는 쓰기 권한을 확인할 수 없습니다.");
    public static string NdefTrailingTlv => T("Another TLV follows the NDEF, so a safe overwrite can't be guaranteed.", "NDEF 뒤에 다른 TLV가 있어 안전한 덮어쓰기를 보장할 수 없습니다.");
    public static string NdefReservedBytes => T("The NDEF data overlaps the tag's lock or reserved bytes, which isn't supported.", "NDEF 데이터가 태그의 잠금·예약 영역과 겹칩니다. 이런 태그는 지원하지 않습니다.");
    public static string TagFull => T("Not enough space on the tag.", "태그 용량이 부족합니다.");
    public static string FelicaNdefTooLong => T("The FeliCa NDEF length exceeds the capacity.", "FeliCa NDEF 길이가 용량을 초과합니다.");
    public static string FelicaNotType3 => T("Not a FeliCa Type 3 NDEF attribute block.", "FeliCa Type 3 NDEF 속성 블록이 아닙니다.");
    public static string FelicaChecksum => T("FeliCa attribute block checksum error", "FeliCa 속성 블록 체크섬 오류");
    public static string FelicaReadOnly => T("The FeliCa tag is read-only.", "FeliCa 태그가 읽기 전용입니다.");
    public static string FelicaFull => T("Not enough space on the FeliCa tag.", "FeliCa 태그 용량이 부족합니다.");
}
