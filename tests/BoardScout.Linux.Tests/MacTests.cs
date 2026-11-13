using System.Net;
using System.Text.Json;
using BoardScout.Models;
using BoardScout.Platform;
using BoardScout.Platform.Mac;
using BoardScout.Services;
using BoardScout.Views;
using Xunit;

namespace BoardScout.Linux.Tests;

// The macOS edition's parsers, fed made-up output shaped like what Apple's tools print (written from published
// samples; no serial numbers or real addresses). Nothing here has run on a Mac.
public class MacTests
{
    private const string Sysctl = """
        hw.ncpu: 8
        hw.byteorder: 1234
        hw.memsize: 17179869184
        hw.optional.arm64: 1
        hw.perflevel0.physicalcpu: 4
        hw.perflevel0.name: Performance
        hw.perflevel1.physicalcpu: 4
        hw.perflevel1.name: Efficiency
        hw.physicalcpu: 8
        hw.logicalcpu: 8
        hw.packages: 1
        hw.cputype: 16777228
        machdep.cpu.brand_string: Apple M2
        kern.osrelease: 24.5.0
        kern.osproductversion: 15.5
        kern.boottime: { sec = 1731400000, usec = 0 } Tue Nov 12 08:26:40 2024
        vm.swapusage: total = 2048.00M  used = 512.00M  free = 1536.00M  (encrypted)
        """;

    private const string Profiler = """
        {
          "SPHardwareDataType": [{ "_name": "hardware_overview", "boot_rom_version": "11881.121.1", "chip_type": "Apple M2",
            "machine_model": "Mac14,2", "machine_name": "MacBook Air", "number_processors": "proc 8:4:4", "physical_memory": "16 GB" }],
          "SPMemoryDataType": [{ "_name": "SPMemoryDataType", "SPMemoryDataType": "16 GB", "dimm_manufacturer": "Hynix", "dimm_type": "LPDDR5" }],
          "SPNVMeDataType": [{ "_name": "Apple SSD Controller", "_items": [{ "_name": "APPLE SSD AP0512Z", "bsd_name": "disk0", "device_revision": "532.120.", "size_in_bytes": 500277790720 }] }],
          "SPUSBHostDataType": [
            { "_name": "USB 3.1 Bus", "Driver": "AppleT8112USBXHCI", "USBKeyHardwareType": "Built-in", "USBKeyLocationID": "0x00000000" },
            { "_name": "USB 3.1 Bus", "Driver": "AppleT8112USBXHCI", "USBKeyLocationID": "0x01000000", "_items": [
              { "_name": "USB2.0 Hub", "USBDeviceKeyLinkSpeed": "480 Mb/s", "USBDeviceKeyVendorID": "0x2109", "USBDeviceKeyProductID": "0x2822",
                "USBDeviceKeyVendorName": "VIA Labs, Inc.", "USBKeyHardwareType": "Removable", "_items": [
                  { "_name": "USB Receiver", "USBDeviceKeyLinkSpeed": "12 Mb/s", "USBDeviceKeyVendorID": "0x046d", "USBDeviceKeyProductID": "0xc52b",
                    "USBDeviceKeyVendorName": "Logitech", "USBDeviceKeyPowerAllocation": "0.5 W (98 mA)", "USBKeyHardwareType": "Removable" } ] },
              { "_name": "Portable SSD", "USBDeviceKeyLinkSpeed": "10 Gb/s", "USBDeviceKeyVendorID": "0x04e8", "USBDeviceKeyProductID": "0x4001",
                "USBDeviceKeyVendorName": "Samsung", "USBDeviceKeyBSDName": "disk4", "USBKeyHardwareType": "Removable" } ] } ],
          "SPDisplaysDataType": [{ "_name": "Apple M2", "sppci_model": "Apple M2", "sppci_cores": "10", "spdisplays_ndrvs": [
            { "_name": "Color LCD", "_spdisplays_pixels": "2560 x 1664", "_spdisplays_resolution": "1280 x 832 @ 60.00Hz", "spdisplays_connection_type": "spdisplays_internal", "spdisplays_main": "spdisplays_yes" },
            { "_name": "LG HDR 4K", "_spdisplays_pixels": "3840 x 2160", "_spdisplays_resolution": "1920 x 1080 @ 60.00Hz", "_spdisplays_display-year": "2021", "spdisplays_online": "spdisplays_yes" } ] }],
          "SPNetworkDataType": [
            { "_name": "Wi-Fi", "hardware": "AirPort", "interface": "en0", "type": "AirPort" },
            { "_name": "USB 10/100/1000 LAN", "hardware": "Ethernet", "interface": "en7", "type": "Ethernet" },
            { "_name": "Thunderbolt Bridge", "hardware": "Ethernet", "interface": "bridge0", "type": "Ethernet" } ],
          "SPBluetoothDataType": [{ "controller_properties": { "controller_address": "AA:BB:CC:00:00:01", "controller_chipset": "BCM_4388", "controller_state": "attrib_on" },
            "device_connected": [{ "Keyboard": { "device_address": "AA:BB:CC:00:00:02", "device_minorType": "Keyboard" } }],
            "device_not_connected": [{ "Headphones": { "device_address": "AA:BB:CC:00:00:03" } }] }]
        }
        """;

