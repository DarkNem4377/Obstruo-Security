# End User License Agreement — Obstruo Security

> **Draft — pending final legal review.** This document accompanies the
> in-development v1.0 rebuild of Obstruo. Placeholders (effective date,
> legal-entity details, jurisdiction) will be completed before public release.
> For any installed copy of the Software, the version shown at install time
> governs.

---

**Version:** 1.0 (draft — pending final legal review)

**Effective Date:** [EFFECTIVE DATE]

**Licensor:** DarkNem4377 [legal-entity details to be completed before launch] ("DarkNem4377," "we," "us," or "our")

---

**IMPORTANT -- READ CAREFULLY BEFORE INSTALLING OR USING THIS SOFTWARE.**


This End User License Agreement ("Agreement") is a legal agreement between you ("you," "User," or "Licensee") and DarkNem4377 governing your installation and use of the Obstruo Security software, including all associated components, services, blocklists, documentation, and updates (collectively, the "Software").

BY CLICKING "I ACCEPT," OR BY INSTALLING OR USING THE SOFTWARE, YOU AGREE TO BE BOUND BY THIS AGREEMENT. IF YOU DO NOT AGREE, CLICK "DECLINE" AND DO NOT INSTALL OR USE THE SOFTWARE.


## 1. DEFINITIONS


1.1 "Software" means the Obstruo Security application, including its Windows service, user interface, watchdog component, installer, bundled blocklists and databases, documentation, and any Updates provided by DarkNem4377.

1.2 "Device" means the Windows computer on which the Software is installed.

1.3 "Administrator" means the person who installs the Software, accepts this Agreement, and configures the Software's credentials and settings.

1.4 "Monitored User" means any person who uses the Device while the Software is active.

1.5 "Update" means any patch, upgrade, new version, or modification of the Software made available by DarkNem4377.


## 2. ELIGIBILITY, AGE, AND AUTHORITY REPRESENTATION


2.1 By accepting this Agreement, you represent and warrant that:

    (a) you are of legal age to form a binding contract in your jurisdiction, or your parent or legal guardian has reviewed this Agreement and accepted it on your behalf; and

    (b) you own the Device, or you have been granted lawful authority by the Device's owner to install system-level software on it; and

    (c) all information you provide during setup is accurate.

2.2 If you accept this Agreement on behalf of another person or a household, you represent that you have the authority to bind that person or household to its terms.


## 3. LICENSE GRANT


3.1 Subject to your compliance with this Agreement, DarkNem4377 grants you a limited, non-exclusive, non-transferable, non-sublicensable, revocable license to install and use the Software on Devices that you own or lawfully control, for personal, family, or internal household use.

3.2 The Software is licensed, not sold. DarkNem4377 and its licensors retain all right, title, and interest in and to the Software, including all intellectual property rights. No rights are granted to you other than those expressly stated in this Agreement.

3.3 The Software is currently provided free of charge. DarkNem4377 reserves the right to introduce paid versions, features, or licensing tiers in the future. Any such change will not retroactively charge you for versions you already lawfully installed, but continued access to Updates or new versions may be conditioned on acceptance of revised terms.


## 4. LICENSE RESTRICTIONS


You shall not, and shall not permit any third party to:

    (a) copy, distribute, sell, rent, lease, lend, sublicense, or otherwise transfer the Software to any third party;

    (b) reverse engineer, decompile, disassemble, or otherwise attempt to derive the source code of the Software, except to the extent such restriction is prohibited by applicable law;

    (c) modify, adapt, translate, or create derivative works of the Software;

    (d) remove, alter, or obscure any proprietary notices on or in the Software;

    (e) circumvent, disable, or tamper with the Software's authentication, tamper-protection, watchdog, or licensing mechanisms, except through the recovery procedures the Software itself provides;

    (f) use the Software to violate any applicable law, including laws governing interception of communications, surveillance, or privacy;

    (g) extract, redistribute, or repurpose the Software's blocklists or databases outside the Software.


## 5. MONITORING AUTHORITY AND CONSENT -- IMPORTANT


5.1 The Software intercepts, filters, and logs Domain Name System (DNS) queries made from the Device. DNS queries reveal the internet domains visited by Monitored Users and constitute a record of browsing activity.

5.2 You are solely responsible for ensuring that your installation and use of the Software on any Device is lawful in your jurisdiction. Depending on your jurisdiction, monitoring the device activity of another person may require that person's knowledge or consent, or may require that you hold legal authority over that person (for example, as the parent or legal guardian of a minor).

5.3 By installing the Software, you represent and warrant that, for each Device on which you install it:

    (a) you own or lawfully control the Device; and

    (b) for each Monitored User, you either (i) hold parental or legal guardianship authority over that person, or (ii) have obtained that person's informed consent to the monitoring, or (iii) are that person yourself.

5.4 You assume all liability arising from monitoring conducted without lawful authority or required consent. DarkNem4377 does not and cannot verify your authority over any Device or Monitored User, and disclaims all responsibility for your compliance with surveillance, interception, wiretap, privacy, and data protection laws applicable to you.


