using System.Text;
using BoardScout.Models;
using BoardScout.Remote;
using Xunit;

namespace BoardScout.Tests;

// The Machines page's plumbing: reading what a machine is, choosing its build, quoting commands for its shell,
// parsing what it answers, and turning SSH's complaints into something to say or fix.
public class RemoteTests
{
    // ---- What the machine is ---------------------------------------------------------------------------

    [Fact]
    public void Probe_reads_Windows_from_cmd() // cmd expands %OS%; uname fails on stderr
    {
        var host = RemoteCommands.ParseProbe("BSP1 Windows_NT AMD64 \r\n");
        Assert.Equal("windows", host!.Os);
        Assert.Equal("win-x64", host.Rid);
        Assert.Null(host.Note);
    }

    [Fact]
    public void Probe_reads_Linux_from_sh() // sh leaves %OS% alone and runs uname
    {
        var host = RemoteCommands.ParseProbe("BSP1 %OS% %PROCESSOR_ARCHITECTURE%\nLinux x86_64\n");
        Assert.Equal("linux", host!.Os);
        Assert.Equal("linux-x64", host.Rid);
    }

    [Fact]
    public void Probe_reads_PowerShell_answer_one_value_per_line()
    {
        var host = RemoteCommands.ParseProbe("BSP2 \r\nWindows_NT\r\nARM64\r\n");
        Assert.Equal("win-x64", host!.Rid); // Windows on Arm runs the x64 build through emulation
        Assert.NotNull(host.Note);
    }

    [Fact]
    public void Probe_knows_macOS_is_not_supported()
    {
        var host = RemoteCommands.ParseProbe("BSP1 %OS% %PROCESSOR_ARCHITECTURE%\nDarwin arm64\n");
        Assert.Null(host!.Rid);
        Assert.Contains("macOS", host.Unsupported);
    }

    [Fact]
    public void Probe_without_an_answer_asks_again() =>
        Assert.Null(RemoteCommands.ParseProbe("The term '&&' is not a valid statement separator in this version."));

    [Theory]
    [InlineData("x86_64", false, "linux-x64")]
    [InlineData("aarch64", false, "linux-arm64")]
    [InlineData("x86_64", true, "linux-musl-x64")]
    [InlineData("aarch64", true, "linux-musl-arm64")]
    [InlineData("armv7l", false, "linux-arm")]
    [InlineData("armv6l", false, null)]
    [InlineData("armv7l", true, null)]
    [InlineData("i686", false, null)]
    [InlineData("riscv64", false, null)]
    public void Linux_build_follows_processor_and_C_library(string machine, bool musl, string? rid)
    {
        var host = RemoteCommands.Linux(machine, musl);
        Assert.Equal(rid, host.Rid);
        if (rid is null) Assert.False(string.IsNullOrEmpty(host.Unsupported));
    }

    // ---- Commands --------------------------------------------------------------------------------------

    [Fact]
    public void Sh_quoting_survives_single_quotes() =>
        Assert.Equal(@"'it'\''s'", RemoteCommands.ShQuote("it's"));

    [Fact]
    public void PowerShell_command_is_the_script_in_base64_utf16()
    {
        var command = RemoteCommands.PowerShell("\"FOLDER=$b\"");
        var encoded = command.Split(' ').Last();
        Assert.StartsWith("powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ", command);
        Assert.Equal("\"FOLDER=$b\"", Encoding.Unicode.GetString(Convert.FromBase64String(encoded)));
    }