    private const string DiskList = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0"><dict>
          <key>AllDisksAndPartitions</key><array>
            <dict><key>Content</key><string>GUID_partition_scheme</string><key>DeviceIdentifier</key><string>disk0</string><key>Size</key><integer>500277790720</integer>
              <key>Partitions</key><array>
                <dict><key>DeviceIdentifier</key><string>disk0s1</string><key>Size</key><integer>524288000</integer></dict>
                <dict><key>DeviceIdentifier</key><string>disk0s2</string><key>Size</key><integer>494384795648</integer></dict>
              </array></dict>
            <dict><key>Content</key><string>Apple_APFS_Container</string><key>DeviceIdentifier</key><string>disk3</string>
              <key>APFSPhysicalStores</key><array><dict><key>DeviceIdentifier</key><string>disk0s2</string></dict></array></dict>
            <dict><key>Content</key><string>FDisk_partition_scheme</string><key>DeviceIdentifier</key><string>disk4</string>
              <key>Partitions</key><array><dict><key>DeviceIdentifier</key><string>disk4s1</string><key>Size</key><integer>1000202240000</integer></dict></array></dict>
          </array>
          <key>WholeDisks</key><array><string>disk0</string><string>disk3</string><string>disk4</string></array>
        </dict></plist>
        """;

    private static string Info(string id, string protocol, bool @internal, bool? solid, string media, long size) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <plist version="1.0"><dict>
          <key>BusProtocol</key><string>{protocol}</string>
          <key>DeviceIdentifier</key><string>{id}</string>
          <key>DeviceNode</key><string>/dev/{id}</string>
          <key>Internal</key><{(@internal ? "true" : "false")}/>
          <key>MediaName</key><string>{media}</string>
          <key>RemovableMediaOrExternalDevice</key><{(@internal ? "false" : "true")}/>
          {(solid is { } s ? $"<key>SolidState</key><{(s ? "true" : "false")}/>" : "")}
          <key>TotalSize</key><integer>{size}</integer>
          <key>VirtualOrPhysical</key><string>Unknown</string>
        </dict></plist>
        """;

    private const string Df = """
        Filesystem     1024-blocks      Used Available Capacity  Mounted on
        /dev/disk3s1s1   482797652  10023456 294174382     4%    /
        devfs                  213       213         0   100%    /dev
        /dev/disk3s6     482797652   2097172 294174382     1%    /System/Volumes/VM
        /dev/disk3s5     482797652 175523456 294174382    38%    /System/Volumes/Data
        map auto_home            0         0         0   100%    /System/Volumes/Data/home
        /dev/disk4s1     976759808 104857600 871902208    11%    /Volumes/SD CARD
        """;

    private const string Mount = """
        /dev/disk3s1s1 on / (apfs, sealed, local, read-only, journaled)
        devfs on /dev (devfs, local, nobrowse)
        /dev/disk3s6 on /System/Volumes/VM (apfs, local, noexec, journaled, noatime, nobrowse)
        /dev/disk3s5 on /System/Volumes/Data (apfs, local, journaled, nobrowse, protect, root data)
        map auto_home on /System/Volumes/Data/home (autofs, automounted, nobrowse)
        /dev/disk4s1 on /Volumes/SD CARD (exfat, local, nodev, nosuid, noowners, noatime, fskit)
        """;

