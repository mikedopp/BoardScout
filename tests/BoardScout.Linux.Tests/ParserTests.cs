using System.Net;
using BoardScout.Live;
using BoardScout.Platform;
using BoardScout.Views;
using Xunit;

namespace BoardScout.Linux.Tests;

// The kernel's text formats, as Linux prints them (samples from a Dell OptiPlex 3050 on Ubuntu 26.04 unless noted).
public class ParserTests
{
    [Theory]
    [InlineData("2.5 GT/s PCIe", 2.5, 1)]
    [InlineData("8.0 GT/s PCIe", 8.0, 3)]
    [InlineData("16.0 GT/s PCIe", 16.0, 4)]
    [InlineData("32.0 GT/s PCIe", 32.0, 5)]
    public void Pcie_link_speeds(string text, double gigatransfers, int generation)
    {
        Assert.Equal(gigatransfers, PciLinkInfo.ParseSpeed(text));
        Assert.Equal(generation, PciLinkInfo.GenOf(gigatransfers));
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("")]
    [InlineData(null)]
    public void Pcie_link_speed_unknown(string? text) => Assert.Null(PciLinkInfo.ParseSpeed(text));

    [Fact]
    public void Pcie_link_below_max()
    {
        Assert.True(new PciLinkInfo(8, 2, 8, 4).BelowMax);
        Assert.True(new PciLinkInfo(8, 4, 16, 4).BelowMax);
        Assert.False(new PciLinkInfo(8, 4, 8, 4).BelowMax);
    }

    [Theory]
    [InlineData("1-5.2.3", "1-5.2")]
    [InlineData("1-5", "usb1")]
    [InlineData("3-1", "usb3")]
    public void Usb_parent_names(string name, string parent) => Assert.Equal(parent, Usb.ParentName(name));

    [Theory]
    [InlineData("500mA", 500)]
    [InlineData("0mA", 0)]
    [InlineData("896mA", 896)]
    [InlineData(null, 0)]
    public void Usb_power(string? text, int milliamps) => Assert.Equal(milliamps, Usb.ParseMilliamps(text));

    [Theory]
    [InlineData(480, "480 Mbps")]
    [InlineData(5000, "5 Gbps")]
    [InlineData(10000, "10 Gbps")]
    [InlineData(20000, "20 Gbps")]
    [InlineData(12, "12 Mbps")]
    public void Usb_speed_names(double mbps, string shortName) => Assert.Equal(shortName, Usb.SpeedName(mbps).Short);

    [Theory]
    [InlineData("WDC WD10", "JPVT-75A1YT0", "WDC WD10JPVT-75A1YT0")]
    [InlineData("ATA     ", "ST500LT012-9WS14", "ST500LT012-9WS14")]
    [InlineData("Samsung", "Portable SSD T5", "Samsung Portable SSD T5")]
    [InlineData("", "SAMSUNG MZVPW256HEGL-000H1", "SAMSUNG MZVPW256HEGL-000H1")]
    [InlineData("Kingston", "Kingston DataTraveler", "Kingston DataTraveler")]
    public void Scsi_split_models(string vendor, string model, string joined) => Assert.Equal(joined, Block.JoinModel(vendor, model));

    [Fact]
    public void Mountinfo_lines()
    {
        var root = Block.ParseMountInfo("29 1 252:0 / / rw,relatime shared:1 - ext4 /dev/mapper/ubuntu--vg-ubuntu--lv rw");
        Assert.Equal(("252:0", "/", "ext4", "/dev/mapper/ubuntu--vg-ubuntu--lv", false), root);
        var spaced = Block.ParseMountInfo(@"120 29 8:33 / /media/me/My\040Passport ro,nosuid shared:70 - exfat /dev/sdc1 ro");
        Assert.Equal("/media/me/My Passport", spaced!.Value.MountPoint);
        Assert.True(spaced.Value.ReadOnly);
        Assert.Null(Block.ParseMountInfo("garbage"));
    }

    [Fact]
    public void Diskstats_line()
    {
        var entry = Block.ParseDiskStats(" 259       0 nvme0n1 93191 109547 4600847 39259 740254 2236299 24153175 1148264 0 288388 1291217 1109 0 362600 1403 26952 102290");
        Assert.NotNull(entry);
        Assert.Equal("nvme0n1", entry!.Value.Name);
        Assert.Equal(new DiskCounters(93191, 4600847, 740254, 24153175, 0, 288388), entry.Value.Counters);
    }

    [Fact]
    public void Default_route()
    {
        var route = NetLinks.ParseRoute("br0\t00000000\t0101A8C0\t0003\t0\t0\t0\t00000000\t0\t0\t0");
        Assert.NotNull(route);
        Assert.Equal("br0", route!.Interface);
        Assert.Equal(IPAddress.Parse("192.168.1.1"), route.Gateway);
        Assert.Null(NetLinks.ParseRoute("br0\t0001A8C0\t00000000\t0001\t0\t0\t0\t00FFFFFF\t0\t0\t0"));
    }

