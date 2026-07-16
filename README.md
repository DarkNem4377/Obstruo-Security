![Obstruo Security — blocked before it loads](readme-assets/welcome-banner.png)

# 🛡️ Obstruo Security

> ### BLOCKED BEFORE IT LOADS.
>
> A fail-closed, tamper-resistant content filter for Windows.
> It refuses unwanted sites at the DNS layer — every browser, every app,
> system-wide — and your browsing never leaves the machine.

---

> ## 🧪 First public build — out now, in testing
>
> Obstruo has been rebuilt from the ground up, and the first public build
> (**v1.0.0**) is **[available for download](https://github.com/DarkNem4377/Obstruo-Security/releases/latest)**
> — currently in its **testing and debugging phase**. It is **free for
> everyone, forever**: no license key, no trial timer, no locked "premium"
> tier, and no paid tier coming later.
> Older builds have been withdrawn and should not be used.
>
> Found a bug or something odd? **[Open an issue](https://github.com/DarkNem4377/Obstruo-Security/issues)**
> — tester feedback directly shapes the stable release. To hear about new
> builds, click **Watch → Custom → Releases** at the top of this page.
>
> *The download is not yet code-signed, so Windows SmartScreen will warn on
> first run — verify the SHA-256 published on the release page, then choose
> **More info → Run anyway**.*

---

## Why Obstruo exists

Cloud parental-control services route your family's browsing through their
servers, tied to an account they hold. Obstruo cannot do this — **there is
nothing to route to.** No account. No server. No telemetry. Everything runs
and stays on your own machine, encrypted.

And unlike most filters, Obstruo **fails closed**: crash it, kill it, cut the
power — the filter does not unlock. Most filters fail open. Obstruo is built
the other way.

## What's in the first public build

| | |
|---|---|
| **System-wide DNS filtering** | A Windows Service becomes the machine's resolver. Blocked domains never resolve — for every browser and every app, including private/incognito windows. |
| **5,900+ domains blocked from the first minute** | A curated blocklist is active the moment installation completes. Add your own domains any time; removing anything requires your credentials. |
| **Fail-closed watchdog** | If the service stops — crash, kill, power loss — nothing opens when the guard goes down. Normal DNS returns only through a verified recovery path. |
| **Encrypted-DNS bypass blocking** | Firewall rules close known DNS-over-HTTPS routes, and enforced browser policies (Chrome, Edge, Firefox) disable secure-DNS overrides. The local resolver stays the only door. |
| **Credential-gated control** | Pausing, allow-listing, settings, and uninstall all require the PIN or password you set — with an escalating lockout on wrong guesses that survives service restarts. Windows Hello supported where hardware allows. |
| **Encrypted local data** | Blocklist, logs, and credential hashes live in a SQLCipher (AES-256) database keyed to the machine. A copied database file is unreadable anywhere else. |
| **One-time recovery code** | Shown exactly once at setup. Lose your PIN, password, *and* the code, and there is no back door — deliberately. |
| **Quiet by design** | No branded block pages, no popups. A blocked site simply never loads. |

## The guarantee

Data leaving your computer — **the complete list**:

| | |
|---|---|
| Account required | **NONE** |
| Cloud sync | **NONE** |
| Telemetry & analytics | **NONE** |
| Browsing history uploaded | **NONE** |
| Blocklist & logs | **LOCAL · ENCRYPTED** |

That is the complete list.

## Two readers. One product.

**The parent** — you want the machine to refuse the material, quietly,
without a dashboard your child can find or a cloud account logging what they
searched. Install once. Set the credentials. Close the lid.

**The self-binder** — you want a fail-closed commitment device on your own
machine, one that future you cannot trivially undo in a weak moment. You want
to know exactly what it does — and that it keeps doing it when tested.

## Honest about its limits

A security tool you can trust is one that states its edges plainly. Obstruo
filters by **domain**, not page content; it does not watch, screenshot, or
keylog; it protects Windows machines, not phones; and no local software can
indefinitely stop a determined administrator — which is why the family
configuration uses a standard (non-admin) account for the protected user.

The full adversary model — what is a security boundary, what is engineered
friction, and every attack/response we design for — is published in
[SECURITY-MODEL.md](SECURITY-MODEL.md).

## Documentation

* [Threat model](SECURITY-MODEL.md) — the adversary, the two deployment
  scenarios, and the honest boundaries of each.
* [Security policy](SECURITY.md) — scope, guarantees, and how to report a
  vulnerability.
* [Filtering limitations](docs/FILTERING-LIMITATIONS.md) — what DNS-layer
  filtering can and cannot catch.
* [Recovery](docs/RECOVERY.md) — credential recovery and last-resort removal.
* [Privacy policy](docs/PRIVACY-POLICY.md) *(draft)* — what data exists,
  where it lives, and what (nothing) leaves your device.
* [End User License Agreement](docs/EULA.md) *(draft)* — the terms shown at
  install time.

## 🧪 Project status

**Current version: [v1.0.0](https://github.com/DarkNem4377/Obstruo-Security/releases/latest) — development complete, now in testing and debugging.**

The architecture is built and shipped; this phase is about proving it in the
real world. Current priorities:

* Gathering tester feedback and fixing reported bugs
* Hardening install / upgrade / uninstall paths across Windows editions
* Refining the dashboard and setup experience
* Expanding blocklist management and feed syncing
* Code signing for public distribution (removes the SmartScreen warning)

Requirements: **Windows 10 / 11 (64-bit)** · administrator rights for
one-time install · no internet account of any kind.

## 📄 License

Obstruo is proprietary software — see [LICENSE.txt](LICENSE.txt).

© 2026 DarkNem4377. All rights reserved.

---

*DENY BY DEFAULT.*