## 6. SYSTEM MODIFICATION AND DNS INTERFERENCE ACKNOWLEDGMENT


6.1 You acknowledge and agree that the Software, by design:

    (a) modifies the Device's DNS configuration to route DNS queries through the Software;

    (b) installs a Windows service and watchdog process that run continuously with elevated privileges;

    (c) may modify firewall rules and Windows registry entries; and

    (d) blocks access to internet domains according to its blocklists and your configuration.

6.2 You acknowledge that software of this nature can, in the event of malfunction, misconfiguration, conflict with other software, or improper removal, result in: partial or total loss of internet connectivity on the Device; blocking of domains you did not intend to block; failure to block domains you intended to block; or interference with other network-dependent software.

6.3 You accept these risks. DarkNem4377 provides recovery mechanisms and documentation on a best-effort basis but does not warrant uninterrupted or error-free network operation on any Device running the Software.


## 7. NO GUARANTEE OF FILTERING OR PROTECTION


7.1 The Software's content filtering operates on DNS-level blocklists. No blocklist is complete, and no filtering technology is infallible. Objectionable, harmful, or unwanted content may be accessible despite the Software's operation, including through domains not present in the blocklists, direct IP access, alternative DNS mechanisms, VPNs, encrypted DNS protocols, or other circumvention techniques.

7.2 The Software is a tool to assist supervision. It is not a substitute for it. DarkNem4377 makes no representation or warranty that the Software will prevent any Monitored User from accessing any particular content, and expressly disclaims all liability for any content accessed, or consequences arising from content accessed, on any Device running the Software.

7.3 Threat-level indicators, category classifications, and analytical labels displayed by the Software are informational heuristics only and do not constitute security assessments, guarantees, or advice.


## 8. PRIVACY AND DATA


8.1 The Software processes DNS query data and stores activity logs locally on the Device in encrypted form. DarkNem4377 does not receive, collect, or have access to your DNS queries, browsing activity, logs, or credentials.

8.2 The Software makes outbound network connections only from the Device and only for: (a) DNS resolution — forwarding queries for non-blocked domains to the upstream DNS resolvers the Device or network already uses (with a public fallback resolver if none is available), exactly as normal name resolution does; and (b) blocklist feed updates — if, and only if, the Administrator configures a blocklist feed URL, periodically downloading that list over HTTPS from the host the Administrator specifies, which exposes the Device's IP address and standard request metadata to that host. This version of the Software contains no automatic self-update mechanism and does not transmit any data to DarkNem4377.

