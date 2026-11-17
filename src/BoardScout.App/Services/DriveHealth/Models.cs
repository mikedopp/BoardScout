using System;
using System.Collections.Generic;

namespace BoardScout.DriveHealth;

/// <summary>
/// One reading of one drive at one moment. Every number is optional: drives report different things,
/// and a reading taken without admin rights or through a USB bridge may hold only a few of them.
/// Written as one JSON line per reading in drives\&lt;key&gt;.jsonl, oldest first.
/// </summary>
public sealed class DriveReading
{
    public DriveReading Copy() => (DriveReading)MemberwiseClone();

    public DateTimeOffset At { get; set; }
    public string Key { get; set; } = "";
    public string? Machine { get; set; }
    /// <summary>smartctl, or windows when only Windows' own storage counters answered.</summary>
    public string Source { get; set; } = "smartctl";
    /// <summary>ok | partial | asleep | needs-admin | no-smart | no-tool | error</summary>
    public string Status { get; set; } = "ok";
    public string? Note { get; set; }
    /// <summary>True when the pass ran with admin rights (USB drives can only be read then).</summary>
    public bool Elevated { get; set; }

    // who the drive is
    public string? Model { get; set; }
    public string? Family { get; set; }
    public string? Serial { get; set; }
    public string? WindowsSerial { get; set; }
    public string? Firmware { get; set; }
    public long? Capacity { get; set; }
    /// <summary>ATA, NVMe or SCSI, as smartctl talks to it.</summary>
    public string? Protocol { get; set; }
    /// <summary>The smartctl -d type that answered: ata, nvme, sat, sntrealtek, scsi...</summary>
    public string? DeviceType { get; set; }
    public string? Device { get; set; }
    public int? DiskNumber { get; set; }
    /// <summary>From Windows: NVMe, SATA, USB...</summary>
    public string? Bus { get; set; }
    /// <summary>SSD or HDD.</summary>
    public string? Media { get; set; }
    public int? Rpm { get; set; }
    public string[]? Letters { get; set; }
    public string? WindowsHealth { get; set; }
    public bool InDriveDatabase { get; set; }

    // health and the counters that only go up
    public bool? SmartPassed { get; set; }
    public int? CriticalWarning { get; set; }
    public long? PowerOnHours { get; set; }
    public long? PowerCycles { get; set; }
    public long? UnsafeShutdowns { get; set; }
    public int? Temp { get; set; }
    public int? TempLimit { get; set; }
    public int? TempLifetimeMax { get; set; }
    public long? BytesWritten { get; set; }
    public long? BytesRead { get; set; }
    /// <summary>When the drive counts writes in a unit smartctl doesn't know: the raw count, and WriteUnit = "unknown".</summary>
    public long? WriteUnits { get; set; }
    public long? ReadUnits { get; set; }
    public string? WriteUnit { get; set; }
    public int? PercentUsed { get; set; }
    public bool? PercentUsedEstimated { get; set; }
    public int? Spare { get; set; }
    public int? SpareThreshold { get; set; }
    public long? MediaErrors { get; set; }
    public long? ErrorLogEntries { get; set; }
    public long? AtaErrors { get; set; }
    public long? WarningTempMinutes { get; set; }
    public long? CriticalTempMinutes { get; set; }
    public long? ControllerBusyMinutes { get; set; }
    public long? LoadCycles { get; set; }
    public long? StartStops { get; set; }
    public long? Reallocated { get; set; }
    public long? Pending { get; set; }
    public long? Uncorrectable { get; set; }
    public long? CrcErrors { get; set; }
    public string? SelfTest { get; set; }
    public bool? SelfTestPassed { get; set; }
    public long? SelfTestHours { get; set; }
    public List<AtaAttribute>? Attributes { get; set; }

    // flash in phones, tablets and TVs (eMMC or UFS), read over adb
    /// <summary>
    /// Life used as the flash chip itself estimates it, in 10% steps: 1 = 0-10% used ... 10 = 90-100%, 11 = past its
    /// rated life. A and B are two kinds of memory cells in the chip; the worse one counts.
    /// </summary>
    public int? FlashLifeA { get; set; }
    public int? FlashLifeB { get; set; }
    /// <summary>The chip's spare-block level: 1 normal, 2 warning (80% of its reserve used), 3 urgent.</summary>
    public int? FlashPreEol { get; set; }
    /// <summary>
    /// Bytes written since the device last started, and which start that was (Linux boot id). A device keeps no
    /// lifetime total, so <see cref="BytesWritten"/> is added up from these, starting at the first reading.
    /// </summary>
    public long? WrittenSinceBoot { get; set; }
    public string? BootId { get; set; }
    /// <summary>Android version, for devices read over adb.</summary>
    public string? OsVersion { get; set; }
}

