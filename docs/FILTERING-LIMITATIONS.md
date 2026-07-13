# Obstruo — What DNS Filtering Can and Can't Do

Obstruo filters at the **DNS layer**: when a device asks "what IP is
`example.com`?", Obstruo answers `NXDOMAIN` for blocked names and forwards the
rest. This is effective, low-overhead, and covers the overwhelming majority of
everyday browsing — but it is **not** an absolute wall. Be honest with yourself
(and with anyone you deploy this for) about the boundaries below.

## The threat model this is built for

| Tier | Adversary | What Obstruo provides |
|---|---|---|
| **Family** | A **non-admin** user (child on a standard account) | A real boundary. They cannot stop the service, change DNS, or read the data — that all requires admin. |
| **Self-control** | The user **is** the admin | Friction, not a wall. Meaningful speed-bumps against impulse, defeatable by a determined admin. |

Obstruo is designed so the family tier is *winnable*. It does **not** claim to
stop a determined, technical admin — that's out of scope by design.

## What Obstruo already hardens

- **System DNS pinned** to the local proxy (IPv4 → `127.0.0.1`, IPv6 → `::1`),
  re-pinned automatically within seconds if changed (tamper detection).
- **Encrypted DNS blocked** so browsers/apps can't quietly resolve elsewhere:
  - DoH provider IPs blocked on TCP+UDP 443,
  - DoT/DoQ blocked globally on TCP+UDP 853,
  - Chrome, Edge, and Firefox DoH disabled via enterprise policy.
- **Fail-closed:** if the proxy can't run, name resolution fails rather than
  falling back to an unfiltered resolver.
- **Data + config locked** to SYSTEM/Administrators (SQLCipher DB, DPAPI key,
  ACL-hardened folder).

## What DNS filtering fundamentally cannot catch

These are **inherent to filtering by domain name** — no configuration closes
them completely:

1. **Connections by raw IP.** If an app or user connects to `93.184.216.34`
   directly, there's no DNS lookup to block.
2. **Apps with hardcoded resolvers.** Some software ships its own DoH/DoT to a
   fixed IP. Obstruo blocks the *known* big providers and all port 853, but a
   novel resolver IP on 443 can slip through until added.
3. **Encrypted SNI / ECH.** Even when DNS is filtered, encrypted ClientHello can
   hide which site is being reached over an allowed IP/CDN.
4. **VPNs and proxies.** A full-tunnel VPN carries DNS *inside* the tunnel,
   bypassing the local proxy entirely. (Obstruo blocks known VPN/proxy *domains*
   under the `Bypass` category, which stops casual setup but not a pre-configured
   or portable client.)
5. **Alternate networks.** A phone hotspot, a different Wi-Fi, or a live-USB OS
   sidesteps the machine entirely.
6. **The hosts file (admin only).** An administrator can map a blocked domain
   to its real IP in `C:\Windows\System32\drivers\etc\hosts` — the OS answers
   from the file and never asks DNS. Editing it requires admin, so the family
   tier holds; for the self-control tier it is one more admin escape hatch,
   like uninstalling.
7. **Shared IPs / CDNs.** Blocking by domain can't distinguish two sites behind
   the same CDN IP; filtering stays at the name layer for exactly this reason.

## Recommended layered defenses

DNS filtering is one layer. For a stronger posture, combine it with:

- **A standard (non-admin) Windows account** for the monitored user — this is
  what makes the family tier hold.
- **Router / network-level DNS** pointed at Obstruo (LAN mode) or at a filtering
  resolver, so other devices are covered too.
- **OS-level parental controls** (Microsoft Family Safety, screen time) for
  app-install and account restrictions Obstruo doesn't manage.
- **Keeping the `Bypass` category enabled and the blocklist synced** so new
  VPN/proxy/DoH domains are picked up.

## The honest summary

Obstruo raises the bar a lot for a non-admin user and adds real friction for
everyone else. It is a **strong first layer**, not a guarantee. A motivated,
technical user — especially one with admin — can get around DNS-only filtering.
Position it that way to the people relying on it.