    [Fact]
    public void Net_dev_line()
    {
        var entry = NetLinks.ParseNetDev("enp3s0: 6374058051 4908998    0 62276    0     0          0     53256 1709514422  845052    0    0    0     0       0          0");
        Assert.NotNull(entry);
        Assert.Equal("enp3s0", entry!.Value.Name);
        Assert.Equal(6374058051, entry.Value.Counters.BytesIn);
        Assert.Equal(62276, entry.Value.Counters.DroppedIn);
        Assert.Equal(1709514422, entry.Value.Counters.BytesOut);
    }

    [Fact]
    public void Iw_link_output()
    {
        const string text = """
            Connected to aa:bb:cc:dd:ee:ff (on wlp4s0)
            	SSID: Home
            	freq: 5180.0
            	RX: 1234 bytes (12 packets)
            	signal: -52 dBm
            	rx bitrate: 866.7 MBit/s VHT-MCS 9 80MHz short GI VHT-NSS 2
            	tx bitrate: 780.0 MBit/s VHT-MCS 8 80MHz short GI VHT-NSS 2
            """;
        var link = NetLinks.ParseIwLink("wlp4s0", text);
        Assert.True(link.Connected);
        Assert.Equal("Home", link.Ssid);
        Assert.Equal("AA:BB:CC:DD:EE:FF", link.Bssid);
        Assert.Equal(-52, link.SignalDbm);
        Assert.Equal(5180, link.FrequencyMhz);
        Assert.Equal(36, link.Channel);
        Assert.Equal(866.7, link.RxMbps);
        Assert.Equal("Wi-Fi 5 (802.11ac)", link.Standard);
        Assert.False(NetLinks.ParseIwLink("wlp4s0", "Not connected.").Connected);
    }

    [Fact]
    public void Nmcli_escaped_fields() =>
        Assert.Equal(["*", "AA:BB:CC:DD:EE:FF", "Home: 5G", "5180 MHz"], NetLinks.SplitNmcli(@"*:AA\:BB\:CC\:DD\:EE\:FF:Home\: 5G:5180 MHz"));

    [Theory]
    [InlineData(2412, 1)]
    [InlineData(2437, 6)]
    [InlineData(5745, 149)]
    [InlineData(5955, 1)]
    public void Wifi_channels(double mhz, int channel) => Assert.Equal(channel, NetLinks.ChannelOf(mhz));

    [Fact]
    public void Proc_stat_usage()
    {
        var before = Cpu.ParseStat("cpu  394059 1814 164341 10142135 73022 0 4309 0 246546 0")!.Value.Times;
        var after = Cpu.ParseStat("cpu  394159 1814 164441 10142935 73022 0 4309 0 246546 0")!.Value.Times;
        Assert.Equal(20, Cpu.Usage(before, after), 3);
    }

    [Fact]
    public void Proc_interrupts()
    {
        var lines = Interrupts.Parse([
            "            CPU0       CPU1       CPU2       CPU3       ",
            "   0:         61          0          0          0 IR-IO-APIC    2-edge      timer",
            "  16:          0          0         10          0 IR-IO-APIC   16-fasteoi   i801_smbus, ehci_hcd:usb1",
            " 133:          0      98876          0          0 IR-PCI-MSIX-0000:02:00.0    1-edge      nvme0q1",
            " LOC:    1236584    1203411    1198871    1213901   Local timer interrupts",
            " ERR:          0"
        ]);
        Assert.Equal(5, lines.Count);
        Assert.Equal(["i801_smbus", "ehci_hcd:usb1"], lines[1].Handlers);
        Assert.False(lines[1].MessageSignaled);
        Assert.Equal("0000:02:00.0", lines[2].PciAddress);
        Assert.True(lines[2].MessageSignaled);
        Assert.Equal(98876, lines[2].Total);
        Assert.False(lines[3].Numbered);
        Assert.Equal("Local timer interrupts", lines[3].Description);
    }