/// <summary>Windows System log events grouped by disk identity and event ID.</summary>
public sealed class DiskIncident
{
    public int EventId { get; set; }
    public int DiskNumber { get; set; }
    public string? Model { get; set; }
    public string? WindowsSerial { get; set; }
    public int Count { get; set; }
    public DateTimeOffset First { get; set; }
    public DateTimeOffset Last { get; set; }

    public string Description => EventId switch
    {
        7 => "bad blocks",
        11 => "controller errors",
        51 => "I/O errors during paging",
        153 => "I/O retries",
        154 => "hardware I/O errors",
        157 => "unexpected removals",
        _ => "disk events",
    };

    // an unexpected removal is usually a USB drive pulled without ejecting: worth watching, not a failing drive
    public string Severity => EventId switch { 7 or 154 => "critical", 153 or 157 => "watch", _ => "warn" };
}

/// <summary>The raw Windows log records <see cref="WindowsInventory.GroupDiskEvents"/> turns into incidents.</summary>
public sealed class DiskEventLog
{
    public List<DiskFault> Faults { get; set; } = new();
    public List<DateTimeOffset> Boots { get; set; } = new();
    public List<DiskAttach> Attaches { get; set; } = new();
}

/// <summary>One System log disk fault: event ID, the disk number Windows used, when.</summary>
public sealed class DiskFault
{
    public int Id { get; set; }
    public int Disk { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>A Partition/Diagnostic 1006 record: which drive Windows attached under a disk number, and when.</summary>
public sealed class DiskAttach
{
    public int Disk { get; set; }
    public DateTimeOffset At { get; set; }
    public string? Serial { get; set; }
    public string? Model { get; set; }
}

public sealed class AtaAttribute
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Value { get; set; }
    public int Worst { get; set; }
    public int? Thresh { get; set; }
    public long Raw { get; set; }
    public string? RawText { get; set; }
    /// <summary>"now" or "past" when the normalized value reached the threshold.</summary>
    public string? WhenFailed { get; set; }
}

/// <summary>How full one volume was at one moment. One line per volume per pass in volumes.jsonl.</summary>
public sealed class VolumeReading
{
    public DateTimeOffset At { get; set; }
    public string Letter { get; set; } = "";
    public string? Label { get; set; }
    public string? FileSystem { get; set; }
    public long Size { get; set; }
    public long Free { get; set; }
    public int? DiskNumber { get; set; }
    public string? DriveKey { get; set; }
    public string? Health { get; set; }
    public string? Status { get; set; }
}

/// <summary>Something worth saying about a drive or a volume, worst first: critical, warn, watch, info.</summary>
public sealed class Finding
{
    public string Severity { get; set; } = "info";
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string? Advice { get; set; }
    /// <summary>"today" when one reading says it, "trend" when it comes from comparing readings.</summary>
    public string Basis { get; set; } = "today";
    public DateTimeOffset? Since { get; set; }

    public static int Rank(string severity) => severity switch { "critical" => 4, "warn" => 3, "watch" => 2, "info" => 1, _ => 0 };
}

/// <summary>A change between two readings, for the "what changed" timeline.</summary>
public sealed class DriveEvent
{
    public DateTimeOffset At { get; set; }
    public string Severity { get; set; } = "info";
    public string Text { get; set; } = "";
}

/// <summary>One calendar month: what the counters did between its first and last reading (or the last reading before it).</summary>
public sealed class MonthRow
{
    public string Month { get; set; } = "";
    public int Readings { get; set; }
    public long? Hours { get; set; }
    public long? Written { get; set; }
    public long? Read { get; set; }
    public long? WriteUnits { get; set; }
    public int? WearFrom { get; set; }
    public int? WearTo { get; set; }
    public int? TempMax { get; set; }
    public long? NewProblems { get; set; }
    public long? UnsafeShutdowns { get; set; }
    public long? LoadCycles { get; set; }
}
