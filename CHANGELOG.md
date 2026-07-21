# Obstruo Security v1.0.4

**Feature release.** Closes a batch of gaps found in a full code audit: SafeSearch is now actually enforced, bypass attempts become real incidents, the activity log can be exported, and custom blocks can be temporary. Existing installs upgrade in place — the new settings seed automatically and an additive schema migration adds the temporary-block column.

## ✨ Added

- **SafeSearch enforcement (Google / YouTube / Bing).** The dashboard's SafeSearch grid is now backed by real DNS enforcement: search-engine hostnames are rewritten to their vendor "force SafeSearch" host (e.g. `forcesafesearch.google.com`). On by default for the three engines that support it; YouTube offers a Moderate/Strict choice in Settings. DuckDuckGo has no DNS mechanism and stays honestly "Not supported."
- **Real incidents.** Bypass attempts now open an `INC-nnnn` incident (written off the DNS hot path) and every blocked event is linked to its incident. A new credential-gated read exposes them to the UI. The Incidents table was previously created but never written.
- **Export the activity log.** A new Export section in Settings writes the last 30 days of blocked events to a CSV or JSON file you choose (PIN-gated; the service writes the file directly so a large export isn't capped by the IPC message limit).
- **Temporary custom blocks.** Blocking a domain now offers a duration — Permanent, 15 minutes, 1 hour, 1 day, or 1 week. A temporary block lifts itself automatically.
- **Whitelist-expiry notification.** When a temporary allow-list exception lapses, the dashboard now says so instead of it expiring silently.

## 🛠️ Fixed / Cleaned

- **Honest "slipped through" metric.** The hero panel now states the fail-closed guarantee rather than a static counter.
- **Removed a dead `geo` field** from the IPC log-event contract (it was always null). The harmless DB column is left in place to avoid a drop-column migration.
- **Clarified the legacy key-derivation comment** so the frozen `…-REPLACE-BEFORE-SHIP` literal reads as intentional migration material, not an unshipped TODO.

_Deferred to a later release: VPN/proxy detection, process monitoring, incident response actions, and the blocklist-source rename — each a substantial change that warrants its own verified build._

---

# Obstruo Security v1.0.3

**Tester-feedback release.** Fixes the "Wi-Fi connected but no internet" failure mode found in round-4 testing, adds whitelist visibility and a whitelist-integrity guard, and makes the dashboard honest — every number and status it shows now comes from the running service.

## 🛠️ Fixed

- **Upstream DNS resilience.** The proxy forwarded only to the *first* backed-up adapter's DNS servers, frozen at service start — after a network change it could keep forwarding to unreachable resolvers forever, so every lookup failed closed and Windows showed "No Internet, Secured". The upstream list is now the union of **all** adapters' backed-up servers plus Cloudflare, and it is rebuilt automatically on every network change.
- **Outage alerting.** When every upstream stops responding, the service now logs critically and raises a dashboard alert explaining why the internet is down (and logs recovery) instead of failing silently.
- **Service alerts are visible.** The dashboard's alert banner was an empty placeholder — tamper alerts, port-53 conflicts, proxy restarts, and outage alerts never actually rendered. They now appear in a dismissible banner.

## ✨ Added

- **View the whitelist.** A new "View (PIN)" button shows every whitelisted domain with its added date, expiry, and reason — with per-entry removal (credential-gated).
- **Whitelist integrity guard.** A domain that matches the system blocklist (including brand-family variants and wildcard descendants) can no longer be whitelisted; conflicting rows are also swept at startup and when categories are re-enabled.
- **Honest dashboard tiles.** The "AI Threat Summary" and "Anomaly Detection" placeholders (features that don't exist) are replaced by a live **Upstream DNS health** tile and the exact **build version + commit**. Blocklist rule counts are now queried live from the database instead of hardcoded text that drifted out of date.

To verify this build, run the bundled `Verify-Obstruo-v1.0.3-G1.ps1` from an elevated PowerShell after installing; step 0 confirms you are actually testing v1.0.3 before any filtering check runs.

---

# Obstruo Security v1.0.2

**Service-startup hotfix.** The original v1.0.1 package could not start its protection stack: a dependency-injection cycle (`TamperDetector → IpcServer → UninstallService → TamperDetector`) crashed the service worker on launch, so the DNS proxy never bound port 53 and **no filtering ran at all**. v1.0.2 ships the fix plus the hardening that followed it.

## 🛠️ Fixed

- **Service starts.** Broke the constructor-injection cycle (`UninstallService` now takes the tamper detector lazily — it only needs it at uninstall). A new test builds the real service container with full validation on every CI run, so a cycle can never ship again.
- **Version stamp.** Binaries now report the true release version plus build commit (v1.0.1 self-reported `1.0.0+<commit>`).
- **Stale LAN rules swept on upgrade.** A v1.0.0 install (LAN mode on by default) left `Obstruo-LAN-DNS-*` inbound allow rules behind; they are now removed on startup whenever LAN mode is off.
- **Fail-closed startup guard.** An unexpected startup error no longer stops the service silently — it logs, raises a dashboard alert, and holds the machine fail-closed instead of leaving DNS pinned to a dead resolver with no explanation.
- **Verifier gates on build identity.** The bundled G.1 verifier now refuses to run (exit 99) if the installed binary's version doesn't match the release it ships with — a stale install can no longer produce a plausible-looking report.

To verify this build, run the bundled `Verify-Obstruo-v1.0.2-G1.ps1` from an elevated PowerShell after installing; step 0 confirms you are actually testing v1.0.2 before any filtering check runs.

---

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
