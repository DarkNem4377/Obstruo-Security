# Obstruo Security v1.0.1

**Security & coverage hotfix** following the 2026-07-17 five-report DNS audit. This release closes every non-Obstruo DNS path, expands blocklist coverage, and reconciles the build with its own documentation. No breaking changes — existing installs upgrade in place (the blocklist and new categories are seeded automatically).

> Upgrading from v1.0.0 is recommended for everyone. The audit confirmed the core filter worked and failed closed; this release closes the remaining bypass routes it found.

---

## 🔒 Security — bypass routes closed

- **Classic DNS (`:53`) locked to Obstruo.** Outbound plain DNS on UDP **and** TCP port 53 is now blocked to every address except the service's own upstream resolver. A process that hardcodes a public resolver — e.g. `nslookup pornhub.com 8.8.8.8` — can no longer sidestep the filter. Loopback stays exempt, so normal local resolution is unaffected. *(Finding H1)*
- **DNS-over-TLS / DNS-over-QUIC (`:853`) presence now asserted.** The outbound 853 block rules are verified at startup and re-applied (with an alert) if anything removes them. *(Finding H3)*
- **DNS pinned on every adapter, not just the active one.** Disconnected and virtual adapters — which could carry a public resolver and win the race the moment they came online — are now pinned to `127.0.0.1` / `::1` too. A new network-change watcher re-pins immediately when a NIC changes, closing the window before the periodic tamper check. *(Findings M2, M3)*
- **IPv6 DNS pinned to `::1` on all adapters,** dropping competing site-local resolvers advertised by virtual adapters. *(Finding M3)*
- **LAN DNS mode is now off by default.** Obstruo binds loopback only and opens no inbound port unless you explicitly enable "Filter DNS for other devices" in Settings. When enabled, the inbound rule is scoped to the **Private** firewall profile (never public networks). *(Finding I-1)*
- **Per-file lockdown on the encrypted database.** Explicit SYSTEM+Administrators-only ACLs are applied directly to `obstruo.db`, its WAL/SHM sidecars, and the key blob, so no standard user can read or copy them. *(Finding M4)*

## 📋 Blocklist coverage

- **The full curated list now ships in the seed — ~6,090 domains blocked from the first minute** (up from a 444-domain sample). Earlier builds seeded only a subset while the full list existed but was never bundled; it is now an embedded resource unioned with the curated per-category set, so a clean install matches the advertised coverage.
- **Added the full leak set from the audit** — 104 domains that resolved through the v1.0.0 filter across three independent tests (e.g. `adultfriendfinder.com`, `brazzersnetwork.com`, `keezmovies.com`, `porn.com`, `pornhublive.com`, `spankwire.com`, `sex.com`, `eporner.com`, `blacked.com`, `manyvids.com`). *(Finding H2)*
- **Brand-family expansion.** Blocking a `.com` apex now automatically covers its siblings — `brand.org` / `.net` / `.co` / `.xxx` and `brandlive` / `brandnetwork` / `brandpremium.com` — so the `pornhub.com` ✓ / `pornhub.org` ✗ class of gap can't recur. *(Finding H2)*
- **Grey-tier categories are now opt-in.** Adult-adjacent and policy-call sites (dating, imageboards, soft magazines/dictionaries, casual-game portals, JP storefronts) moved into five new categories — **Dating, Forums, SoftContent, CasualGames, JPStore** — that ship **disabled**. Toggle them on in Settings; hard-adult stays always-on. *(Finding M6)*
- **Versioned seed.** Blocklist additions now reach existing installs on upgrade, not just fresh databases.

## ✨ Added

- **p50 / p95 block-decision latency** on the dashboard, so filter responsiveness is a measured number. *(Finding M1)*
- **Build identity in the app.** Every binary is stamped with its git commit; the Settings screen footer shows `Obstruo Security vX.Y.Z · build <commit>`, so a running install is always identifiable. *(Finding L3)*
- **CI release gate.** A regression suite runs on every push/PR and a build cannot ship if any audited leak is reintroduced or the filter engine regresses.

## 🛠️ Fixed / docs

- Corrected the security model doc's claim of an "anonymous GitHub update check" — Obstruo makes **no** automatic network contact beyond DNS resolution (verified against source). Privacy Policy and EULA already reflected this.
- Fixed the troubleshooting guide's service-log filename (`service-YYYYMMDD.log` under `C:\ProgramData\Obstruo\logs`).
- Clarified in the recovery guide that the `ObstruoWatchdogRecovery` task is transient by design (absent on a healthy machine).

---

## ✅ Verifying this build

1. Confirm the download's SHA-256 matches the value published with this release.
2. After install, open **Settings** and confirm the footer shows this release's build commit.
3. Optionally run the bundled verifier on the machine (admin PowerShell):
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File Verify-Obstruo-v1.0.1-G1.ps1
   ```
   It re-checks every claim from the audit (blocked names fail, controls resolve, `nslookup … 8.8.8.8` fails, IPv6 blocked, firewall rules present, DB locked down, LAN mode off) and prints a PASS/FAIL summary.

## ⏭️ Known / deferred

- **Installer and binaries are not yet Authenticode-signed** — SmartScreen may warn on first run. Verify the SHA-256 above. (Signing lands once a code-signing certificate is in place.)
- **Package size** (~286 MB) reflects a deliberate self-contained, no-prerequisites install — no .NET runtime needs to be installed separately.
- **Legal documents** (EULA / Privacy Policy) carry pending effective-date and jurisdiction details, to be finalized before wide public promotion.

---

*Obstruo is a local DNS content filter. It is a strong first layer, not an absolute wall: a determined administrator can remove it by design (see FILTERING-LIMITATIONS.md). Built for the family (non-admin) and self-control tiers.*