    private const string Ifconfig = """
        lo0: flags=8049<UP,LOOPBACK,RUNNING,MULTICAST> mtu 16384
        	options=1203<RXCSUM,TXCSUM,TXSTATUS,SW_TIMESTAMP>
        	inet 127.0.0.1 netmask 0xff000000
        en0: flags=8863<UP,BROADCAST,SMART,RUNNING,SIMPLEX,MULTICAST> mtu 1500
        	ether 3a:12:34:56:78:9a
        	inet 192.0.2.20 netmask 0xffffff00 broadcast 192.0.2.255
        	media: autoselect
        	status: active
        en1: flags=8963<UP,BROADCAST,SMART,RUNNING,PROMISC,SIMPLEX,MULTICAST> mtu 1500
        	ether 36:00:00:00:00:01
        	media: autoselect <full-duplex>
        	status: inactive
        en7: flags=8863<UP,BROADCAST,SMART,RUNNING,SIMPLEX,MULTICAST> mtu 1500
        	ether 00:11:22:33:44:55
        	media: autoselect (1000baseT <full-duplex,flow-control>)
        	status: active
        bridge0: flags=8863<UP,BROADCAST,SMART,RUNNING,SIMPLEX,MULTICAST> mtu 1500
        	ether 36:00:00:00:00:00
        	member: en1 flags=3<LEARNING,DISCOVER>
        	media: <unknown type>
        	status: inactive
        utun0: flags=8051<UP,POINTOPOINT,RUNNING,MULTICAST> mtu 1380
        	inet6 fe80::1%utun0 prefixlen 64 scopeid 0x10
        """;

    internal static MacOutputs Outputs() => new(Profiler, Sysctl, DiskList,
        new Dictionary<string, string>
        {
            ["disk0"] = Info("disk0", "Apple Fabric", true, true, "APPLE SSD AP0512Z", 500277790720),
            ["disk3"] = Info("disk3", "Apple Fabric", true, true, "", 494384795648),
            ["disk4"] = Info("disk4", "USB", false, null, "Portable SSD T7", 1000204886016)
        },
        Df, Mount, Ifconfig, "ProductName:\t\tmacOS\nProductVersion:\t\t15.5\nBuildVersion:\t\t24F74\n");

    internal static HardwareCapture Machine() => MacCapture.Build(Outputs(),
        new NetworkFacts([], new Dictionary<string, RouterFacts>(), new Dictionary<string, DnsFacts>(), [], null), false, 1200);

    [Fact]
    public void Apple_silicon_cpu_and_identity()
    {
        var capture = Machine();
        Assert.Equal("Apple M2", capture.Cpu.Name);
        Assert.Equal("Apple", capture.Cpu.Vendor);
        Assert.Equal((8, 8), (capture.Cpu.Cores, capture.Cpu.Threads));
        Assert.Equal("aarch64", capture.Cpu.Architecture);
        Assert.Equal("4 Performance + 4 Efficiency cores", MacParse.CoreMix(MacParse.Sysctl(Sysctl)));
        Assert.Equal("Laptop", capture.FormFactor);
        Assert.Equal("Mac14,2", capture.Identity.ProductVersion);
        Assert.Equal("Darwin 24.5.0", capture.Kernel);
        var slot = Assert.Single(capture.Memory.Firmware.MemorySlots);
        Assert.Equal((16384, "LPDDR5"), (slot.SizeMb, slot.MemoryType));
        Assert.Equal("Hynix", capture.Memory.Makers.Values.Single());
    }

    [Fact]
    public void Intel_memory_lists_each_slot()
    {
        var json = """
            { "_items": [ { "_name": "BANK 0/ChannelA-DIMM0", "dimm_manufacturer": "Micron", "dimm_part_number": "8ATF1G64HZ-2G6E1", "dimm_size": "8 GB", "dimm_speed": "2667 MHz", "dimm_type": "DDR4" },
                          { "_name": "BANK 2/ChannelB-DIMM0", "dimm_size": "empty", "dimm_type": "empty" } ] }
            """;
        var memory = MacParse.Memory(JsonDocument.Parse(json).RootElement.Clone(), MacParse.Identity(null));
        Assert.Equal(2, memory.Firmware.MemorySlots.Count);
        Assert.Equal(("ChannelA-DIMM0", "BANK 0", 8192, 2667), (memory.Firmware.MemorySlots[0].DeviceLocator, memory.Firmware.MemorySlots[0].BankLocator,
            memory.Firmware.MemorySlots[0].SizeMb, memory.Firmware.MemorySlots[0].SpeedMts));
        Assert.Equal(0, memory.Firmware.MemorySlots[1].SizeMb);
        Assert.Equal("Micron", Assert.Single(memory.Makers).Value);
    }

