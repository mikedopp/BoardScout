using System.Text;
using System.Text.RegularExpressions;

namespace BoardScout.Remote;

/// <summary>
/// What BoardScout runs on a remote machine, and how it reads the answers. Linux commands go through sh -c, so
/// they work whatever the login shell is; Windows commands go through PowerShell's -EncodedCommand, so nothing
/// in them needs quoting for cmd or PowerShell, whichever OpenSSH's default shell is.
/// </summary>
internal static class RemoteCommands
{
    /// <summary>
    /// Asks a machine what it is, in a way every login shell answers: sh, bash, zsh, and fish expand $(…) and
    /// leave %OS% alone; cmd expands %OS% and fails at uname. Windows PowerShell 5.1 can't parse it at all;
    /// <see cref="ProbePowerShell"/> asks again in its own words.
    /// </summary>
    public const string Probe = "echo BSP1 %OS% %PROCESSOR_ARCHITECTURE% && uname -sm";

    public const string ProbePowerShell = "echo BSP2 $env:OS $env:PROCESSOR_ARCHITECTURE";

    /// <summary>Reads a probe's answer. Null means it didn't say (try <see cref="ProbePowerShell"/> next).</summary>
    public static RemoteHost? ParseProbe(string output)
    {
        var tokens = output.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var windows = Array.FindIndex(tokens, t => t.Equals("Windows_NT", StringComparison.OrdinalIgnoreCase));
        if (windows >= 0)
        {
            var arch = windows + 1 < tokens.Length ? tokens[windows + 1].ToUpperInvariant() : "";
            return arch switch
            {
                "AMD64" => new RemoteHost { Os = "windows", Machine = arch, Rid = "win-x64" },
                "ARM64" => new RemoteHost { Os = "windows", Machine = arch, Rid = "win-x64", Note = "Windows on Arm runs BoardScout's x64 build through its x64 emulation." },
                _ => new RemoteHost { Os = "windows", Machine = arch, Unsupported = $"BoardScout needs 64-bit Windows; this one reports {(arch.Length > 0 ? arch : "an unknown processor")}." }
            };
        }
        // uname -sm: "Linux x86_64", "Darwin arm64", "FreeBSD amd64".
        for (var i = 0; i + 1 < tokens.Length; i++)
        {
            if (tokens[i] == "Linux") return Linux(tokens[i + 1], musl: false);
            if (tokens[i] is "Darwin" or "FreeBSD" or "OpenBSD" or "NetBSD" or "SunOS")
                return new RemoteHost { Os = tokens[i].ToLowerInvariant(), Machine = tokens[i + 1], Unsupported = $"BoardScout runs on Linux and Windows; this machine runs {(tokens[i] == "Darwin" ? "macOS" : tokens[i])}." };
        }
        return null;
    }

    /// <summary>Which Linux build runs on a processor (uname -m) with glibc or musl (Alpine).</summary>
    public static RemoteHost Linux(string machine, bool musl)
    {
        var rid = machine switch
        {
            "x86_64" or "amd64" => musl ? "linux-musl-x64" : "linux-x64",
            "aarch64" or "arm64" or "armv8l" => musl ? "linux-musl-arm64" : "linux-arm64",
            "armv7l" or "armv7" or "armhf" => musl ? null : "linux-arm",
            _ => null
        };
        var why = rid is not null ? null : machine switch
        {
            "armv6l" or "armv5tel" => "This is a 32-bit ARMv6 board (Raspberry Pi 1 or Zero). .NET, which BoardScout is built on, needs ARMv7 or newer.",
            "i686" or "i386" or "i586" => "This is 32-bit x86 Linux. BoardScout needs a 64-bit system.",
            "armv7l" or "armv7" or "armhf" => "32-bit ARM with musl (Alpine) has no .NET build.",
            _ => $"There's no BoardScout build for {machine} processors."
        };
        return new RemoteHost { Os = "linux", Machine = machine, Rid = rid, Unsupported = why };
    }

    // ---- Linux ------------------------------------------------------------------------------------------

    /// <summary>The whole script as one argument to sh -c, single-quoted (a quote inside becomes '\'').</summary>
    public static string Sh(string script) => "sh -c " + ShQuote(script);

    public static string ShQuote(string text) => "'" + text.Replace("'", "'\\''") + "'";

