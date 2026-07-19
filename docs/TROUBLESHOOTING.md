# Obstruo — Troubleshooting

Quick fixes for the things testers hit most. For lost credentials or full
manual removal, see **[RECOVERY.md](RECOVERY.md)**. For what Obstruo can and
cannot block by design, see **[FILTERING-LIMITATIONS.md](FILTERING-LIMITATIONS.md)**.

Obstruo is a local DNS filter that **fails closed**: if the filter isn't
running, DNS stays pointed at it and nothing resolves. So "no internet" is
usually Obstruo protecting you, not a crash — the fixes below tell the two apart.

---

## Windows warns "Windows protected your PC" on first run

Expected. The first public build is **not code-signed yet**, so Microsoft
SmartScreen has no reputation for it.

1. Verify the download first: compare the SHA-256 printed on the
   [release page](https://github.com/DarkNem4377/Obstruo-Security/releases/latest)
   against your file — in PowerShell: `Get-FileHash .\Obstruo-v1.0.2-win-x64.zip`.
2. If it matches, click **More info → Run anyway**.

If the hash does **not** match, do not run it — re-download and report it.

---

## Installation didn't finish / internet is now broken

If the installer crashed partway, a recovery window appears on your next sign-in
and restores your DNS automatically. Click **OK** and let it run, then try the
installer again.

If you closed that window or it didn't appear, restore internet manually from an
**elevated** Command Prompt (repeat per adapter — list them with
`netsh interface show interface`):

```cmd
netsh interface ipv4 set dnsservers name="Wi-Fi" source=dhcp
netsh interface ipv6 set dnsservers name="Wi-Fi" source=dhcp
```

---

## "No internet" after a normal install

First, check whether Obstruo is *intentionally* blocking (fail-closed) or
genuinely down. Open the dashboard — the banner tells you which:

| Dashboard shows | Meaning | Fix |
|---|---|---|
| **Active** | Filtering is on; the site you tried is blocked | Working as intended — whitelist it if it should be allowed (below) |
| **Port 53 conflict** | Another DNS app (Acrylic, pi-hole for Windows, dnscrypt-proxy, Internet Connection Sharing) already owns port 53 | Stop that app, then restart the service (below) |
| **Error / proxy unresponsive** | The filter stopped answering; Obstruo is restarting itself | Wait ~1 minute — it self-heals. If it doesn't, see the next section |
| Dashboard **won't open at all** | The service isn't running | See "The app won't open and internet is dead" |

**Restart the service** (elevated Command Prompt):

```cmd
sc stop ObstruoService
sc start ObstruoService
```

If a blocked page seems cached, flush the resolver: `ipconfig /flushdns`.

---

## The app won't open AND internet is dead

This is the one case where you're stuck: the filter's DNS pin is still in place,
but the service isn't running to answer — so there's no internet and the
dashboard (which talks to the service) can't open, which means the in-app
uninstall is unreachable.

Get back online first, then remove Obstruo. From an **elevated** Command Prompt:

```cmd
:: 1. Restore internet immediately (repeat per adapter)
netsh interface ipv4 set dnsservers name="Wi-Fi" source=dhcp
netsh interface ipv6 set dnsservers name="Wi-Fi" source=dhcp

:: 2. Confirm whether the service exists / why it won't start
sc query ObstruoService
```

If you want to keep Obstruo, reboot and let the service start cleanly. If it
still won't start, do the **full manual removal in [RECOVERY.md](RECOVERY.md)**
(it also restores the Windows DNS Client and clears the firewall/browser
policies), then reinstall.

> A **standard (non-admin) user cannot do any of this** — that is the family
> protection working as designed, not a bug.

---

## A site I need is blocked

Add it to the allow-list from the dashboard (**Whitelist → Add domain**). This
requires your PIN or password. A whitelisted domain covers itself and all its
subdomains and overrides every block rule. You can set an expiry for a temporary
allow.

---

## A site that should be blocked still loads

Work through these in order:

1. **Flush DNS** — the old answer may be cached: `ipconfig /flushdns`, then
   reload.
2. **Add it as a custom domain** (dashboard → Blocklist → Add), or enable the
   category it belongs to in Settings.
3. **It may be a known limit.** Obstruo filters by **domain, not page content**,
   and can't see inside an encrypted tunnel. A full-tunnel **VPN**, **Tor**, or
   an app using a **DoH provider not on Obstruo's block list** can bypass it.
   These are documented, expected gaps — see
   **[FILTERING-LIMITATIONS.md](FILTERING-LIMITATIONS.md)**. Report it only if it
   is a plain domain that *should* have matched.

---

## "Too many wrong attempts — locked out"

After 3 wrong PIN/password/recovery entries, Obstruo locks credential checks for
an escalating window (1, then 3, then 5 … minutes), and the lockout survives a
service restart. Just wait it out.

Note: the lockout is machine-wide by design, so a wrong-PIN spammer can trigger
it. Protection stays **on** the whole time — only *changes* are paused.

---

## Forgot my PIN or password

Use your **recovery code** on the dashboard's auth screen ("Use recovery code").
A correct code clears all credentials and re-runs the setup wizard. Recovery
guesses share the same lockout as PIN guesses. If PIN, password, **and** recovery
code are all lost, only an administrator can remove Obstruo manually — see
**[RECOVERY.md](RECOVERY.md)**.

---

## Reporting a bug

Please **[open an issue](https://github.com/DarkNem4377/Obstruo-Security/issues)**
with:

- **Build version** and the **SHA-256** you verified.
- **Windows edition + version** (e.g. Windows 11 Home 24H2) and whether you were
  on an **admin** or **standard** account.
- **What you did → what you expected → what happened.**
- The relevant **service log**: the newest `service-*.log` (named
  `service-YYYYMMDD.log`) in `C:\ProgramData\Obstruo\logs` (that folder is
  admin-only — copy it from an elevated prompt). The logs never contain your
  PIN, password, or the encryption key.

Tester feedback directly shapes the stable release.