    [Fact]
    public void Edid_name_and_preferred_mode()
    {
        var edid = new byte[128];
        byte[] header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
        header.CopyTo(edid, 0);
        // "DEL": D=4, E=5, L=12 → 0b0_00100_00101_01100.
        var id = (4 << 10) | (5 << 5) | 12;
        edid[8] = (byte)(id >> 8);
        edid[9] = (byte)id;
        edid[17] = 30; // 2020
        edid[21] = 60;
        edid[22] = 34;
        // Preferred timing: 533.25 MHz, 3840 + 160 by 2160 + 62.
        byte[] timing = [0x4D, 0xD0, 0x00, 0xA0, 0xF0, 0x70, 0x3E, 0x80];
        timing.CopyTo(edid, 54);
        byte[] name = [0, 0, 0, 0xFC, 0, .. "DELL U2720Q\n "u8.ToArray()];
        name.CopyTo(edid, 72);
        var parsed = Displays.ParseEdid(edid);
        Assert.NotNull(parsed);
        Assert.Equal("Dell", parsed!.Maker);
        Assert.Equal("DELL U2720Q", parsed.Name);
        Assert.Equal(2020, parsed.Year);
        Assert.Equal(3840, parsed.Width);
        Assert.Equal(2160, parsed.Height);
        Assert.Equal(60, parsed.RefreshHz!.Value, 0);
        Assert.Equal(27, parsed.DiagonalInches);
    }

    [Theory]
    [InlineData("HDMI-A-1", "HDMI")]
    [InlineData("DP-3", "DisplayPort")]
    [InlineData("eDP-1", "eDP (built-in screen)")]
    [InlineData("DVI-D-1", "DVI")]
    public void Connector_names(string connector, string kind) => Assert.Equal(kind, Displays.ConnectorKind(connector));

    [Theory]
    [InlineData("802C0000802C", "Micron")]
    [InlineData("80CE000080CE", "Samsung")]
    [InlineData("80AD", "SK hynix")]
    [InlineData("859B", "Crucial")]
    [InlineData("0198", "Kingston")]
    [InlineData("Kingston", "Kingston")]
    [InlineData("Not Specified", null)]
    public void Memory_makers(string code, string? maker) => Assert.Equal(maker, Firmware.JedecMaker(code == "Not Specified" ? null : code));

    [Fact]
    public void Memory_slots_from_udev()
    {
        var properties = new Dictionary<string, string>
        {
            ["MEMORY_ARRAY_NUM_DEVICES"] = "2",
            ["MEMORY_DEVICE_0_SIZE"] = "8589934592",
            ["MEMORY_DEVICE_0_LOCATOR"] = "DIMM1",
            ["MEMORY_DEVICE_0_BANK_LOCATOR"] = "Not Specified",
            ["MEMORY_DEVICE_0_TYPE"] = "DDR4",
            ["MEMORY_DEVICE_0_SPEED_MTS"] = "2400",
            ["MEMORY_DEVICE_0_CONFIGURED_SPEED_MTS"] = "2400",
            ["MEMORY_DEVICE_0_RANK"] = "1",
            ["MEMORY_DEVICE_0_PART_NUMBER"] = "8ATF1G64HZ-2G3B1",
            ["MEMORY_DEVICE_0_MANUFACTURER"] = "802C0000802C",
            ["MEMORY_DEVICE_0_SERIAL_NUMBER"] = "00000000",
            ["MEMORY_DEVICE_1_PRESENT"] = "0",
            ["MEMORY_DEVICE_1_LOCATOR"] = "DIMM2",
            ["MEMORY_DEVICE_1_TYPE"] = "Unknown"
        };
        var identity = new MachineIdentity("Dell Inc.", "OptiPlex 3050", null, "Dell Inc.", "0JP3NX", "A00", "Dell Inc.", "1.2.0", "12/22/2016", 35);
        var memory = Firmware.FromUdev(properties, identity);
        Assert.Equal(2, memory.Firmware.SlotCount);
        Assert.Equal(2, memory.Firmware.MemorySlots.Count);
        var stick = memory.Firmware.MemorySlots[0];
        Assert.Equal(("DIMM1", "", 8192, 2400, "DDR4", "8ATF1G64HZ-2G3B1"), (stick.DeviceLocator, stick.BankLocator, stick.SizeMb, stick.ConfiguredMts, stick.MemoryType, stick.PartNumber));
        Assert.False(memory.Firmware.MemorySlots[1].Populated);
        Assert.Equal("Micron", memory.Makers["DIMM1|"]);
        Assert.Equal("Mini PC", memory.Firmware.FormFactor);
    }

    [Fact]
    public void Ata_identify_reads_speed_firmware_and_rpm_only()
    {
        var words = new string[256];
        for (var i = 0; i < words.Length; i++) words[i] = "0000";
        // Serial number words 10-19 hold text that must never come back out.
        for (var i = 10; i < 20; i++) words[i] = "5345";
        // "0001LVM1" in words 23-26, two characters per word.
        words[23] = "3030"; words[24] = "3031"; words[25] = "4c56"; words[26] = "4d31";
        words[76] = "0f06";   // SATA 1.5 and 3 Gbps
        words[217] = "1518";  // 5400 RPM
        var identity = KernelInfo.ParseIdentify(string.Join(' ', words));
        Assert.NotNull(identity);
        Assert.Equal(3, identity!.MaxGbps);
        Assert.Equal("0001LVM1", identity.Firmware);
        Assert.Equal(5400, identity.Rpm);
        Assert.False(identity.SolidState);
        words[76] = "070e"; words[217] = "0001";
        var ssd = KernelInfo.ParseIdentify(string.Join(' ', words));
        Assert.Equal(6, ssd!.MaxGbps);
        Assert.True(ssd.SolidState);
        Assert.Null(KernelInfo.ParseIdentify("0000 0000"));
    }