8.3 Full details are set out in the Obstruo Security Privacy Policy (PRIVACY-POLICY.txt, distributed with the Software and published at https://github.com/DarkNem4377/Obstruo-Security/blob/main/docs/PRIVACY-POLICY.md), which is incorporated into this Agreement by reference.


## 9. UPDATES


9.1 DarkNem4377 may, but is not obligated to, provide Updates. Updates may add, modify, or remove features.

9.2 Updates may be subject to revised versions of this Agreement. Where a revised Agreement applies, you will be prompted to accept it before or upon installation of the Update; declining means you may not install that Update but may continue using your current version under the terms you previously accepted.

9.3 DarkNem4377 may discontinue the Software, Updates, or blocklist maintenance at any time without liability to you.


## 10. THIRD-PARTY COMPONENTS


The Software incorporates third-party open-source components, which are licensed under their own terms. Those terms are set out in the THIRD-PARTY-NOTICES file included with the Software and accessible from the Software's About screen. To the extent any third-party license conflicts with this Agreement with respect to that component, the third-party license governs that component.


## 11. DISCLAIMER OF WARRANTIES


11.1 THE SOFTWARE IS PROVIDED "AS IS" AND "AS AVAILABLE," WITH ALL FAULTS AND WITHOUT WARRANTY OF ANY KIND. TO THE MAXIMUM EXTENT PERMITTED BY APPLICABLE LAW, DARKNEM4377 DISCLAIMS ALL WARRANTIES, EXPRESS, IMPLIED, OR STATUTORY, INCLUDING WITHOUT LIMITATION IMPLIED WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE, TITLE, NON-INFRINGEMENT, ACCURACY, QUIET ENJOYMENT, AND ANY WARRANTIES ARISING FROM COURSE OF DEALING OR USAGE OF TRADE.

11.2 WITHOUT LIMITING THE FOREGOING, DARKNEM4377 DOES NOT WARRANT THAT: (a) THE SOFTWARE WILL MEET YOUR REQUIREMENTS; (b) THE SOFTWARE WILL OPERATE UNINTERRUPTED, ERROR-FREE, OR SECURE; (c) ANY CONTENT WILL BE SUCCESSFULLY BLOCKED OR FILTERED; (d) DEFECTS WILL BE CORRECTED; OR (e) THE SOFTWARE IS FREE OF VULNERABILITIES.

11.3 No oral or written information or advice given by DarkNem4377 shall create any warranty.


## 12. LIMITATION OF LIABILITY


12.1 TO THE MAXIMUM EXTENT PERMITTED BY APPLICABLE LAW, IN NO EVENT SHALL DARKNEM4377 BE LIABLE FOR ANY INDIRECT, INCIDENTAL, SPECIAL, CONSEQUENTIAL, EXEMPLARY, OR PUNITIVE DAMAGES, OR FOR ANY LOSS OF PROFITS, REVENUE, DATA, GOODWILL, OR INTERNET CONNECTIVITY, OR FOR THE COST OF SUBSTITUTE SOFTWARE OR SERVICES, ARISING OUT OF OR RELATING TO THIS AGREEMENT OR THE SOFTWARE, WHETHER BASED ON CONTRACT, TORT (INCLUDING NEGLIGENCE), STRICT LIABILITY, OR ANY OTHER THEORY, EVEN IF DARKNEM4377 HAS BEEN ADVISED OF THE POSSIBILITY OF SUCH DAMAGES.

12.2 TO THE MAXIMUM EXTENT PERMITTED BY APPLICABLE LAW, DARKNEM4377'S TOTAL AGGREGATE LIABILITY ARISING OUT OF OR RELATING TO THIS AGREEMENT OR THE SOFTWARE SHALL NOT EXCEED THE GREATER OF (a) THE AMOUNTS YOU PAID FOR THE SOFTWARE IN THE TWELVE (12) MONTHS PRECEDING THE CLAIM, OR (b) TEN UNITED STATES DOLLARS (USD $10).

12.3 The limitations in this Section apply notwithstanding any failure of essential purpose of any limited remedy.

12.4 Some jurisdictions do not allow the exclusion or limitation of certain damages; in such jurisdictions, the above limitations apply to the maximum extent permitted.


## 13. INDEMNIFICATION


You agree to indemnify, defend, and hold harmless DarkNem4377 and its owners, officers, and agents from and against any claims, damages, losses, liabilities, costs, and expenses (including reasonable legal fees) arising out of or relating to: (a) your use or misuse of the Software; (b) your violation of this Agreement; (c) your violation of any applicable law, including surveillance, interception, privacy, or data protection laws; or (d) monitoring conducted without lawful authority or required consent.


## 14. TERMINATION


14.1 This Agreement is effective until terminated. It terminates automatically, without notice, if you breach any of its terms.

14.2 You may terminate this Agreement at any time by uninstalling the Software and destroying all copies.

14.3 Upon termination, you must cease all use of the Software and uninstall it. Sections 2, 5, 6.2-6.3, 7, 10, 11, 12, 13, 14.3, 15, and 16 survive termination.


## 15. CONSUMER RIGHTS PRESERVATION


Nothing in this Agreement excludes, limits, or restricts any statutory rights or remedies that you have as a consumer under the mandatory laws of your country of residence (including, where applicable, the laws of the European Union, the United Kingdom, Australia, or other jurisdictions) that cannot be lawfully excluded, limited, or waived by agreement. In the event of a conflict between this Agreement and such non-waivable rights, those rights prevail to the extent of the conflict.


## 16. GOVERNING LAW AND DISPUTE RESOLUTION


16.1 This Agreement shall be governed by and construed in accordance with the laws of [JURISDICTION], without regard to its conflict-of-laws principles.

16.2 Any dispute arising out of or relating to this Agreement or the Software shall be subject to the exclusive jurisdiction of the courts of [JURISDICTION/VENUE], except where the mandatory consumer protection laws of your country of residence grant you the right to bring proceedings in your local courts.


## 17. GENERAL PROVISIONS


17.1 Entire Agreement. This Agreement, together with the Privacy Policy and THIRD-PARTY-NOTICES file, constitutes the entire agreement between you and DarkNem4377 regarding the Software and supersedes all prior understandings.

17.2 Severability. If any provision of this Agreement is held invalid or unenforceable, that provision shall be enforced to the maximum extent permissible and the remaining provisions shall remain in full force.

17.3 No Waiver. Failure by DarkNem4377 to enforce any provision shall not constitute a waiver of that or any other provision.

17.4 Assignment. You may not assign this Agreement. DarkNem4377 may assign it in connection with a merger, acquisition, reorganization, or sale of assets.

17.5 Amendments. DarkNem4377 may revise this Agreement for future versions of the Software. The version you accepted at installation governs your use of that installed version. Revised terms apply only upon your acceptance.

17.6 Export Compliance. You agree to comply with all applicable export and import laws in your use of the Software.

17.7 Contact. Questions about this Agreement: obstruo.software@gmail.com.

BY CLICKING "I ACCEPT," YOU ACKNOWLEDGE THAT YOU HAVE READ THIS AGREEMENT, UNDERSTAND IT, AND AGREE TO BE BOUND BY ITS TERMS.

Obstruo Security -- © 2026 DarkNem4377. All rights reserved.