    [Fact]
    public void Drives_skip_apfs_containers_and_find_their_usb_port()
    {
        var capture = Machine();
        Assert.Equal(["disk0", "disk4"], capture.Disks.Select(d => d.Name));
        var ssd = capture.Disks[0];
        Assert.Equal(("nvme", "APPLE SSD AP0512Z", "532.120", false), (ssd.Transport, ssd.Model, ssd.Firmware, ssd.Removable));
        Assert.Equal(2, ssd.Partitions.Count);
        var usb = capture.Disks[1];
        Assert.Equal("usb", usb.Transport);
        Assert.True(usb.Removable);
        var owner = capture.UsbDevices.Single(d => d.ProductName == "Portable SSD");
        Assert.Equal(owner.SysPath, usb.UsbPath);
        Assert.StartsWith(owner.SysPath + "/", usb.SysPath, StringComparison.Ordinal);
        Assert.Contains(owner.Interfaces, i => i.Class == 0x08);
    }

    [Fact]
    public void Volumes_keep_the_system_its_data_and_external_drives()
    {
        var volumes = Machine().Volumes;
        Assert.Equal(["/", "/System/Volumes/Data", "/Volumes/SD CARD"], volumes.Select(v => v.MountPoint));
        Assert.True(volumes[0].ReadOnly);
        Assert.Equal(["disk0"], volumes[1].Disks);
        Assert.Equal(("exfat", "SD CARD", "disk4"), (volumes[2].FileSystem, volumes[2].Label, volumes[2].Disks.Single()));
        Assert.Equal(871902208L * 1024, volumes[2].FreeBytes);
    }

    [Fact]
    public void Interfaces_from_ifconfig()
    {
        var links = Machine().Links.ToDictionary(l => l.Name);
        Assert.Equal(("wifi", true, true, true), (links["en0"].Kind, links["en0"].Physical, links["en0"].Up, links["en0"].RandomMac));
        Assert.Equal(("ethernet", true, 1000L, "full", false), (links["en7"].Kind, links["en7"].Physical, links["en7"].SpeedMbps, links["en7"].Duplex, links["en7"].RandomMac));
        Assert.Equal("00:11:22:33:44:55", links["en7"].Mac);
        Assert.False(links["en1"].Physical);
        Assert.Equal("bridge0", links["en1"].Master);
        Assert.Equal(["en1"], links["bridge0"].Members);
        Assert.Equal(("vpn", true), (links["utun0"].Kind, links["utun0"].Up));
        Assert.Equal("loopback", links["lo0"].Kind);
    }

    [Fact]
    public void Counters_routes_neighbors_and_dns()
    {
        var netstat = """
            Name       Mtu   Network       Address            Ipkts Ierrs     Ibytes    Opkts Oerrs     Obytes  Coll
            lo0        16384 <Link#1>                          5000     0     900000     5000     0     900000     0
            lo0        16384 127           127.0.0.1           5000     -     900000     5000     -     900000     -
            en0        1500  <Link#11>   3a:12:34:56:78:9a   123456     2  987654321    65432     1   12345678     0
            en5*       1500  <Link#12>   ac:de:48:00:11:22        0     0          0        0     0          0     0
            utun0      1380  <Link#15>                           10     0       1200       12     0       1500     0
            """;
        var counters = MacParse.NetstatCounters(netstat);
        Assert.Equal(new NetCounters(987654321, 123456, 2, 0, 12345678, 65432, 1, 0), counters["en0"]);
        Assert.Equal(1200, counters["utun0"].BytesIn);
        Assert.Contains("en5", counters.Keys);
        Assert.Equal(900000, counters["lo0"].BytesIn);

        var routes = MacParse.DefaultRoutes("""
            Routing tables

            Internet:
            Destination        Gateway            Flags               Netif Expire
            default            192.0.2.1        UGScg                 en0
            default            192.0.2.1        UGScIg                en7
            127                127.0.0.1          UCS                   lo0
            """);
        Assert.Equal(["en0", "en7"], routes.Select(r => r.Interface));
        Assert.Equal(IPAddress.Parse("192.0.2.1"), routes[0].Gateway);

        var arp = MacParse.Arp("""
            ? (192.0.2.1) at 0:11:2:33:4:55 on en0 ifscope [ethernet]
            ? (192.0.2.9) at (incomplete) on en0 ifscope [ethernet]
            ? (192.0.2.255) at ff:ff:ff:ff:ff:ff on en0 ifscope [ethernet]
            """);
        Assert.Equal((IPAddress.Parse("192.0.2.1"), "00:11:02:33:04:55", "en0"), Assert.Single(arp));

        var dns = MacParse.DnsServers("""
            DNS configuration

            resolver #1
              nameserver[0] : 192.0.2.1
              nameserver[1] : fd00::1
              if_index : 11 (en0)

            DNS configuration (for scoped queries)

            resolver #1
              nameserver[0] : 192.0.2.1
            """);
        Assert.Equal([IPAddress.Parse("192.0.2.1")], dns);
    }

