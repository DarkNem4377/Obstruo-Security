# Obstruo — Credential Recovery & Last-Resort Removal

## Normal paths

| Situation | Path |
|---|---|
| Know PIN or password | Everything works: uninstall, pause, whitelist, settings. |
| Forgot PIN **and** password, have recovery code | Launch the dashboard → auth screen → "Use recovery code". A correct code atomically clears all credentials and re-runs the setup wizard. |

Recovery-code guesses share the same escalating lockout as PIN/password
guesses (3 wrong → 1, 3, 5, … minute lockouts, persisted across service
restarts).

## Last resort: PIN, password, AND recovery code all lost

There is deliberately **no software backdoor** — for the family threat model,
that is the product working as intended. An **administrator** of the machine
can remove Obstruo manually. This is the documented, supported escape hatch
for the self-control tier (where the user is their own admin) and for support
walking a locked-out parent through recovery.

From an **elevated** Command Prompt, in this order:

```cmd
:: 1. Stop and remove the service (also stops tamper re-pinning of DNS)
sc stop ObstruoService
sc delete ObstruoService

:: 2. Restore DNS to DHCP on every adapter (repeat for each connected adapter;
::    list names with: netsh interface show interface)
netsh interface ipv4 set dnsservers name="Ethernet" source=dhcp
netsh interface ipv6 set dnsservers name="Ethernet" source=dhcp
netsh interface ipv4 set dnsservers name="Wi-Fi" source=dhcp
netsh interface ipv6 set dnsservers name="Wi-Fi" source=dhcp

:: 3. Restore the Windows DNS Client (Obstruo sets it to manual start when it
::    claims port 53 — skip if "reg query HKLM\SOFTWARE\Obstruo /v DnscacheDisabled"
::    shows nothing)
sc config Dnscache start= auto
sc start Dnscache

:: 4. Remove the DoH/DoT firewall rules
powershell -Command "Get-NetFirewallRule -DisplayName 'Obstruo-*' | Remove-NetFirewallRule"

:: 5. Remove browser DoH policies
reg delete "HKLM\SOFTWARE\Policies\Google\Chrome" /v DnsOverHttpsMode /f
reg delete "HKLM\SOFTWARE\Policies\Microsoft\Edge" /v DnsOverHttpsMode /f
:: Firefox: delete the DNSOverHTTPS block from
::   "C:\Program Files\Mozilla Firefox\distribution\policies.json"

:: 6. Remove files, data, and registry state
rmdir /s /q "C:\Program Files\Obstruo"
rmdir /s /q "C:\ProgramData\Obstruo"
reg delete "HKLM\SOFTWARE\Obstruo" /f
reg delete "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Obstruo" /f
schtasks /Delete /F /TN ObstruoWatchdogRecovery

:: 7. Reboot (clears the resolver cache and any cached policy)
shutdown /r /t 0
```

Notes:

- Step 2 is the one that restores internet — do it even if later steps fail.
- The database (`C:\ProgramData\Obstruo\obstruo.db`) is SQLCipher-encrypted
  with a DPAPI-SYSTEM key; deleting it destroys the block history and the
  credential hashes. There is no way to read it without SYSTEM access on the
  same machine — that is by design.
- A **standard (non-admin) user cannot perform any of this.** For the family
  tier that is the security property, not a bug.