    [Fact]
    public void Kernel_versions_order_numerically()
    {
        var comparer = LinuxPlan.KernelVersionComparer.Instance;
        Assert.True(comparer.Compare("7.0.0-38-generic", "7.0.0-9-generic") > 0);
        Assert.True(comparer.Compare("6.14.0-1-generic", "7.0.0-1-generic") < 0);
        Assert.Equal(0, comparer.Compare("7.0.0-38-generic", "7.0.0-38-generic"));
    }

    [Fact]
    public void Fwupd_updates()
    {
        const string json = """
            {"Devices":[{"Name":"System Firmware","Vendor":"Dell","Version":"1.2.0","Flags":["updatable"],
              "Releases":[{"Version":"1.27.0","Summary":"Firmware for the Dell OptiPlex 3050","Urgency":"high","DetailsUrl":"https://fwupd.org/lvfs/devices/x"}]}]}
            """;
        var update = Assert.Single(FirmwareUpdates.ParseUpdates(json));
        Assert.Equal(("System Firmware", "1.2.0", "1.27.0", "high"), (update.Device, update.Current, update.Latest, update.Urgency));
        var device = Assert.Single(FirmwareUpdates.ParseDevices(json));
        Assert.True(device.Updatable);
        Assert.Empty(FirmwareUpdates.ParseUpdates("No updates available"));
    }

    [Theory]
    [InlineData("Dell Inc.", "Dell")]
    [InlineData("ASUSTeK COMPUTER INC.", "ASUS")]
    [InlineData("LENOVO", "Lenovo")]
    [InlineData("Micro-Star International Co., Ltd.", "MSI")]
    [InlineData("ASRock", "ASRock")]
    public void Company_names(string raw, string clean) => Assert.Equal(clean, Format.Company(raw));

    [Theory]
    [InlineData("Intel Corporation", "Intel")]
    [InlineData("Advanced Micro Devices, Inc. [AMD/ATI]", "AMD")]
    [InlineData("Realtek Semiconductor Co., Ltd.", "Realtek")]
    [InlineData("Samsung Electronics Co Ltd", "Samsung")]
    public void Pci_vendor_names(string raw, string clean) => Assert.Equal(clean, PciIds.CleanVendor(raw));

    [Fact]
    public void Pci_ids_lookup()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pci-{Guid.NewGuid():N}.ids");
        File.WriteAllText(path, """
            # sample
            10de  NVIDIA Corporation
            	2507  GA106 [GeForce RTX 3050]
            		1458 4085  Gigabyte GeForce RTX 3050 EAGLE
            8086  Intel Corporation
            	5912  HD Graphics 630
            C 00  Unclassified device
            """);
        try
        {
            var names = PciIds.Resolve([(0x10de, 0x2507, 0x1458, 0x4085), (0x8086, 0x5912, 0x1028, 0x07a1)], path);
            Assert.Equal("GA106 [GeForce RTX 3050]", names[(0x10de, 0x2507, 0x1458, 0x4085)].Device);
            Assert.Equal("Gigabyte GeForce RTX 3050 EAGLE", names[(0x10de, 0x2507, 0x1458, 0x4085)].Subsystem);
            Assert.Equal("HD Graphics 630", names[(0x8086, 0x5912, 0x1028, 0x07a1)].Device);
            Assert.Equal("GeForce RTX 3050", PciIds.CleanDevice("GA106 [GeForce RTX 3050]"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Launch_options()
    {
        var lan = LaunchOptions.Parse(["--listen", "0.0.0.0:9000"]);
        Assert.Equal(("0.0.0.0", 9000, false), (lan.Listen, lan.Port, lan.OpenBrowser));
        Assert.Equal("127.0.0.1", LaunchOptions.Parse(["--listen", "localhost"]).Listen);
        Assert.NotNull(LaunchOptions.Parse(["--listen", "not-an-address"]).Error);
        Assert.NotNull(LaunchOptions.Parse(["--port", "80"]).Error);
        Assert.Equal("connections-json", LaunchOptions.Parse(["--connections-json", "--privacy"]).Command);
        Assert.NotNull(LaunchOptions.Parse(["--bogus"]).Error);
    }
}
