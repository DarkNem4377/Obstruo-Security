using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

// Lets Obstruo.Tests exercise internal validation logic (e.g. the IPC
// UpdateConfig allowlist) without widening the public surface.
[assembly: InternalsVisibleTo("Obstruo.Tests")]

// The service targets net10.0 (not net10.0-windows) so the installer's payload
// layout stays stable, but every subsystem (registry, DPAPI, pipes ACLs,
// netsh) is Windows-only. Declaring it here zeroes out the ~30 CA1416
// warnings so a REAL cross-platform regression becomes visible again.
[assembly: SupportedOSPlatform("windows")]
