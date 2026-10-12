# Machines: other PCs and servers, over SSH

BoardScout 2.0 can show any Linux or Windows machine you can reach with SSH: its board, Topology,
Connections with live traffic, the optimization plan, Drivers, Storage, and System, exactly as it
would show them on that machine itself. It can also check every machine at once and put the results
in one table.

There is nothing to install on the other machine and no port to open. BoardScout signs in with your
SSH keys, copies itself into a folder there, starts, and reaches its dashboard through the same SSH
connection. When you close the dashboard, the connection ends and BoardScout stops there.

## Where to find it

- **Windows app:** the **Machines** tab. A machine's dashboard opens in a window of its own.
- **Linux edition (browser):** the **Machines** tab. A machine's dashboard opens in a new browser tab.
- **Any terminal** (both editions):

  | Command | What it does |
  | --- | --- |
  | `--machines` | Lists the machines on the Machines page. |
  | `--check NAME` | Prints one machine's health summary as JSON. `--check all` checks every machine. |
  | `--connect NAME` | Opens a machine's dashboard through an SSH tunnel and prints the link; Ctrl+C disconnects. |
  | `--trust` | With `--check` or `--connect`: trusts a machine's host key the first time (its fingerprints are printed first). |
  | `--key FILE` | Signs in with this private key. |

  `NAME` is a machine from the list, a `Host` from `~/.ssh/config`, or any SSH destination such as
  `admin@192.168.1.20` or `nas.local:2222`.

## Adding machines

Every `Host` in your `~/.ssh/config` (and the files it `Include`s) shows up on its own; patterns with
`*` are left out. **Add machine** takes what you would type after `ssh`: `user@host`, `host:port`, or
a config alias, plus an optional name, private key file, and folder on the machine. **Hide** removes
a config host from the page without touching your config.

BoardScout keeps the list in `machines.json` in its data folder: names, addresses, ports, and key
*paths*. Never a password, never a key.

## Signing in

BoardScout runs your own `ssh` with `BatchMode=yes`, so SSH can never prompt, and only keys can sign
in: from `~/.ssh`, your SSH agent, or the key file chosen for the machine. A machine that only
accepts passwords can't be added until you put your public key in its `authorized_keys`.

- **Which ssh.** On Windows, BoardScout uses Windows' own OpenSSH client
  (`C:\Windows\System32\OpenSSH\ssh.exe`) or, if you choose it under **SSH program**, Git for
  Windows' copy. On Linux it uses `ssh` from your PATH.
- **Key file permissions on Windows.** Windows' OpenSSH refuses a private key that another account
  can read, including an account that no longer exists. The Machines page says so and offers two
  fixes: **Limit the key file to me** (it keeps only you, SYSTEM, and Administrators on the file) or
  **Use Git's SSH instead**, which doesn't check.
- **Host keys.** The first time, SSH doesn't know the machine. The Machines page shows the host key
  fingerprints so you can compare them with the machine's own (`ssh-keygen -lf
  /etc/ssh/ssh_host_ed25519_key.pub` on Linux, `C:\ProgramData\ssh\ssh_host_ed25519_key.pub` on
  Windows) and then **trust** it. Keys trusted here go in BoardScout's own `known_hosts` in its data
  folder; your `~/.ssh/known_hosts` isn't changed. A machine whose key has *changed* from the one SSH
  remembers is refused until you check it and remove the old key yourself.

## What happens on the other machine

1. BoardScout asks what the machine is with one `echo`: Linux or Windows, and which processor.
2. It looks in its folder there for this exact version (by SHA-256) and copies it only when it's
   missing or different: about 51 MB for Linux, more for Windows. Older versions are removed.
3. It starts BoardScout there in agent mode. The agent serves that machine's dashboard to that machine
   only (127.0.0.1), on a random port, with a fresh access key. The same SSH connection forwards a free
   port on your computer to it.
4. Closing the dashboard (or Disconnect, or Ctrl+C) ends the SSH connection; the agent stops with it.
   If the connection drops some other way, the agent stops on its own after three idle minutes. On
   Windows, if the BoardScout that opened it ends unexpectedly, Windows ends its SSH connections too.

| On the machine | Linux | Windows |
| --- | --- | --- |
| Folder | `~/.cache/boardscout` (owner-only) | `%LOCALAPPDATA%\BoardScout\Remote` |
| Inside | `agent/` the program, `data/` its settings and log, `extract/` .NET's unpacked files | the same, plus DriverScout's inventory and driver reports in `data` |
| Runs as | the SSH account | the SSH account |
| Remove it | `rm -rf ~/.cache/boardscout` | delete the folder |

Choose another folder per machine if its home folder can't run programs (mounted `noexec`).

**Privacy mode** travels: with privacy mode on, the machine's dashboard and its summary start masked.

## Which machines

| Machine | Build it runs | Notes |
| --- | --- | --- |
| Linux, x86-64, glibc (Ubuntu, Debian, Fedora, RHEL 8+, openSUSE, Arch, Proxmox…) | `linux-x64` | Tested: Ubuntu 26.04. |
| Linux, Arm64 (Raspberry Pi 4/5 64-bit, Arm servers) | `linux-arm64` | Built; not tried on hardware. |
| Linux, 32-bit ARMv7 (Raspberry Pi OS 32-bit) | `linux-arm` | Built; not tried on hardware. ARMv6 (Pi 1, Zero) can't run .NET. |
| Alpine and other musl distributions, x86-64 or Arm64 | `linux-musl-x64`, `linux-musl-arm64` | Built; not tried on Alpine. |
| Windows 10, 11, Server 2019 and later | `win-x64` | Needs OpenSSH Server, built into Windows as an optional feature. Sensors and per-driver interrupt timing need an administrator account. |
| Windows 11 on Arm | `win-x64` | Runs under Windows' x64 emulation; not tried. |
| macOS, BSD, 32-bit Linux or Windows | none | The Machines page says why. |

A BoardScout carries its own build. For a different one it looks beside itself (put the release's
other files next to `BoardScout.exe` or `boardscout`), then in its `remote-builds` download folder.
When neither has it, the Machines page offers **Get the … build from GitHub**: it downloads this
version's file from the release and keeps it only if its SHA-256 matches the release's checksum file.

## Every machine at a glance

**Check** runs BoardScout once on the machine (about 5–30 seconds) and shows its summary on the card:
support status, updates waiting, security checks that didn't pass, processor, memory, uptime, the
fullest filesystem, links running below their best, CPU temperature, and the top fixes from its
optimization plan. **Check all** checks up to three machines at a time; with two or more checked, a
table compares them.

## Turning on SSH on a Windows machine

Settings → System → Optional features → **OpenSSH Server**, then start the `sshd` service. For an
account in Administrators, put your public key in `C:\ProgramData\ssh\administrators_authorized_keys`
(only Administrators and SYSTEM may have access to that file); for other accounts,
`%USERPROFILE%\.ssh\authorized_keys`.
