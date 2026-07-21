<#
.SYNOPSIS
    Signs Obstruo binaries with a self-signed code-signing certificate.

.DESCRIPTION
    A stop-gap for development and household deployment while there is no budget
    for a publicly-trusted certificate. A self-signed signature is REAL Authenticode
    — it removes the "unknown publisher" prompt on any machine that trusts the cert
    — but it is NOT trusted by strangers' PCs. For public distribution you still need
    a paid cert (cheapest legit path: Microsoft Trusted Signing, ~$120/yr) or free
    signing via SignPath Foundation if Obstruo is open-sourced.

    What this does:
      1. Creates a reusable self-signed code-signing cert in CurrentUser\My
         (or reuses the existing one with the same subject).
      2. Authenticode-signs every Obstruo*.exe / Obstruo*.dll under -Path.
      3. Optionally installs the cert into LocalMachine\Root + TrustedPublisher
         (needs an elevated shell) so THIS machine trusts the signature.

.PARAMETER Path
    Folder to sign (searched recursively). Defaults to .\publish.

.PARAMETER Subject
    Certificate subject / publisher name. Defaults to "DarkNem4377".

.PARAMETER Trust
    Also install the cert into the machine trust stores (requires admin).

.PARAMETER TimestampServer
    RFC3161 timestamp server. Timestamping keeps signatures valid after the cert
    expires. Default is DigiCert's public server.

.EXAMPLE
    # From a normal shell — sign the publish output:
    pwsh ./scripts/sign-selfsigned.ps1 -Path ./publish

.EXAMPLE
    # From an ELEVATED shell — sign and make this PC trust it:
    pwsh ./scripts/sign-selfsigned.ps1 -Path ./publish -Trust
#>
[CmdletBinding()]
param(
    [string]$Path = (Join-Path $PSScriptRoot '..' 'publish'),
    [string]$Subject = 'DarkNem4377',
    [switch]$Trust,
    [string]$TimestampServer = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$cn = "CN=$Subject"

function Get-OrCreateSigningCert {
    $existing = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $cn -and $_.HasPrivateKey } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1

    if ($existing) {
        Write-Host "Reusing existing signing cert: $($existing.Thumbprint)"
        return $existing
    }

    Write-Host "Creating a new self-signed code-signing cert for $cn ..."
    return New-SelfSignedCertificate `
        -Subject $cn `
        -Type CodeSigningCert `
        -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA `
        -KeyLength 3072 `
        -CertStoreLocation Cert:\CurrentUser\My `
        -NotAfter (Get-Date).AddYears(5)
}

function Install-Trust([System.Security.Cryptography.X509Certificates.X509Certificate2]$cert) {
    $isAdmin = ([Security.Principal.WindowsPrincipal] `
        [Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

    if (-not $isAdmin) {
        Write-Warning '-Trust needs an elevated shell. Skipping trust installation.'
        return
    }

    # Export the public cert (no private key) and import into machine trust stores.
    $tmp = [IO.Path]::GetTempFileName() + '.cer'
    Export-Certificate -Cert $cert -FilePath $tmp -Force | Out-Null
    foreach ($store in 'Root', 'TrustedPublisher') {
        Import-Certificate -FilePath $tmp -CertStoreLocation "Cert:\LocalMachine\$store" | Out-Null
        Write-Host "Installed cert into LocalMachine\$store"
    }
    Remove-Item $tmp -Force
}

# ── Resolve target path ─────────────────────────────────────────────────────
$Path = (Resolve-Path $Path).Path
if (-not (Test-Path $Path)) { throw "Path not found: $Path" }

$targets = Get-ChildItem -Path $Path -Recurse -Include 'Obstruo*.exe', 'Obstruo*.dll' -File
if ($targets.Count -eq 0) {
    Write-Warning "No Obstruo*.exe / Obstruo*.dll found under $Path — nothing to sign."
    return
}

$cert = Get-OrCreateSigningCert
if ($Trust) { Install-Trust $cert }

# ── Sign ────────────────────────────────────────────────────────────────────
$ok = 0; $fail = 0
foreach ($file in $targets) {
    try {
        $res = Set-AuthenticodeSignature `
            -FilePath $file.FullName `
            -Certificate $cert `
            -HashAlgorithm SHA256 `
            -TimestampServer $TimestampServer `
            -ErrorAction Stop
        if ($res.Status -eq 'Valid') {
            $ok++
            Write-Host "  signed  $($file.Name)"
        } else {
            $fail++
            Write-Warning "  $($res.Status): $($file.Name) — $($res.StatusMessage)"
        }
    }
    catch {
        $fail++
        Write-Warning "  failed  $($file.Name) — $($_.Exception.Message)"
    }
}

Write-Host ""
Write-Host "Done. Signed $ok file(s), $fail failure(s)."
if (-not $Trust) {
    Write-Host "Note: this is a self-signed signature. Other PCs will still warn"
    Write-Host "unless you run with -Trust on them (elevated) or install a publicly-trusted cert."
}
if ($fail -gt 0) { exit 1 }
