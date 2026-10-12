using BoardScout.Views;
using Xunit;

namespace BoardScout.Linux.Tests;

// Updates waiting on distributions other than Debian and Ubuntu, and Alpine's package list. The samples are the
// tools' documented output formats; none of these distributions was available to run them on.
public class UpdateTests
{
    [Fact]
    public void Dnf_counts_packages_before_obsoletes()
    {
        const string output = """

            kernel.x86_64                      6.11.4-301.fc41               updates
            curl.x86_64                        8.9.1-3.fc41                  updates
            shadow-utils.x86_64                2:4.15.1-12.fc41              updates
            Obsoleting Packages
            grub2-tools.x86_64                 1:2.12-10.fc41                updates
                grub2-tools.x86_64             1:2.12-9.fc41                 @updates
            """;
        Assert.Equal(["kernel.x86_64", "curl.x86_64", "shadow-utils.x86_64"], LinuxSystemInfo.ParseDnfUpdates(output));
    }

    [Fact]
    public void Dnf_with_nothing_waiting_is_zero() => Assert.Empty(LinuxSystemInfo.ParseDnfUpdates(""));

    [Fact]
    public void Zypper_counts_table_rows()
    {
        const string output = """
            S | Repository              | Name    | Current Version | Available Version | Arch
            --+-------------------------+---------+-----------------+-------------------+-------
            v | Main Update Repository  | curl    | 8.6.0-1.1       | 8.6.0-2.1         | x86_64
            v | Main Update Repository  | openssl | 3.1.4-3.2       | 3.1.4-4.1         | x86_64
            """;
        Assert.Equal(["curl", "openssl"], LinuxSystemInfo.ParseZypperUpdates(output));
    }

    [Fact]
    public void Pacman_counts_arrows() =>
        Assert.Equal(["linux", "mesa"], LinuxSystemInfo.ParsePacmanUpdates("linux 6.11.3.arch1-1 -> 6.11.4.arch1-1\nmesa 1:24.2.4-1 -> 1:24.2.5-1\n"));

    [Fact]
    public void Apk_counts_older_versions_only()
    {
        const string output = """
            Installed:                                Available:
            busybox-1.36.1-r29                      < 1.36.1-r31
            musl-1.2.5-r0                           < 1.2.5-r1
            """;
        Assert.Equal(["busybox-1.36.1-r29", "musl-1.2.5-r0"], LinuxSystemInfo.ParseApkUpdates(output));
    }

    [Fact]
    public void Apk_installed_database_lists_packages()
    {
        string[] lines =
        [
            "C:Q1abc=", "P:musl", "V:1.2.5-r0", "A:x86_64", "S:411323", "I:654321", "T:the musl c library", "m:Natanael Copa <ncopa@alpinelinux.org>", "t:1712345678", "",
            "C:Q1def=", "P:busybox", "V:1.36.1-r29", "I:950272", "m:Sören Tempel <soeren@alpinelinux.org>", "",
            "P:no-trailing-blank", "V:1"
        ];
        var packages = LinuxSystemInfo.ParseApkInstalled(lines);
        Assert.Equal(["musl", "busybox", "no-trailing-blank"], packages.Select(p => p.Name));
        Assert.Equal("1.2.5-r0", packages[0].Version);
        Assert.Equal("Natanael Copa", packages[0].Publisher);
        Assert.Equal("639 KB", packages[0].Size);
        Assert.Equal("", packages[0].InstallDate); // apk keeps the build time, not the install time
    }
}
