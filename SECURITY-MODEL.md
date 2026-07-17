# Obstruo Security — Threat Model

**Status:** Living document · matches the current release
**Publisher:** DarkNem4377 · obstruo.software@gmail.com

This document states, precisely, what Obstruo defends against and what it does not. We publish it because a filter you can't reason about is a filter you can't trust — and because the first question any technical reader asks is *"can't an admin just turn this off?"* The answer is nuanced, and we'd rather give it in full than have it asked as an accusation.

---

## 1. The adversary

Obstruo's adversary is not a remote hacker. It is **the person using the filtered machine** — someone with physical access, valid Windows login, and a motive to reach blocked content. This is the opposite of most security software's threat model, and it drives every design decision below.

Two deployment scenarios, with very different guarantees:

### Scenario A — The family machine (non-admin user)

The protected user (typically a child) uses a **standard, non-administrator Windows account**. The parent holds the Windows administrator account *and* the Obstruo credentials.

**In this scenario, Obstruo is a security boundary.** A standard user cannot stop the service, edit firewall rules, change system DNS, modify browser policies, alter the registry keys involved, or read the encrypted database. Every bypass route we know of requires administrator rights the user doesn't have. This configuration is winnable, and we treat holes in it as security vulnerabilities — report them (see §6).

> **Requirement:** this guarantee only holds if the protected user's account is non-admin. Giving a child an administrator account defeats Obstruo the same way it defeats every parental control on Earth. The installer and documentation say this loudly.

### Scenario B — Self-control (the administrator filtering themselves)

The protected user *is* the administrator — someone installing Obstruo on their own machine as a commitment device.

**In this scenario, Obstruo is engineered friction, not a security boundary.** An administrator with unlimited time, full system access, and no one watching can eventually remove any local software — by booting into recovery, taking ownership of protected resources, or simply reinstalling Windows. We do not claim otherwise, and any local filter that does is misleading you.

What Obstruo provides here is **distance between impulse and access**: disabling it requires credentials you may have given away, a deliberate multi-step effort, and time — which is precisely what a weak moment doesn't have. Friction is not a technicality; for impulse-driven behavior, it is the mechanism that works. But it is friction, and we name it as such.

---

## 2. What Obstruo enforces

| Mechanism | What it does |
|---|---|
| **DNS interception** | A Windows Service becomes the machine's resolver. Blocked domains never resolve — for every browser, every app, every Windows account on the device. |
| **Fail-closed watchdog** | If the service stops — killed, crashed, power loss — the filter fails *closed*: normal DNS is not restored except through a verified recovery path. Stopping Obstruo does not stop the filter. |
| **Encrypted-DNS (DoH) blocking** | Firewall rules block known DNS-over-HTTPS endpoints, and enforced browser policies (Chrome, Edge, Firefox) disable secure-DNS overrides, so browsers cannot quietly route around the local resolver. |
| **Credential gate** | Pausing, disabling, changing the blocklist, or uninstalling requires the PIN or password set during installation. Three wrong attempts triggers an escalating lockout. |
| **Encrypted local database** | Blocklist, logs, and credential hashes live in a SQLCipher (AES-256) database. The key is random, machine-bound, and protected via Windows DPAPI under the SYSTEM account — a standard user cannot extract it, and a copied database file is unreadable elsewhere. |
| **One-time recovery** | A recovery code shown exactly once at setup is the only fallback if credentials are lost. There is no support hotline that unlocks it — deliberately. |

---

## 3. Attacks and responses

| Attack | Scenario A (non-admin) | Scenario B (admin) |
|---|---|---|
| Kill the service (Task Manager, `taskkill`) | Denied — insufficient rights. | Possible — but the filter **fails closed**; killing it blocks everything rather than unblocking. |
| Change system DNS settings | Denied — insufficient rights. | Possible, but the watchdog re-enforces; defeating it requires sustained deliberate effort. |
| Browser DoH / secure-DNS bypass | Blocked by policy + firewall. | Blocked by policy + firewall while Obstruo runs. |
| Guess the PIN | Escalating lockout after 3 attempts. | Same. |
| Copy or edit the database | Unreadable — encrypted, key not extractable by a standard user. | Key is DPAPI-protected under SYSTEM; extraction requires deliberate privileged tooling, not a weak moment. |
| Uninstall | Requires Obstruo credentials. | Requires Obstruo credentials via the normal path; forcible removal is possible with effort (see §1B). |
| Boot into recovery / offline OS modification / reinstall Windows | Requires the administrator password the user doesn't have. | Possible. This is the honest outer boundary of any local software. |

---

## 4. Explicit non-goals

Obstruo does **not**:

- **Inspect page content.** Filtering is by domain. What loads inside an allowed site is out of scope.
- **Surveil.** No screenshots, no keylogging, no message reading. The only record is which domains were blocked, and when — encrypted, local.
- **Protect other devices.** Phones, tablets, consoles, and other computers on the network need their own controls. (An optional, off-by-default LAN mode lets routed devices use Obstruo as their DNS server on a private network, but this is opt-in and not the core guarantee.)
- **Phone home.** There is no server to phone, no account, no telemetry. Obstruo makes no automatic network contact beyond DNS resolution itself — it contains no self-update or update-check mechanism. New versions ship as GitHub releases you choose to download and install.
- **Defeat a determined administrator.** See §1B. Anyone selling you a local filter that claims this is selling snake oil.

---

## 5. Residual risks

- **Alternate devices.** The fastest bypass of a filtered machine is an unfiltered phone. Obstruo cannot help with this; plan for it.
- **VPNs and proxies (Scenario B).** An administrator can install tunneling software. In Scenario A, installation rights are the parent's to control.
- **Physical coercion of credentials.** If someone can watch you type your PIN, no software model survives.
- **Bugs.** No security measure is perfect. The mechanisms in §2 are the design; defects in their implementation are vulnerabilities we want to hear about.

---

## 6. Reporting a vulnerability

If you find a way for a **non-administrator user** to disable, bypass, or read Obstruo's data (Scenario A break), that is a security vulnerability. Report it privately via GitHub's [security report form](https://github.com/DarkNem4377/Obstruo-Security/security/advisories/new) (preferred), or email **obstruo.software@gmail.com** — full policy in [SECURITY.md](SECURITY.md). Please allow reasonable time for a fix before public disclosure.

Findings of the form "an administrator can remove it" are acknowledged here in advance (§1B) and are not vulnerabilities — but creative reductions of the *effort* required in Scenario B are still welcome reports; friction is the product.

---

*DENY BY DEFAULT.*
