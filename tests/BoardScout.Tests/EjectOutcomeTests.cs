using BoardScout.Services;
using Xunit;

namespace BoardScout.Tests;

// What an eject card says. Only an eject Windows confirmed may say "safe to unplug".
public class EjectOutcomeTests
{
    [Fact]
    public void Confirmed_eject_is_safe_to_unplug() =>
        Assert.StartsWith("Safe to unplug", new EjectOutcome(true, 0, null, 0).Message);

    [Theory]
    [InlineData(DeviceTree.EjectGone, 0x0D)]          // Windows no longer lists the device
    [InlineData(DeviceTree.EjectBusy, -1)]            // BoardScout's own reads didn't finish in time
    [InlineData(DeviceTree.EjectLookupFailed, 0x05)]  // CM_Locate_DevNode failed for another reason
    [InlineData(5, 0x17)]                             // a file is open (CR_REMOVE_VETOED)
    [InlineData(3, 0x17)]                             // a program
    [InlineData(4, 0x17)]                             // a service
    [InlineData(12, 0x17)]                            // needs administrator rights
    [InlineData(13, 0x17)]                            // Windows: already removed
    [InlineData(0, 0x17)]                             // unknown veto
    public void Anything_else_never_says_safe(int veto, int result)
    {
        var outcome = new EjectOutcome(false, veto, null, result);
        Assert.DoesNotContain("safe to unplug", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Device_gone_says_nothing_was_ejected() =>
        Assert.StartsWith("Nothing was ejected", new EjectOutcome(false, DeviceTree.EjectGone, null, 0x0D).Message);

    [Fact]
    public void Lookup_failure_reports_the_code() =>
        Assert.Contains("0x1F", new EjectOutcome(false, DeviceTree.EjectLookupFailed, null, 0x1F).Message);

    [Fact]
    public void Names_the_program_holding_the_drive() =>
        Assert.Contains("explorer.exe is using it", new EjectOutcome(false, 3, @"C:\Windows\explorer.exe", 0x17).Message);

    [Fact]
    public void Device_paths_are_not_shown_as_a_program()
    {
        // For an open handle Windows names a device path (which embeds a serial number); never show it.
        var outcome = new EjectOutcome(false, 5, @"STORAGE\Volume\{abc}#0000000012345", 0x17);
        Assert.Null(outcome.Who);
        Assert.DoesNotContain("STORAGE", outcome.Message);
    }
}
