![Obstruo Security — Windows DNS-layer content filtering, blocked before it loads](readme-assets/welcome-banner.png)

# Obstruo Security

[![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)
[![Latest release](https://img.shields.io/github/v/release/DarkNem4377/Obstruo-Security)](https://github.com/DarkNem4377/Obstruo-Security/releases/latest)
[![Platform: Windows 10/11](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6)](https://github.com/DarkNem4377/Obstruo-Security)

**Windows DNS-layer content filtering and bypass-resistant parental control, by [DarkNem4377](https://github.com/DarkNem4377).**

Obstruo filters the internet at the DNS layer for an entire Windows machine. A
background Windows service runs a local DNS resolver that returns `NXDOMAIN` for
blocked domains and forwards everything else, pins the system to itself, closes
the common ways around a DNS filter, and defends itself against being switched
off. A separate, non-elevated dashboard lets a parent — or a person practising
self-control — manage rules behind a PIN. Nothing leaves the machine: all
configuration, blocklist data, and history live in an encrypted local database.

*Platform: Windows 10/11 · Runtime: .NET 10 · Architecture: x64*

---

## Features

**Content filtering**
- System-wide blocking across every browser and app — filtering happens below
  the application layer, so it can't be sidestepped by switching browsers.
- Curated, categorized built-in blocklist (adult, chat, games, AI-adult,
  malware) plus your own custom rules, with per-category on/off toggles.
- **SafeSearch enforcement** for Google, YouTube (Moderate or Strict), and Bing,
  applied at the DNS layer. On by default; DuckDuckGo offers no DNS mechanism and
  is reported honestly as unsupported.
- Optional signed **blocklist-feed sync** so the built-in list can be refreshed
  from a source you configure.

**Bypass resistance**
- Pins system DNS to the local resolver and blocks queries to any other DNS
  server on port 53.
- Locks encrypted DNS: blocks DoH/DoT endpoints and applies browser policies so
  Chromium/Firefox can't quietly resolve around the filter.
- **Fail-closed by design** — if the resolver can't run, name resolution stops
  rather than silently reopening the internet.

**Control & safety**
- PIN- or password-gated changes, with a recovery code and optional **Windows
  Hello** unlock.
- **Temporary rules** — custom blocks and allow-list exceptions can auto-expire
  (15 minutes to 1 week); when one lapses, the dashboard says so.
- **Emergency pause** with a hard maximum duration, a cooldown between uses, and
  automatic resume — it can't be chained into a permanent bypass.
- **Self-protection** — a watchdog process keeps the service alive, a tamper
  detector reverts external DNS changes within seconds, and the service
  auto-restarts and survives reboots.

**Visibility**
- Live activity feed, per-category metrics, and uptime — every number comes from
  the running service, not placeholder text.
- Bypass attempts are recorded as incidents.
- Export the activity log to CSV or JSON. Logs auto-wipe on a configurable
  schedule (30-day default).
- Optional **LAN mode** (off by default) to filter DNS for other devices on the
  local network that point at this machine.

## How it works

Obstruo is a small set of cooperating components:

| Component | Runs as | Responsibility |
|---|---|---|
| **Service** | Windows Service (LocalSystem) | Local DNS resolver, blocklist matching, DoH/port-53 locking, tamper detection, LAN mode, logging, retention, and the IPC server |
| **Dashboard (UI)** | Standard user (WPF) | Status, activity feed, settings, setup wizard, and uninstall — a remote control for the service |
| **Watchdog** | Standalone process | Restores original DNS if an install or the service dies mid-flight |
| **Installer** | Elevated (WPF) | Install/upgrade with rollback, EULA acceptance, and service registration |

The dashboard never touches the database or privileged settings directly. It
talks to the service over an **authenticated named pipe** using a versioned JSON
message contract; every change is verified against the stored credential
service-side. All persistent data lives in a **SQLCipher-encrypted database**
whose key is a random value sealed with Windows DPAPI under the SYSTEM account —
so a standard (non-administrator) user cannot read or copy it.

## Security & privacy model

Obstruo is built for two distinct situations, and is honest about the difference:

- **Parental control (family):** the person being filtered is a **standard user**
  without administrator rights. Here the boundaries are real — the encrypted
  database and SYSTEM-scoped key are not readable, and the DNS/DoH locks can't be
  removed without the PIN.
- **Self-control:** the person being filtered **is** the administrator. Here
  Obstruo is deliberate, high-friction resistance rather than an unbreakable
  wall — an administrator can always dismantle software on their own machine.

DNS filtering has inherent limits (a full commercial VPN tunnels around any
DNS-based filter). Those limits, and the threat model above, are documented in
[docs/FILTERING-LIMITATIONS.md](docs/FILTERING-LIMITATIONS.md).

**Privacy:** Obstruo runs entirely locally. It makes no telemetry or analytics
calls; the only outbound request it can make is to a blocklist-feed URL you
configure. Blocked-domain history stays on the machine and auto-wipes on
schedule.

## Installation

Download the latest `Obstruo-vX.Y.Z-win-x64.zip` from the
[Releases](https://github.com/DarkNem4377/Obstruo-Security/releases) page, extract it, and
run `Obstruo.Installer.exe` (installation registers a Windows service and
requires administrator approval).

> Builds are currently **self-signed**, so Windows SmartScreen will warn on
> machines that don't already trust the certificate. Verify the download against
> the published `.sha256` before installing.

## Build from source

Requires the **.NET 10 SDK** on Windows (the UI, Installer, and Watchdog target
Windows).

```bash
dotnet build Obstruo.slnx
dotnet test  Obstruo.Tests/Obstruo.Tests.csproj
```

Build the full release layout (installer + payload + standalone watchdog) and a
distributable zip:

```powershell
pwsh ./scripts/publish.ps1
```

Always use the script — assembling the `publish/` folder by hand risks shipping a
stale payload. To trust a self-signed build on your own machine:

```powershell
pwsh ./scripts/sign-selfsigned.ps1 -Path ./publish -Trust   # elevated shell
```

## Repository layout

| Project | Target | Role |
|---|---|---|
| `Obstruo.Service` | `net10.0` (Windows Service) | DNS resolver, blocklist store, IPC server, tamper/DoH/LAN, database, retention |
| `Obstruo.UI` | `net10.0-windows` (WPF) | Dashboard, authentication, setup wizard, settings, uninstall |
| `Obstruo.Watchdog` | `net10.0-windows` | Restores DNS if an install dies mid-flight |
| `Obstruo.Installer` | `net10.0-windows` (WPF) | Install/upgrade with rollback, EULA, service registration |
| `Obstruo.Shared` | `net10.0` | IPC contracts, enums, logging, domain masking |
| `Obstruo.Tests` | `net10.0` | xUnit test suite |

## Documentation

- [Filtering limitations & threat model](docs/FILTERING-LIMITATIONS.md) — what DNS
  filtering can and cannot catch, and the family vs self-control distinction.
- [Blocklist feed format](docs/BLOCKLIST-FEED.md) — the JSON format for the
  Settings → Blocklist Feed sync.
- [Recovery](docs/RECOVERY.md) — credential recovery and last-resort removal.

## License

Obstruo Security is open source under the **[Apache License 2.0](LICENSE)** — you
are free to use, modify, and redistribute it, including commercially, subject to
the terms of the license (see also [NOTICE](NOTICE)).

Additional documents:

- [EULA](Obstruo.Installer/Resources/EULA.txt) — End User License Agreement (shown at install).
- [Privacy Policy](Obstruo.Installer/Resources/PRIVACY-POLICY.txt) — local-only data handling.
- [Third-Party Notices](Obstruo.Installer/THIRD-PARTY-NOTICES.txt) — open-source attributions.

---
© 2026 DarkNem4377 · Licensed under Apache-2.0.
