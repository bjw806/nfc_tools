# NFC Tagger

ATNFC-102/103, ACR1552U, PCR532를 한 화면에서 사용하는 Windows NFC 도구입니다. 연결된 장치 중 한 대를 선택해 사용합니다.

## 실행

`NfcTagger-win-x64.zip`을 원하는 폴더에 풀고 `NfcTagger.exe`를 실행합니다. .NET 런타임 설치는 필요하지 않습니다. Windows에서 각 장치가 COM 포트 또는 PC/SC 리더로 인식되어야 하며, 필요한 장치 드라이버는 별도로 설치해야 합니다. 사용 설정은 실행 파일 옆의 `settings.json`에 저장됩니다. 키와 카드 데이터는 자동 저장하지 않습니다.

1. 리더를 USB로 연결하고 **새로고침**을 누릅니다.
2. ATNFC-102/103 또는 PCR532는 해당 COM 포트와 모델을 선택합니다. ACR1552U는 `PICC` 리더를 선택합니다.
3. **연결**을 누르고 카드를 올려 감지된 UID와 종류를 확인합니다.
4. NDEF, 메모리, APDU 화면에서 필요한 작업을 선택합니다.

## 기능과 경계

| 카드 | NDEF 텍스트/URL | 블록 읽기/쓰기 | APDU |
|---|---|---|---|
| NTAG/Ultralight | 미리 포맷된 Type 2 태그 | 사용자 페이지 | 해당 없음 |
| MIFARE Classic | 미지원 | 알려진 6바이트 Key A/B로 데이터 블록 | ACR의 전문가 콘솔에서 제조사 명령 가능 |
| ISO15693 | 미리 포맷된 Type 5 태그 | 일반 블록 | 해당 없음 |
| FeliCa Lite-S | 미리 포맷된 Type 3 태그 | 데이터 블록 1~13 (속성 블록 0은 NDEF 화면에서 관리) | 해당 없음 |
| ISO14443-4 | 미지원 | 직접 블록 작업 미지원 | 원시 APDU |

PCR532는 PN532 USB 직렬 프로토콜로 연결하며 ISO15693을 지원하지 않습니다. FeliCa Lite-S의 시스템 코드 `88B4`가 확인된 경우에만 사용자 블록 작업을 활성화합니다. 읽기/쓰기 가능 영역은 리더 펌웨어와 카드의 접근 권한에 따라 달라집니다.

**쓰기 전 확인**: 앱은 대상 UID와 기존 블록을 다시 읽고, 보호 영역 쓰기를 막으며, 쓴 뒤 같은 블록을 재읽어 비교합니다. NDEF 쓰기 전에는 관련 블록을 세션에 백업하고 메모리 화면의 **JSON 내보내기**로 저장할 수 있습니다. 카드 전체 덤프는 읽기와 JSON 내보내기만 지원합니다. 복원, UID 변경, 키 추출, 카드 에뮬레이션, 자동 포맷은 제공하지 않습니다.

전문가 APDU 화면은 원시 명령을 전송하므로 카드·리더별 명령 형식을 알고 있는 경우에만 사용하세요. 진단 로그에는 APDU 내용과 MIFARE 키가 기록되지 않습니다.

## 실물 검증 상태

2026-09-24에 ATNFC-102, ATNFC-103, ACR1552U, PCR532 각각에서 **같은 NTAG 태그**의 UID, 사용자 페이지, NDEF를 읽었고, 테스트 문구 쓰기 및 재읽기를 확인했습니다. MIFARE Classic, ISO15693, FeliCa Lite-S, ISO14443-4 카드가 없어 이 카드군의 실제 통신은 검증하지 못했습니다. Type 3/5 NDEF 흐름은 가짜 리더를 이용한 단위 검사까지 완료했습니다.

## 소스 빌드

Windows에서 .NET 10 SDK를 설치한 뒤 저장소 루트의 PowerShell에서 실행합니다.

```powershell
.\build.ps1
```

스크립트는 단위 검사를 실행한 뒤 `dist\NfcTagger-win-x64\NfcTagger.exe`와 `dist\NfcTagger-<Version>-win-x64.zip`을 만듭니다. 버전은 앱 프로젝트의 `<Version>` 값에서 읽습니다. 검사 없이 배포본만 다시 만들려면 `.\build.ps1 -SkipTests`를 사용합니다. 실행 파일은 `dist\NfcTagger-win-x64` 폴더 전체와 함께 사용해야 합니다.

장치 연결부는 `NfcTagger.Core`에 있습니다. ATNFC 문서는 `D:\repo\pitin\ATNFC` 폴더를 프로토콜 자료로만 참고했습니다. ACR1552U 명령은 [ACS 참고서](https://www.acs.com.hk/en/products/575/), PN532 프레임은 [NXP 사용자 설명서](https://www.nxp.com/docs/en/user-guide/141520.pdf)를 기준으로 구현했습니다.