    [Fact]
    public void Memory_swap_and_boot_time()
    {
        var (available, page) = MacParse.VmStat("""
            Mach Virtual Memory Statistics: (page size of 16384 bytes)
            Pages free:                              128230.
            Pages active:                            794612.
            Pages inactive:                          897256.
            Pages speculative:                        25662.
            "Translation faults":                 7487451417.
            """);
        Assert.Equal(16384UL, page);
        Assert.Equal((128230UL + 897256UL) * 16384, available);
        Assert.Equal((2048UL * 1_048_576, 1536UL * 1_048_576), MacParse.SwapUsage(MacParse.Sysctl(Sysctl)["vm.swapusage"]));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1731400000), MacParse.BootTime(MacParse.Sysctl(Sysctl)["kern.boottime"]));
    }

    [Fact]
    public void Disk_counters_from_ioreg()
    {
        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><array>
              <dict>
                <key>IOObjectClass</key><string>IOBlockStorageDriver</string>
                <key>Statistics</key><dict>
                  <key>Bytes (Read)</key><integer>1048576</integer><key>Bytes (Write)</key><integer>2097152</integer>
                  <key>Operations (Read)</key><integer>10</integer><key>Operations (Write)</key><integer>20</integer>
                  <key>Total Time (Read)</key><integer>3000000</integer><key>Total Time (Write)</key><integer>4000000</integer>
                </dict>
                <key>IORegistryEntryChildren</key><array><dict><key>BSD Name</key><string>disk0</string></dict></array>
              </dict>
            </array></plist>
            """;
        Assert.Equal(new DiskCounters(10, 2048, 20, 4096, 0, 7), MacParse.IoregDiskStats(xml)["disk0"]);
    }

    [Fact]
    public void Wifi_from_system_profiler()
    {
        var json = """
            { "SPAirPortDataType": [ { "spairport_airport_interfaces": [ { "_name": "en0",
              "spairport_current_network_information": { "_name": "<redacted>", "spairport_network_channel": "149 (5GHz, 80MHz)",
                "spairport_network_phymode": "802.11ax", "spairport_network_rate": 1200, "spairport_security_mode": "spairport_security_mode_wpa3_transition",
                "spairport_signal_noise": "-55 dBm / -92 dBm" } } ] } ] }
            """;
        var wifi = MacParse.Wifi("en0", json);
        Assert.True(wifi.Connected);
        Assert.Null(wifi.Ssid);
        Assert.Equal((149, 5745d, -55, "Wi-Fi 6 (802.11ax)", 1200d), (wifi.Channel!.Value, wifi.FrequencyMhz!.Value, wifi.SignalDbm!.Value, wifi.Standard, wifi.TxMbps!.Value));
        Assert.Equal("WPA3 Transition", wifi.Security);
        Assert.False(MacParse.Wifi("en1", json).Connected);
    }

    [Fact]
    public void Usb_tree_both_formats()
    {
        var capture = Machine();
        Assert.Equal(2, capture.Usb.Count);
        var hub = capture.UsbDevices.Single(d => d.ProductName == "USB2.0 Hub");
        Assert.True(hub.IsHub);
        Assert.Equal("2-1", hub.Name);
        var receiver = Assert.Single(hub.Children);
        Assert.Equal(("2-1.1", 0x046d, 0xc52b, 12d, 98), (receiver.Name, receiver.Vendor, receiver.Product, receiver.SpeedMbps, receiver.MaxPowerMa));
        Assert.Equal(10000, capture.UsbDevices.Single(d => d.ProductName == "Portable SSD").SpeedMbps);

        var old = """
            { "SPUSBDataType": [ { "_name": "USB31Bus", "host_controller": "AppleUSBXHCITR", "_items": [
              { "_name": "Magic Keyboard", "device_speed": "full_speed", "vendor_id": "apple_vendor_id", "product_id": "0x029c", "bus_power_used": "500",
                "Media": [] },
              { "_name": "Flash Drive", "device_speed": "super_speed", "vendor_id": "0x0781  (SanDisk Corporation)", "product_id": "0x5581",
                "Media": [ { "_name": "Flash Drive", "bsd_name": "disk5" } ] } ] } ] }
            """;
        var (buses, owners) = MacParse.Usb(MacParse.Profiler(old));
        var devices = buses.Single().RootHub.Children;
        Assert.Equal((0x05ac, 12d, 500), (devices[0].Vendor, devices[0].SpeedMbps, devices[0].MaxPowerMa));
        Assert.Equal((0x0781, 5000d), (devices[1].Vendor, devices[1].SpeedMbps));
        Assert.Equal(devices[1].SysPath, owners["disk5"]);
    }

    [Fact]
    public void Screens_and_bluetooth()
    {
        var capture = Machine();
        Assert.Equal(2, capture.Displays.Count);
        Assert.Equal(("card0-eDP-1", 2560, 1664), (capture.Displays[0].Connector, capture.Displays[0].Edid!.Width!.Value, capture.Displays[0].Edid!.Height!.Value));
        Assert.Equal(("card0-DP-1", 60d, 2021), (capture.Displays[1].Connector, capture.Displays[1].Edid!.RefreshHz!.Value, capture.Displays[1].Edid!.Year!.Value));
        var radio = Assert.Single(capture.Bluetooth);
        Assert.False(radio.Blocked);
        Assert.Equal([("Keyboard", true), ("Headphones", false)], radio.Paired.Select(d => (d.Name, d.Connected)));
    }

    [Fact]
    public void Os_name_and_support_note()
    {
        var distro = Distro.Mac("ProductName:\t\tmacOS\nProductVersion:\t\t15.5\nBuildVersion:\t\t24F74\n");
        Assert.Equal(("macos", "macOS Sequoia 15.5", "15.5 (24F74)"), (distro.Id, distro.PrettyName, distro.Version));
        Assert.Equal("Tahoe", MacParse.MarketingName("26.1"));
        Assert.StartsWith("Apple doesn't publish end dates", Lifecycle.Of(distro, new DateOnly(2026, 11, 12)).Status, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_view_builds_from_a_mac()
    {
        var capture = Machine();
        var map = LinuxConnections.Build(capture, privacy: false, discovery: null);
        Assert.NotEmpty(LinuxConnections.Walk(map.Snapshot.Root));
        var plan = LinuxPlan.Build(capture, map, new CoreSample([]), null, privacy: false);
        Assert.NotNull(plan);
        Assert.NotNull(LinuxOverview.Build(capture, map, plan, privacy: false));
        Assert.Contains("APPLE SSD AP0512Z", LinuxOverview.ScanJson(capture, privacy: false), StringComparison.Ordinal);
        Assert.NotEmpty(LinuxConnections.ToJson(LinuxConnections.Build(capture, privacy: true, discovery: null).Snapshot));
    }

    [Fact]
    public void Storage_offers_the_startup_disk_and_plugged_in_drives()
    {
        Assert.True(StorageFill.IsMacScannable("/", "apfs", 494_384_795_648));
        Assert.True(StorageFill.IsMacScannable("/Volumes/SD CARD", "exfat", 250_000_000_000));
        Assert.False(StorageFill.IsMacScannable("/System/Volumes/Data", "apfs", 494_384_795_648));
        Assert.False(StorageFill.IsMacScannable("/System/Volumes/VM", "apfs", 494_384_795_648));
        Assert.False(StorageFill.IsMacScannable("/dev", "devfs", 213 * 1024));
        Assert.False(StorageFill.IsMacScannable("/System/Volumes/Data/home", "autofs", 0));
        Assert.False(StorageFill.IsMacScannable("/Volumes/", "apfs", 1));
    }
}
