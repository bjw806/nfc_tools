using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Platform;
using NfcTagger.Core;

namespace NfcTagger.App;

// Out of the box Ubuntu keeps a desktop user away from the readers: serial ports belong to the dialout group and
// ModemManager probes the ATNFC as a modem, pcscd is not installed, and the kernel NFC driver claims the ACR122U.
static class LinuxSetup
{
    private const string RulesPath = "/etc/udev/rules.d/70-nfc-tagger.rules";

    // What Script does, for the confirmation dialog.
    public const string Summary = """
        • pcscd·libccid 설치 (ACR1552U·ACR122U용, 없을 때만)
        • 직렬 리더(ATNFC·PCR532) 사용 권한과 ModemManager 제외: /etc/udev/rules.d/70-nfc-tagger.rules
        • ACR122U를 가로채는 커널 NFC 드라이버(pn533_usb) 차단: /etc/modprobe.d/nfc-tagger-blacklist.conf
        • udev 규칙 다시 읽기, pcscd 켜기
        """;

    // Fixed text without user input; running it again changes nothing. It holds no single quote, so it also fits in
    // sh -c '…'. The rules file sorts before systemd's 73-seat-late.rules, which is what turns uaccess into access.
    // Only pn533_usb is blocked: blocking the nfc core too would switch off a laptop's own NFC.
    private const string Script = """
        set -e
        export DEBIAN_FRONTEND=noninteractive
        if command -v apt-get >/dev/null 2>&1; then
          missing=""
          for p in pcscd libccid; do dpkg -s "$p" >/dev/null 2>&1 || missing="$missing $p"; done
          if [ -n "$missing" ]; then apt-get install -y $missing; fi
        fi
        cat > /etc/udev/rules.d/70-nfc-tagger.rules <<"EOF"
        # NFC Tagger: ATNFC-102/103 (1a86:fe0c) and PCR532 (CH340, 1a86:7523) for the desktop user, without ModemManager.
        SUBSYSTEM=="tty", ATTRS{idVendor}=="1a86", ATTRS{idProduct}=="fe0c", TAG+="uaccess", ENV{ID_MM_DEVICE_IGNORE}="1"
        SUBSYSTEM=="tty", ATTRS{idVendor}=="1a86", ATTRS{idProduct}=="7523", TAG+="uaccess", ENV{ID_MM_DEVICE_IGNORE}="1"
        EOF
        echo "blacklist pn533_usb" > /etc/modprobe.d/nfc-tagger-blacklist.conf
        modprobe -r pn533_usb 2>/dev/null || true
        udevadm control --reload-rules
        udevadm trigger --subsystem-match=tty --action=change
        systemctl enable --now pcscd.socket 2>/dev/null || true
        systemctl try-restart pcscd.service 2>/dev/null || true
        """;

    public static string TerminalCommand => $"sudo sh -c '{Script}'";

    // Lines for the setup panel: what is ready and what is still missing.
    public static IReadOnlyList<string> Check(IReadOnlyCollection<string> deniedPorts)
    {
        var lines = new List<string> {
            ReaderDiscovery.PcscProblem() is { } pcsc ? "✗ " + pcsc : "✓ PC/SC(pcscd)에 연결됩니다. ACR1552U·ACR122U를 쓸 수 있습니다.",
            File.Exists(RulesPath) ? "✓ 직렬 리더(ATNFC·PCR532) 권한 규칙이 설치되어 있습니다." : "✗ 직렬 리더(ATNFC·PCR532) 권한 규칙이 없습니다."
        };
        if (Directory.Exists("/sys/module/pn533_usb"))
            lines.Add("✗ 커널 NFC 드라이버(pn533_usb)가 올라와 있어 ACR122U를 PC/SC로 쓸 수 없습니다.");
        if (deniedPorts.Count > 0)
            lines.Add($"✗ 권한이 없어 열지 못한 포트: {string.Join(", ", deniedPorts)}");
        if (Ch340WithoutPort())
            lines.Add("✗ PCR532(CH340)가 꽂혀 있는데 포트가 없습니다. 점자 단말기 프로그램 brltty가 가로챘을 수 있습니다. " +
                "점자 단말기를 쓰지 않는다면 터미널에서 sudo systemctl mask brltty-udev.service 를 실행하고 다시 꽂으세요.");
        return lines;
    }

    // brltty, installed by default, claims CH340 adapters as braille displays, so the ttyUSB port never appears.
    private static bool Ch340WithoutPort()
    {
        try {
            foreach (var device in Directory.EnumerateDirectories("/sys/bus/usb/devices")) {
                if (Attribute(device, "idVendor") != "1a86" || Attribute(device, "idProduct") != "7523") continue;
                var interfaces = Directory.EnumerateDirectories(device, Path.GetFileName(device) + ":*");
                if (!interfaces.Any(x => Directory.EnumerateDirectories(x, "ttyUSB*").Any())) return true;
            }
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return false;
    }

    private static string? Attribute(string device, string name)
    {
        var path = Path.Combine(device, name);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    // Runs Script as root through the desktop's polkit password prompt. Fails when pkexec is missing, when the prompt
    // was dismissed (exit 126) or no polkit agent could ask (127); the caller then offers TerminalCommand instead.
    public static async Task<(bool Ok, string Output)> RunAsync()
    {
        var start = new ProcessStartInfo("pkexec") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/bin/sh", "-c", Script }) start.ArgumentList.Add(argument);
        Process process;
        try { process = Process.Start(start) ?? throw new Win32Exception(); }
        catch (Win32Exception) { return (false, "pkexec을 찾을 수 없습니다."); }
        using (process) {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var lines = ((await output) + (await error)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return (process.ExitCode == 0, string.Join('\n', lines.TakeLast(12))); // apt-get can print a lot
        }
    }

    // A launcher for this executable in the desktop's app list. Exec is this file, so moving the folder needs a redo.
    public static string AddToMenu()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("실행 파일 경로를 알 수 없습니다.");
        // ~/.local/share; DoNotVerify because it may not exist yet, and the default then returns "".
        var data = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        var icon = Path.Combine(data, "icons", "nfc-tagger.png");
        var entry = Path.Combine(data, "applications", "nfc-tagger.desktop");
        Directory.CreateDirectory(Path.GetDirectoryName(icon)!);
        Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
        using (var source = AssetLoader.Open(new Uri("avares://NfcTagger/Assets/NfcTagger.png")))
        using (var target = File.Create(icon)) source.CopyTo(target);
        // Inside a quoted Exec argument ", ` and $ must be escaped.
        var exec = exe.Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$");
        File.WriteAllText(entry, $"""
            [Desktop Entry]
            Type=Application
            Name=NFC Tagger
            Comment=NFC 태그 읽기·쓰기
            Exec="{exec}"
            Icon={icon}
            Terminal=false
            Categories=Utility;
            StartupWMClass=NfcTagger

            """);
        return entry;
    }
}
