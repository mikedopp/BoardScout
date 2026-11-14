using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BoardScout.DriveHealth;

/// <summary>
/// What Windows itself knows about the disks and volumes: the storage cmdlets (Get-PhysicalDisk, Get-Volume,
/// Get-Partition) plus, with admin rights, Windows' own reliability counters, which answer for some USB drives
/// that smartctl can't reach. One PowerShell call per pass.
/// </summary>
public static class WindowsInventory
{
    internal const string Script = @"
$ErrorActionPreference = 'SilentlyContinue'
$disks = @(Get-PhysicalDisk | ForEach-Object { [pscustomobject]@{ n = [int]$_.DeviceId; name = $_.FriendlyName; serial = ""$($_.SerialNumber)"".Trim(); media = ""$($_.MediaType)""; bus = ""$($_.BusType)""; health = ""$($_.HealthStatus)""; status = ($_.OperationalStatus -join ', '); size = [int64]$_.Size } })
$rel = @()
# one disk at a time: a card reader or virtual disk that refuses must not cost the others their counters
$rel = @(foreach ($d in Get-PhysicalDisk) { try { $c = $d | Get-StorageReliabilityCounter -ErrorAction Stop; if ($c) { [pscustomobject]@{ n = [int]$d.DeviceId; temp = $c.Temperature; tempMax = $c.TemperatureMax; poh = $c.PowerOnHours; wear = $c.Wear; readErr = $c.ReadErrorsUncorrected; writeErr = $c.WriteErrorsUncorrected; startStop = $c.StartStopCycleCount; loadUnload = $c.LoadUnloadCycleCount } } } catch { } })
$parts = @(Get-Partition | Where-Object DriveLetter)
$vols = @(Get-Volume | Where-Object DriveLetter | ForEach-Object { $v = $_; $p = $parts | Where-Object DriveLetter -eq $v.DriveLetter | Select-Object -First 1
  [pscustomobject]@{ letter = ""$($v.DriveLetter):""; label = $v.FileSystemLabel; fs = $v.FileSystem; size = [int64]$v.Size; free = [int64]$v.SizeRemaining; health = ""$($v.HealthStatus)""; status = ($v.OperationalStatus -join ', '); disk = if ($p) { [int]$p.DiskNumber } else { -1 } } })
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
[pscustomobject]@{ disks = $disks; reliability = $rel; volumes = $vols; elevated = $admin } | ConvertTo-Json -Depth 4 -Compress
";

    public static async Task<Inventory> ReadAsync(CancellationToken ct = default)
    {
        var inv = new Inventory { Source = "windows" };
        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-EncodedCommand");
            psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::OutputEncoding = [Text.Encoding]::UTF8\n" + Script)));
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync(ct);
            var err = p.StandardError.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(90));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } inv.Error = "Windows storage cmdlets took more than 90 s"; return inv; }
            string json = (await output).Trim();
            await err;
            if (json.Length == 0) { inv.Error = "Windows storage cmdlets returned nothing"; return inv; }
            Parse(json, inv);
        }
        catch (Exception ex) { inv.Error = ex.Message; }
        return inv;
    }

    public static void Parse(string json, Inventory inv)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        inv.Source = "windows";
        inv.Elevated = root.TryGetProperty("elevated", out var el) && el.ValueKind == JsonValueKind.True;
        foreach (var d in Items(root, "disks"))
        {
            inv.Disks.Add(new Inventory.Disk
            {
                Number = Smartctl.Int(d, "n") ?? -1,
                Name = Smartctl.Str(d, "name"),
                Serial = Smartctl.Str(d, "serial") is string s && s.Length > 0 ? s : null,
                Media = Blank(Smartctl.Str(d, "media")),
                Bus = Blank(Smartctl.Str(d, "bus")),
                Health = Blank(Smartctl.Str(d, "health")),
                Status = Blank(Smartctl.Str(d, "status")),
                Size = Smartctl.Long(d, "size") ?? 0,
            });
        }
        foreach (var r in Items(root, "reliability"))
        {
            var disk = inv.Disks.FirstOrDefault(x => x.Number == (Smartctl.Int(r, "n") ?? -2));
            if (disk == null) continue;
            // Windows fills unsupported counters with 0; a disk at 0 °C or with 0 hours isn't a reading
            disk.Temp = Smartctl.Int(r, "temp") is int t and > 0 ? t : null;
            disk.TempMax = Smartctl.Int(r, "tempMax") is int tm and > 0 ? tm : null;
            disk.PowerOnHours = Smartctl.Long(r, "poh") is long h and > 0 ? h : null;
            disk.Wear = Smartctl.Int(r, "wear");
            disk.ReadErrors = Smartctl.Long(r, "readErr");
            disk.WriteErrors = Smartctl.Long(r, "writeErr");
            disk.StartStops = Smartctl.Long(r, "startStop") is long ss and > 0 ? ss : null;
            disk.LoadCycles = Smartctl.Long(r, "loadUnload") is long lc and > 0 ? lc : null;
            disk.HasCounters = disk.Temp != null || disk.PowerOnHours != null || disk.StartStops != null;
        }
        foreach (var v in Items(root, "volumes"))
        {
            inv.Volumes.Add(new Inventory.Volume
            {
                Letter = Smartctl.Str(v, "letter") ?? "",
                Label = Blank(Smartctl.Str(v, "label")),
                FileSystem = Blank(Smartctl.Str(v, "fs")),
                Size = Smartctl.Long(v, "size") ?? 0,
                Free = Smartctl.Long(v, "free") ?? 0,
                Health = Blank(Smartctl.Str(v, "health")),
                Status = Blank(Smartctl.Str(v, "status")),
                Disk = Smartctl.Int(v, "disk") ?? -1,
            });
        }
    }

    internal static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>ConvertTo-Json writes a one-item array as a bare object; take either.</summary>
    static IEnumerable<JsonElement> Items(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var e)) yield break;
        if (e.ValueKind == JsonValueKind.Array) { foreach (var x in e.EnumerateArray()) yield return x; }
        else if (e.ValueKind == JsonValueKind.Object) yield return e;
    }
}
