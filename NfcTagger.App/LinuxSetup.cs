using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Platform;
using NfcTagger.Core;

namespace NfcTagger.App;

// Stock Ubuntu keeps the readers away from the desktop user: serial ports need the dialout group,
// ModemManager probes the ATNFC, pcscd isn't installed and pn533_usb grabs the ACR122U.
static class LinuxSetup
{
    private const string RulesPath = "/etc/udev/rules.d/70-nfc-tagger.rules";

    // Safe to re-run. No single quotes (also used in sh -c '...').
    // The rules file must sort before 73-seat-late.rules for uaccess to apply.
    // Only pn533_usb is blocked; blocking nfc too would turn off a laptop's built-in NFC.
    private const string Script = """
        set -e
        export DEBIAN_FRONTEND=noninteractive
        if command -v apt-get >/dev/null 2>&1; then
          missing=""
          for p in pcscd libccid; do dpkg -s "$p" >/dev/null 2>&1 || missing="$missing $p"; done
          if [ -n "$missing" ]; then apt-get install -y $missing; fi
        fi
        cat > /etc/udev/rules.d/70-nfc-tagger.rules <<"EOF"
        # NFC Tagger: ATNFC-102/103 (1a86:fe0c), PCR532 (CH340, 1a86:7523)
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

    public static IReadOnlyList<string> Check(IReadOnlyCollection<string> deniedPorts)
    {
        var lines = new List<string> {
            ReaderDiscovery.PcscProblem() is { } pcsc ? "✗ " + pcsc : AppStrings.PcscOk,
            File.Exists(RulesPath) ? AppStrings.RulesInstalled : AppStrings.RulesMissing
        };
        if (Directory.Exists("/sys/module/pn533_usb"))
            lines.Add(AppStrings.Pn533Loaded);
        if (deniedPorts.Count > 0)
            lines.Add(AppStrings.PortsDeniedLine(string.Join(", ", deniedPorts)));
        if (Ch340WithoutPort())
            lines.Add(AppStrings.BrlttyLine);
        return lines;
    }

    // brltty grabs CH340 adapters as braille displays, so ttyUSB never shows up.
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

    // Runs Script as root via pkexec. Exit 126/127 means the prompt was cancelled or there's no polkit
    // agent; the caller then shows TerminalCommand instead.
    public static async Task<(bool Ok, string Output)> RunAsync()
    {
        var start = new ProcessStartInfo("pkexec") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/bin/sh", "-c", Script }) start.ArgumentList.Add(argument);
        Process process;
        try { process = Process.Start(start) ?? throw new Win32Exception(); }
        catch (Win32Exception) { return (false, AppStrings.PkexecMissing); }
        using (process) {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var lines = ((await output) + (await error)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return (process.ExitCode == 0, string.Join('\n', lines.TakeLast(12))); // apt-get can print a lot
        }
    }

    // Adds a .desktop entry for this executable. Needs to be done again if the folder moves.
    public static string AddToMenu()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException(AppStrings.ExePathUnknown);
        // ~/.local/share. DoNotVerify because it may not exist yet, and the default returns "" then.
        var data = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        var icon = Path.Combine(data, "icons", "nfc-tagger.png");
        var entry = Path.Combine(data, "applications", "nfc-tagger.desktop");
        Directory.CreateDirectory(Path.GetDirectoryName(icon)!);
        Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
        using (var source = AssetLoader.Open(new Uri("avares://NfcTagger/Assets/NfcTagger.png")))
        using (var target = File.Create(icon)) source.CopyTo(target);
        // " ` and $ must be escaped inside a quoted Exec value
        var exec = exe.Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$");
        File.WriteAllText(entry, $"""
            [Desktop Entry]
            Type=Application
            Name=NFC Tagger
            Comment=Read and write NFC tags
            Comment[ko]=NFC 태그 읽기·쓰기
            Exec="{exec}"
            Icon={icon}
            Terminal=false
            Categories=Utility;
            StartupWMClass=NfcTagger

            """);
        return entry;
    }
}