    [Fact]
    public void Run_commands_carry_the_label_quoted_for_each_shell()
    {
        Assert.Contains("exec \"$B/agent/boardscout-2.0.0-linux-x64\" --data \"$B/data\" --agent",
            RemoteCommands.LinuxRun("$HOME/.cache/boardscout", "boardscout-2.0.0-linux-x64", "--agent"));
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(
            RemoteCommands.WindowsRun(RemoteCommands.WindowsFolder(null), "BoardScout-2.0.0-win-x64.exe", "--agent --label 'Mike''s PC'").Split(' ').Last()));
        Assert.Contains("--label 'Mike''s PC' | ForEach-Object { $_ }", script);
        Assert.Contains("DOTNET_BUNDLE_EXTRACT_BASE_DIR", script);
    }

    [Theory]
    [InlineData("~/boardscout", "$HOME/boardscout")]
    [InlineData("/srv/tools/boardscout/", "/srv/tools/boardscout")]
    public void Linux_folder_expands_home(string typed, string expected) =>
        Assert.Equal(expected, RemoteCommands.LinuxFolder(typed));

    [Theory]
    [InlineData("/tmp/$(reboot)")]
    [InlineData("/tmp/`id`")]
    [InlineData("/srv/../etc")]
    [InlineData("/tmp/x\"; rm -rf ~")]
    public void Linux_folder_refuses_shell_tricks(string typed) =>
        Assert.Throws<RemoteException>(() => RemoteCommands.LinuxFolder(typed));

    [Fact]
    public void Labels_keep_only_safe_characters() =>
        Assert.Equal("Mikes PC rm -rf", RemoteCommands.Label("Mike's PC; rm -rf `/`"));

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Local\BoardScout\Remote", @"C:\Users\me", "AppData/Local/BoardScout/Remote/agent/x.exe.part")]
    [InlineData(@"D:\Tools\BoardScout", @"C:\Users\me", "/D:/Tools/BoardScout/agent/x.exe.part")]
    public void Scp_path_is_home_relative_when_it_can_be(string folder, string profile, string expected) =>
        Assert.Equal(expected, RemoteCommands.ScpPath(folder, profile, @"agent\x.exe.part"));

    [Theory]
    [InlineData("Windows 10 Pro|24H2|26100|Professional", "Windows 11 Pro 24H2")]
    [InlineData("Windows 10 Pro|22H2|19045|Professional", "Windows 10 Pro 22H2")]
    [InlineData("Windows Server 2022 Datacenter||20348|ServerDatacenter", "Windows Server 2022 Datacenter")]
    public void Windows_name_follows_the_build_number(string pretty, string expected) =>
        Assert.Equal(expected, RemoteCommands.WindowsName(pretty));

    [Fact]
    public void Check_answers_are_read_as_values_and_files()
    {
        var output = "ARCH=x86_64\nLIBC=glibc\nPRETTY=Ubuntu 26.04 LTS\nFILE=boardscout-2.0.0-linux-x64 " + new string('a', 64) + "\nFILE=broken nohash\n";
        var values = RemoteCommands.Values(output);
        Assert.Equal("x86_64", values["ARCH"]);
        Assert.Equal("Ubuntu 26.04 LTS", values["PRETTY"]);
        Assert.False(values.ContainsKey("FILE"));
        var files = RemoteCommands.Files(output);
        Assert.Single(files);
        Assert.Equal(new string('a', 64), files["boardscout-2.0.0-linux-x64"]);
    }

    // ---- Machines and builds -------------------------------------------------------------------------------

    [Theory]
    [InlineData("admin@192.168.1.20", "admin@192.168.1.20", null)]
    [InlineData("ssh dutch", "dutch", null)]
    [InlineData("nas.local:2222", "nas.local", 2222)]
    [InlineData(" root@pi:22 ", "root@pi", 22)]
    public void Typed_addresses_become_targets(string typed, string target, int? port)
    {
        var (parsed, parsedPort, problem) = MachineStore.ParseTarget(typed);
        Assert.Null(problem);
        Assert.Equal(target, parsed);
        Assert.Equal(port, parsedPort);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-oProxyCommand=calc")]
    [InlineData("host; rm -rf /")]
    [InlineData("two words")]
    [InlineData("host:99999")]
    public void Unsafe_or_empty_addresses_are_refused(string typed) =>
        Assert.NotNull(MachineStore.ParseTarget(typed).Problem);

    [Fact]
    public void Build_names_match_the_release()
    {
        Assert.Equal("BoardScout-2.0.0-linux-musl-x64", AgentBuilds.FileName("linux-musl-x64", "2.0.0"));
        Assert.Equal("BoardScout-2.0.0-win-x64.exe", AgentBuilds.FileName("win-x64", "2.0.0"));
        Assert.Equal("boardscout-2.0.0-linux-arm", AgentBuilds.RemoteName("linux-arm", "2.0.0"));
        Assert.Equal("BoardScout-2.0.0-linux-SHA256SUMS.txt", AgentBuilds.SumsName("linux-arm64", "2.0.0"));
        Assert.Equal("BoardScout-2.0.0-SHA256SUMS.txt", AgentBuilds.SumsName("win-x64", "2.0.0"));
    }

    [Fact]
    public void Checksums_are_found_by_exact_name()
    {
        var a = new string('a', 64);
        var b = new string('B', 64);
        var sums = $"{a}  BoardScout-2.0.0-linux-x64.tar.gz\r\n{b} *BoardScout-2.0.0-linux-x64\r\n";
        Assert.Equal(b.ToLowerInvariant(), AgentBuilds.ExpectedHash(sums, "BoardScout-2.0.0-linux-x64"));
        Assert.Equal(a, AgentBuilds.ExpectedHash(sums, "BoardScout-2.0.0-linux-x64.tar.gz"));
        Assert.Null(AgentBuilds.ExpectedHash(sums, "BoardScout-2.0.0-linux-arm64"));
    }

    // ---- What SSH said ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("No ED25519 host key is known for 10.0.0.5 and you have requested strict checking.\r\nHost key verification failed.", "hostkey")]
    [InlineData("@@@@@@@\r\n@    WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!     @\r\nHost key verification failed.", "hostkey-changed")]
    [InlineData("bald@10.0.0.5: Permission denied (publickey,password).", "auth")]
    [InlineData("ssh: connect to host 10.0.0.9 port 22: Connection timed out", "unreachable")]
    [InlineData("ssh: connect to host 10.0.0.9 port 22: Connection refused", "unreachable")]
    [InlineData("ssh: Could not resolve hostname nas: No such host is known.", "unreachable")]
    [InlineData("kex_exchange_identification: something unusual", "other")]
    public void Ssh_failures_are_sorted(string error, string kind) =>
        Assert.Equal(kind, SshTool.Explain(new SshResult(255, "", error, false), "Connecting").Kind);

    [Fact]
    public void Refused_key_path_comes_back_whole()
    {
        var error = "Bad permissions. Try removing permissions for user: UNKNOWN\\\\UNKNOWN (S-1-5-21-1) on file C:/Users/me/.ssh/id.\r\n" +
                    "Load key \"C:\\\\Users\\\\me\\\\.ssh\\\\id\": bad permissions\r\nme@host: Permission denied (publickey).";
        var problem = SshTool.Explain(new SshResult(255, "", error, false), "Connecting");
        Assert.Equal("key-permissions", problem.Kind);
        Assert.Equal(@"C:\Users\me\.ssh\id", problem.Detail);
    }

    [Fact]
    public void Timeout_is_its_own_story() =>
        Assert.Equal("unreachable", SshTool.Explain(new SshResult(-1, "", "", true), "Copying BoardScout").Kind);

    // ---- The summary row ----------------------------------------------------------------------------------

    [Fact]
    public void Summary_counts_what_needs_attention()
    {
        var system = new SystemInfoSnapshot
        {
            Os = new OsAnalysisInfo { Caption = "Ubuntu 26.04 LTS", SupportStatus = "Supported", SupportTone = "good", PatchHealth = "2 updates waiting", PatchTone = "info" },
            Readiness = new UpgradeReadiness { Checks = [new() { Label = "Firewall", State = "warn" }, new() { Label = "TPM", State = "pass" }, new() { Label = "Secure Boot", State = "fail" }] },
            Build = new BuildSummary { Cpu = "Intel Core i5-7500T", Memory = "8 GB" }
        };
        var slow = new ConnectionNode { Id = "d1", Kind = "storage", Link = new ConnectionLink { Degraded = true } };
        var broken = new ConnectionNode { Id = "d2", Kind = "device", Problem = true };
        var map = new ConnectionsSnapshot { Root = new ConnectionNode { Id = "cpu", Kind = "cpu", Children = [new ConnectionNode { Id = "chipset", Kind = "chipset", Children = [slow, broken] }] } };
        var plan = new OptimizationPlan
        {
            Sections =
            [
                new PlanSection { Items = [new PlanItem { Tone = "improve", Title = "Move the drive" }, new PlanItem { Tone = "good", Title = "Fine" }] },
                new PlanSection { Items = [new PlanItem { Tone = "warn", Title = "Disk almost full" }] }
            ]
        };
        var summary = MachineSummaries.Build("linux", "Dell OptiPlex 3050", system, map, plan,
            [("/", 100, 40), ("/data", 100, 5)], 44.6, false, 1200, privacy: true);

        Assert.Equal(["Firewall", "Secure Boot"], summary.Checks.Select(c => c.Label));
        Assert.Equal(["Disk almost full", "Move the drive"], summary.Fixes); // warnings first
        Assert.Equal(1, summary.SlowLinks);
        Assert.Equal(1, summary.Problems);
        Assert.Equal("/data", summary.Fullest);
        Assert.Equal(95, summary.FullestPercent);
        Assert.Equal(45, summary.CpuTemp);
        Assert.Equal(Services.Privacy.HostPlaceholder, summary.Hostname);
    }

    [Fact]
    public void Summary_json_is_plain_ascii()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new MachineSummary { Memory = "8 GB · 2400 MT/s", CpuTemp = 44 }, MachineSummaries.Ascii);
        Assert.All(json, c => Assert.True(c < 128));
        Assert.Contains("\\u00B7", json);
    }
}
