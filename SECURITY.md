# Security Policy

Obstruo is a privacy-first, **local-only** Windows application. It filters
content at the DNS layer on the user's own machine — there is no cloud
component, no account, and no server to talk to.

---

## 📣 Reporting a Security Vulnerability

If you discover a vulnerability, a privacy issue, or behavior that could
compromise user safety, please report it responsibly:

- **Preferred (private):** use GitHub's confidential
  [security report form](https://github.com/DarkNem4377/Obstruo-Security/security/advisories/new)
  (Security tab → "Report a vulnerability").
- Alternatively, email **obstruo.software@gmail.com** with the details.
- Please do **not** post working exploit details in a public issue before
  review.

All reports are reviewed in good faith, and reasonable efforts will be made to
investigate and address valid issues.

---

## 🔒 Security Scope & Guarantees

- **Local-only by design.** No user data is collected, transmitted, or stored
  remotely. No telemetry, analytics, or background reporting. The only
  outbound request the software ever makes is fetching a blocklist feed **if
  the administrator configures one** (HTTPS required). Full details in the
  [privacy policy](docs/PRIVACY-POLICY.md).
- **Encrypted at rest.** Credentials, blocklists, and block history are stored
  in a SQLCipher-encrypted database keyed with a DPAPI key held under the
  SYSTEM account, in a folder locked to SYSTEM + Administrators.
- **Authenticated control channel.** The dashboard talks to the filtering
  service over a named pipe; every sensitive command requires the
  administrator's PIN or password, with an escalating lockout on wrong
  guesses that survives service restarts.
- **Fail-closed.** If the filter cannot run, name resolution fails rather than
  silently falling back to an unfiltered resolver.
- **Tamper-resistant, honestly scoped.** DNS settings are re-pinned
  automatically if changed, and encrypted-DNS bypasses (DoH/DoT) are blocked.
  Against a **non-administrator** user this is a real boundary. Against the
  machine's **administrator** it is friction, not a wall — Obstruo does not
  attempt to bypass or subvert operating-system security boundaries, and a
  determined admin can remove it. That limit is documented, not hidden: see
  [docs/FILTERING-LIMITATIONS.md](docs/FILTERING-LIMITATIONS.md).

---

## 🔑 Locked Out?

Credential recovery paths (recovery code, and the documented administrator
last-resort removal) are described in [docs/RECOVERY.md](docs/RECOVERY.md).
There is deliberately **no software backdoor**.

---

## 🛑 Windows Defender / SmartScreen Notice

Obstruo is not yet signed with a publicly-trusted code-signing certificate, so
Windows Defender or SmartScreen may warn during download or first run. This is
expected for early-stage independent software and does **not** indicate
malicious activity. Only install Obstruo from this repository or the official
website. Code signing is planned once distribution stabilizes.

---

## ⚠️ Responsible Use

Obstruo is a protective and supportive tool. It is not designed to:

- Circumvent operating-system safeguards
- Enforce restrictions beyond the limits of the host system
- Replace parental supervision, education, or personal responsibility

Whoever deploys it is responsible for using it lawfully, including any notice
or consent their jurisdiction requires for monitoring software.

---

## 🔄 Updates & Maintenance

This project is maintained by a single developer and provided **as-is**.
Security-related fixes are prioritized when identified and may ship without
prior notice. No formal SLA is guaranteed.

---

© 2026 DarkNem4377. All rights reserved.