    /// <summary>The folder's shell expression: $HOME/.cache/boardscout by default.</summary>
    public static string LinuxFolder(string? custom)
    {
        if (string.IsNullOrWhiteSpace(custom)) return "$HOME/.cache/boardscout";
        var folder = custom.Trim().TrimEnd('/');
        if (!SafeFolder(folder)) throw new RemoteException("other", "The remote folder may use letters, digits, spaces, and . _ - / ~ only.");
        return folder.StartsWith("~/", StringComparison.Ordinal) ? "$HOME/" + folder[2..] : folder;
    }

    public static bool SafeFolder(string folder) => Regex.IsMatch(folder, @"^[A-Za-z0-9._ /~:\\-]+$") && !folder.Contains("..");

    /// <summary>Reads the processor, C library, distribution, and any copies of this version already there.</summary>
    public static string LinuxCheck(string folder, string version) => Sh(
        $"B=\"{folder}\"; mkdir -p \"$B/agent\" \"$B/data\" && chmod 700 \"$B\" \"$B/agent\" \"$B/data\"; " +
        "echo \"ARCH=$(uname -m)\"; " +
        "if (ldd --version 2>&1 | grep -qi musl) || ls /lib/ld-musl-* >/dev/null 2>&1; then echo LIBC=musl; else echo LIBC=glibc; fi; " +
        "if [ -r /etc/os-release ]; then . /etc/os-release; echo \"PRETTY=$PRETTY_NAME\"; fi; " +
        $"for f in \"$B\"/agent/boardscout-{version}-*; do [ -f \"$f\" ] || continue; " +
        "h=$( (sha256sum \"$f\" 2>/dev/null || shasum -a 256 \"$f\" 2>/dev/null || openssl dgst -sha256 -r \"$f\" 2>/dev/null) | cut -d' ' -f1); " +
        "echo \"FILE=$(basename \"$f\") $h\"; done");

    /// <summary>Receives the build on stdin, checks it arrived whole, and replaces older versions.</summary>
    public static string LinuxReceive(string folder, string name) => Sh(
        $"set -e; B=\"{folder}\"; F=\"$B/agent/{name}\"; umask 077; cat > \"$F.part\"; chmod 700 \"$F.part\"; " +
        "echo \"HASH=$( (sha256sum \"$F.part\" 2>/dev/null || shasum -a 256 \"$F.part\" 2>/dev/null || openssl dgst -sha256 -r \"$F.part\") | cut -d' ' -f1)\"; " +
        $"mv -f \"$F.part\" \"$F\"; find \"$B/agent\" -maxdepth 1 -name 'boardscout-*' ! -name '{name}' -exec rm -f {{}} +; rm -rf \"$B/extract\"");

    /// <summary>Runs the build there: as a dashboard (agent) or once, printing a summary.</summary>
    public static string LinuxRun(string folder, string name, string arguments) => Sh(
        $"B=\"{folder}\"; export DOTNET_BUNDLE_EXTRACT_BASE_DIR=\"$B/extract\"; exec \"$B/agent/{name}\" --data \"$B/data\" {arguments}");

    // ---- Windows ----------------------------------------------------------------------------------------

    /// <summary>A PowerShell script as one plain argument: base64 of its UTF-16 text.</summary>
    public static string PowerShell(string script) =>
        "powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    public static string PsQuote(string text) => "'" + text.Replace("'", "''") + "'";

    /// <summary>The folder's PowerShell expression: %LOCALAPPDATA%\BoardScout\Remote by default.</summary>
    public static string WindowsFolder(string? custom)
    {
        if (string.IsNullOrWhiteSpace(custom)) return "(Join-Path $env:LOCALAPPDATA 'BoardScout\\Remote')";
        var folder = custom.Trim().TrimEnd('\\', '/');
        if (!SafeFolder(folder)) throw new RemoteException("other", "The remote folder may use letters, digits, spaces, and . _ - \\ / : only.");
        return PsQuote(folder);
    }

