# NFC Tagger

NFC Tagger reads and writes NFC tags using ATNFC-102/103, ACR1552U, ACR122U and PCR532 readers on Windows.

## Features

- Card type and UID, with UID copy in hex or decimal (normal or reversed byte order)
- NDEF text and URL records (read and write)
- Memory read and write by block, full dump saved as JSON
- Raw APDU exchange
- Finds connected readers and connects automatically

Writes are confirmed in a dialog first and read back afterwards to verify them. Protected areas such as manufacturer blocks and sector trailers are not written.

## Supported readers

| Reader | Connection | Notes |
| --- | --- | --- |
| ATNFC-102, ATNFC-103 | USB serial | |
| PCR532 | USB serial (PN532) | No ISO15693 |
| ACR1552U | PC/SC | |
| ACR122U | PC/SC | No ISO15693 or FeliCa |

While connected to an ATNFC reader, the app turns off the reader's unsolicited reports (URC) and automatic beep, and turns them back on when it disconnects. Nothing is saved to the reader.

## Supported cards

| Card | NDEF | Memory | APDU |
| --- | :-: | :-: | :-: |
| NTAG / Ultralight | ✓ | ✓ | |
| MIFARE Classic | | ✓ | ACR readers only |
| ISO15693 | ✓ | ✓ | |
| FeliCa Lite-S | ✓ | ✓ | |
| ISO14443-4 | | | ✓ |

NDEF works on tags that are already NDEF formatted.

## Get the app

Download the zip from [Releases](../../releases), extract it and run `NfcTagger.exe`. The .NET runtime is included, so there is nothing else to install. Some readers may need a driver.

Settings are stored in `settings.json` next to the executable.

## Build

Requires the .NET 10 SDK.

```powershell
.\build.ps1
```

This creates the zip in `dist`. If the execution policy blocks the script, run `build.cmd` instead.
