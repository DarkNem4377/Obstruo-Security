# Privacy Policy — Obstruo Security

> **Draft — pending final legal review.** This document accompanies the
> in-development v1.0 rebuild of Obstruo. Placeholders (effective date,
> legal-entity details, jurisdiction) will be completed before public release.
> For any installed copy of the Software, the version shown at install time
> governs.

---

**Version:** 1.0 (draft — pending final legal review)

**Effective Date:** [EFFECTIVE DATE]

**Publisher:** DarkNem4377 [legal-entity details to be completed before launch] ("DarkNem4377," "we," "us," or "our")

**Contact:** obstruo.software@gmail.com

---

## 1. THE SHORT VERSION


Obstruo Security processes your DNS activity entirely on your own computer. Your browsing data, DNS logs, and credentials are stored locally in encrypted form and are never transmitted to DarkNem4377. We operate no servers, no accounts, no analytics, and no telemetry, and the Software does not phone home to us.

The Software makes only two kinds of outbound connection, both from your device and neither to DarkNem4377: (a) ordinary DNS resolution, forwarded to the same upstream resolvers your device already used; and (b) an optional blocklist-feed download, only if you configure one. Both are described in Section 4.


## 2. WHO WE ARE


DarkNem4377 is the publisher of Obstruo Security, a DNS-layer content filtering and parental-control application for Windows ("the Software"). This Privacy Policy explains what data the Software processes, where that data lives, and what — if anything — leaves your device.

Because all activity data remains on your device and is never transmitted to us, DarkNem4377 does not hold, access, or process your personal data on any server. To the extent data-protection laws such as the EU/UK General Data Protection Regulation (GDPR) apply, the person who installs and administers the Software on a device is the party in control of that device's data — not DarkNem4377.


## 3. DATA THE SOFTWARE PROCESSES LOCALLY


When active, the Software processes the following on your device, and only on your device:

    (a) DNS queries made from the device (the domain names requested by apps and browsers);
    (b) Block events: which domains were blocked, when, on which device/interface, and under which category;
    (c) Timestamps and statistical aggregates derived from the above (counters, category totals, activity charts);
    (d) Administrator credentials (PIN and password), stored only as salted bcrypt hashes — never in plain text — plus a recovery code, also stored only as a hash;
    (e) Configuration and settings you choose.

All logs and credentials are stored in a local database encrypted with SQLCipher (AES-256). The database key is a random value generated on your device and protected by Windows Data Protection (DPAPI) under the local SYSTEM account; the protected key file on disk cannot be read by other user accounts or on any other machine. DarkNem4377 never receives the key and cannot read or decrypt this data.


## 4. OUTBOUND NETWORK CONNECTIONS


The Software makes two kinds of outbound connection. Neither is to DarkNem4377, and neither transmits your logs, credentials, or settings anywhere.

4.1 DNS resolution. For domains that are not blocked, the Software forwards the DNS query to upstream DNS resolvers — by default the resolver(s) your device or network was already using, with a public fallback (Cloudflare, 1.1.1.1) only if none is available. This is how normal name resolution works: the requested domain is necessarily visible to whichever resolver answers it, exactly as it was before you installed the Software. DarkNem4377 operates no DNS resolver and receives none of these queries.

4.2 Blocklist feed updates (optional). If the Administrator configures a blocklist feed URL in Settings, the Software periodically downloads that list over HTTPS from the host the Administrator specifies. Like any web request, this exposes your device's public IP address and standard request metadata to that host — which is chosen by the Administrator, not by DarkNem4377. The Software ships with no default feed configured; no feed is fetched unless an Administrator sets one.

4.3 No automatic update / no phone-home. This version of the Software contains no automatic self-update mechanism and does not contact DarkNem4377 or any DarkNem4377 service on its own. If a future version adds any form of update check or data transmission, this Policy will be updated first (see Section 11).


## 5. WHAT WE COLLECT


Nothing automatically. The Software contains no telemetry, no analytics, no crash reporting, no advertising identifiers, and no account system. We do not receive your DNS queries, browsing history, logs, credentials, or any other data from your device. If you email us at obstruo.software@gmail.com, we receive what you choose to send and use it only to respond to you.


## 6. CHILDREN'S DATA


The Software is commonly used by parents and guardians to filter and monitor devices used by children. Any data about a child's browsing activity is processed and stored only on the monitored device itself, under the control of the installing Administrator (typically the parent or guardian). DarkNem4377 never receives or has access to any child's data. Because no children's data is collected by or transmitted to DarkNem4377, the Software does not collect personal information from children within the meaning of laws such as the U.S. Children's Online Privacy Protection Act (COPPA).


## 7. DATA RETENTION AND DELETION


You control all data. Activity logs are retained on your device for the retention period you set (default 30 days) and are cleaned up automatically after that; you can also change the period or uninstall at any time. Uninstalling the Software deletes its encrypted database as part of removal. Because the database is encrypted with a machine-bound, SYSTEM-protected key, any stray copy is unreadable on another machine or by another account.


## 8. SECURITY


Local data is protected by AES-256 database encryption (SQLCipher), credential hashing (bcrypt), a random database key protected by Windows DPAPI under the SYSTEM account, an access-control-hardened data directory, and a service architecture in which the non-elevated user interface has no direct database access. No security measure is perfect; you are responsible for the physical security of your device and for keeping your Administrator credentials and recovery code confidential.


## 9. YOUR RIGHTS


Data-protection laws such as the GDPR (EU/UK) and the California Consumer Privacy Act (CCPA) grant individuals rights over personal data held by companies — including rights of access, correction, deletion, and portability. Because DarkNem4377 holds no personal data about you, there is nothing for us to access, correct, delete, or port: every such right is exercised directly by you, on your own device, through the Software's controls or by uninstalling it. If you believe we hold any personal data about you (for example, from email correspondence), contact obstruo.software@gmail.com and we will address your request.


## 10. WHAT THIS POLICY DOES NOT COVER


This Policy covers only the Software and DarkNem4377. It does not cover: your operating system; your internet service provider; the upstream DNS resolvers your device or network uses; any blocklist-feed host an Administrator configures; or any website or download platform (such as a code-hosting service) from which you obtained the Software, each of which has its own privacy practices. Monitoring of other people conducted by an Administrator is governed by the End User License Agreement, which places responsibility for lawful monitoring on the Administrator.


## 11. CHANGES TO THIS POLICY


If the Software's data practices ever change — for example, if a future version adds any form of data transmission — this Policy will be updated first, the version number and effective date will change, and material changes will be disclosed before or upon the corresponding software update. The version of this Policy in effect for your installed version of the Software is the one that governs it.


## 12. CONTACT


Questions about this Policy or the Software's data practices: obstruo.software@gmail.com.

Obstruo Security — © 2026 DarkNem4377. All rights reserved.