    public static string WindowsCheck(string folder, string version) => PowerShell(
        "$ErrorActionPreference='SilentlyContinue'; $ProgressPreference='SilentlyContinue'\n" +
        $"$b = {folder}\n" +
        "New-Item -ItemType Directory -Force -Path (Join-Path $b 'agent'), (Join-Path $b 'data') | Out-Null\n" +
        "\"FOLDER=$((Resolve-Path $b).Path)\"\n" +
        "\"PROFILE=$env:USERPROFILE\"\n" +
        "\"ARCH=$env:PROCESSOR_ARCHITECTURE\"\n" +
        "$v = Get-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion'\n" +
        "\"PRETTY=$($v.ProductName)|$($v.DisplayVersion)|$($v.CurrentBuild)|$($v.EditionID)\"\n" +
        "\"ADMIN=$(([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))\"\n" +
        $"Get-ChildItem -Path (Join-Path $b 'agent') -Filter 'BoardScout-{version}-*' -File | ForEach-Object {{ \"FILE=$($_.Name) $((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())\" }}");

    /// <summary>After scp: checks the copy arrived whole, then replaces older versions.</summary>
    public static string WindowsFinish(string folder, string name) => PowerShell(
        "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'\n" +
        $"$b = {folder}; $a = Join-Path $b 'agent'; $f = Join-Path $a {PsQuote(name)}\n" +
        "\"HASH=$((Get-FileHash -LiteralPath \"$f.part\" -Algorithm SHA256).Hash.ToLowerInvariant())\"\n" +
        "Move-Item -LiteralPath \"$f.part\" -Destination $f -Force\n" +
        $"Get-ChildItem -Path $a -Filter 'BoardScout-*' -File | Where-Object Name -ne {PsQuote(name)} | Remove-Item -Force -ErrorAction SilentlyContinue\n" +
        "Remove-Item -LiteralPath (Join-Path $b 'extract') -Recurse -Force -ErrorAction SilentlyContinue");

    /// <summary>
    /// Runs the build there. BoardScout.exe is a Windows (not console) program, so PowerShell is told to read
    /// its output through a pipe: that makes it wait for the program and pass each line on as it comes.
    /// </summary>
    public static string WindowsRun(string folder, string name, string arguments) => PowerShell(
        "$ProgressPreference='SilentlyContinue'\n" +
        $"$b = {folder}\n" +
        "$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $b 'extract'\n" +
        $"& (Join-Path (Join-Path $b 'agent') {PsQuote(name)}) --data (Join-Path $b 'data') {arguments} | ForEach-Object {{ $_ }}\n" +
        "exit $LASTEXITCODE");

    /// <summary>The scp destination for a file in the remote folder: relative to the home folder when it's inside it.</summary>
    public static string ScpPath(string folder, string profile, string file)
    {
        var full = folder.TrimEnd('\\') + "\\" + file;
        if (profile.Length > 0 && full.StartsWith(profile.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            return full[(profile.TrimEnd('\\').Length + 1)..].Replace('\\', '/');
        return "/" + full.Replace('\\', '/');
    }

    /// <summary>Arguments are passed on as typed; only the label needs quoting, and it's limited to safe characters.</summary>
    public static string Label(string name)
    {
        var clean = new string(name.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '.' or '-' or '_' or '@').ToArray()).Trim();
        return clean.Length == 0 ? "remote" : clean.Length > 60 ? clean[..60] : clean;
    }

    /// <summary>KEY=value lines from a check script.</summary>
    public static Dictionary<string, string> Values(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var equals = line.IndexOf('=');
            if (equals <= 0 || line.StartsWith("FILE=", StringComparison.Ordinal)) continue;
            values.TryAdd(line[..equals], line[(equals + 1)..].Trim());
        }
        return values;
    }

    /// <summary>FILE=name hash lines: the copies of this version already on the machine.</summary>
    public static Dictionary<string, string> Files(string output)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("FILE=", StringComparison.Ordinal)) continue;
            var parts = line[5..].Split(' ', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[1].Length == 64) files[parts[0]] = parts[1].ToLowerInvariant();
        }
        return files;
    }

    /// <summary>"Windows 11 Pro 24H2": the registry still says "Windows 10" on 11, so the build number decides.</summary>
    public static string WindowsName(string pretty)
    {
        var parts = pretty.Split('|');
        if (parts.Length < 3) return pretty;
        var name = parts[0].Trim();
        if (int.TryParse(parts[2], out var build) && build >= 22000) name = name.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
        return $"{name} {parts[1].Trim()}".Trim();
    }
}
