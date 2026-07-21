using Obstruo.Service;
using Obstruo.Service.Data;
using Obstruo.Service.Dns;
using Obstruo.Shared.Logging;

var builder = Host.CreateApplicationBuilder(args);

// ── File logging ───────────────────────────────────────────────────────────
// Rolling daily log under ProgramData\Obstruo\logs (inherits the hardened
// SYSTEM+Administrators ACL). EventLog alone leaves nothing support can
// collect for failed installs, DNS outages, or tamper events.
builder.Logging.AddProvider(new FileLoggerProvider(
    Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Obstruo", "logs"),
    prefix: "service"));

// ── Windows Service ────────────────────────────────────────────────────────
builder.Services.AddWindowsService(options =>
{
    // MUST match the name the installer passes to `sc create` (InstallEngine.ServiceName).
    // A mismatch breaks the EventLog source and any name-based service lookups.
    options.ServiceName = "ObstruoService";
});

// ── Application services ─────────────────────────────────────────────────────
// All Obstruo singletons + the hosted Worker. Centralized in AddObstruoServices
// so the DI-graph regression test builds the identical object graph.
builder.Services.AddObstruoServices();

var host = builder.Build();
host.Run();