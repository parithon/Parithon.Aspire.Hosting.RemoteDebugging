# Aspire Remote Debugging Plan (Development-Only)

## Purpose
Create an Aspire Hosting extension that runs app workloads on a remote machine (Linux or Windows), while preserving a local F5 debugging workflow in AppHost.

This plan is explicitly for development scenarios, not production deployment.

## Scope and Constraints
- Development-only workflow.
- Remote targets: Linux and Windows.
- Target Aspire version: 13.2.2.
- Transport: SSH/SCP only (OpenSSH required on Windows targets).
- Support both web projects and .NET worker service projects.
- Worker service mode must support remote service install/update and debug-capable service start.
- Required explicit platform at resource configuration time.
- `AddRemoteProject<TProject>` must encode the source project via generic type so Debug build output can be resolved deterministically.
- SSH password authentication should be supported via Aspire `ResourceParameter` to avoid repeated hardware-key unlock prompts during debug sessions.
- Remote host should support Aspire `ResourceParameter` for environment-specific hostnames/IPs.
- Remote username should support Aspire `ResourceParameter` through `WithCredential(userName, auth)` overloads for environment-specific account mapping.
- Remote directory should support Aspire `ResourceParameter` for environment-specific deployment roots.
- Core fluent methods should provide overloads for `string` and `ResourceParameter` where applicable.
- No auto-detect as default behavior.
- TDD-first delivery (red -> green -> refactor for every behavior slice).

## Resource Model
Introduce a first-class resource type for remote workloads:
- `RemoteHostResource`
- Appears in Aspire dashboard.
- Supports lifecycle state transitions and endpoint metadata.

## AppHost API Shape
Align extension method shape with Aspire 13.2.2 `Add*` patterns on `IDistributedApplicationBuilder`.

Require `OSPlatform` explicitly in `AddRemoteProject`.

Require project identity explicitly in generic `AddRemoteProject<TProject>`.

Prefer fluent configuration for primary connection settings:
- `WithHost(...)`
- `WithCredential(userName, auth)`
- `WithRemoteDirectory(...)`

Worker service fluent extensions:
- `WithServiceRegistration(name: ...)`

Use optional configure callback only for advanced options (timeouts, retries, custom start behavior).

Required-only API example:

```csharp
var remoteHost = builder.AddParameter("remote-host", secret: false);
var remoteUser = builder.AddParameter("remote-user", secret: false);
var remoteDir = builder.AddParameter("remote-dir", secret: false);
var sshPassword = builder.AddParameter("remote-ssh-password", secret: true);

var remoteWeb = builder
    .AddRemoteProject<Projects.WebApp>(
        name: "web-remote",
        platform: OSPlatform.Windows)
    .WithHost(remoteHost)
    .WithCredential(remoteUser, RemoteAuth.Password(sshPassword))
    .WithRemoteDirectory(remoteDir);
```

Explicit API example:

```csharp
var remoteHost = builder.AddParameter("remote-host", secret: false);
var remoteUser = builder.AddParameter("remote-user", secret: false);
var remoteDir = builder.AddParameter("remote-dir", secret: false);
var sshPassword = builder.AddParameter("remote-ssh-password", secret: true);

var remoteWeb = builder
    .AddRemoteProject<Projects.WebApp>(
        name: "web-remote",
        platform: OSPlatform.Windows)
    .WithHost(remoteHost)
    .WithSshPort(22)
    .WithCredential(remoteUser, RemoteAuth.Password(sshPassword))
    .WithRemoteDirectory(remoteDir)
    .WithHttpEndpoint(remotePort: 8080, preferredLocalPort: 5080)
    .WithHttpsEndpoint(remotePort: 8443, preferredLocalPort: 5443)
    .WithDebugger(remotePort: 4711, preferredLocalPort: 4711)
    .WithOtlp(remotePort: 4318, preferredLocalPort: 4318)
    .WithHealthCheck(path: "/health");
```

Worker service API example:

```csharp
var remoteHost = builder.AddParameter("remote-host", secret: false);
var remoteUser = builder.AddParameter("remote-user", secret: false);
var remoteDir = builder.AddParameter("remote-dir", secret: false);
var sshPassword = builder.AddParameter("remote-ssh-password", secret: true);

var remoteWorker = builder
    .AddRemoteProject<Projects.WorkerService>(
        name: "worker-remote",
        platform: OSPlatform.Linux)
    .WithHost(remoteHost)
    .WithCredential(remoteUser, RemoteAuth.Password(sshPassword))
    .WithRemoteDirectory(remoteDir)
    .WithServiceRegistration(name: "worker-remote", configure: service =>
    {
        service.RunAsCredential = ServiceRunAs.Credential(
            userName: builder.AddParameter("service-user", secret: false),
            password: builder.AddParameter("service-password", secret: true));
    })
    .WithOtlp(remotePort: 4318, preferredLocalPort: 4318)
    .WithHealthCheck(path: "/health");
```

Planned extension signature (conceptual):

```csharp
public static IResourceBuilder<RemoteHostResource> AddRemoteProject<TProject>(
    this IDistributedApplicationBuilder builder,
    string name,
    OSPlatform platform)
    where TProject : IProjectMetadata;

public static IResourceBuilder<RemoteHostResource> WithHost(
    this IResourceBuilder<RemoteHostResource> builder,
    string host);

public static IResourceBuilder<RemoteHostResource> WithHost(
    this IResourceBuilder<RemoteHostResource> builder,
    IResourceBuilder<ParameterResource> hostParameter);

public static IResourceBuilder<RemoteHostResource> WithRemoteDirectory(
    this IResourceBuilder<RemoteHostResource> builder,
    string remoteDirectory);

public static IResourceBuilder<RemoteHostResource> WithRemoteDirectory(
    this IResourceBuilder<RemoteHostResource> builder,
    IResourceBuilder<ParameterResource> remoteDirectoryParameter);

public static IResourceBuilder<RemoteHostResource> WithCredential(
    this IResourceBuilder<RemoteHostResource> builder,
    string userName,
    RemoteAuth credential);

public static IResourceBuilder<RemoteHostResource> WithCredential(
    this IResourceBuilder<RemoteHostResource> builder,
    IResourceBuilder<ParameterResource> userNameParameter,
    RemoteAuth credential);

public static IResourceBuilder<RemoteHostResource> WithServiceRegistration(
    this IResourceBuilder<RemoteHostResource> builder,
    string name,
    Action<RemoteServiceRegistrationOptions>? configure = null);

public sealed class RemoteServiceRegistrationOptions
{
    public ServiceRunAs? RunAsCredential { get; set; }
}
```

## Lifecycle Contract
### Initialize / Prepare
1. Validate required `platform` (`OSPlatform.Linux` or `OSPlatform.Windows`).
2. Validate declared shell with a no-op command (validation only).
3. Resolve project type from `TProject` and locate Debug build output path.
4. Resolve host, username, remote directory, and SSH auth configuration from fluent settings; if `ResourceParameter` values are configured, resolve them before connecting.
5. Open SSH/SCP session.

### Pre-Deploy Cleanup
1. Detect stale process from previous broken/killed sessions.
2. Terminate gracefully.
3. Force-terminate on timeout.
4. Clear stale temp/lock files if needed.

### Deploy
1. Build local app in Debug configuration.
2. Copy Debug artifacts (including symbols/PDBs and config files) via SCP.
3. Verify remote directory contents.

### Start
1. Resolve effective start command.
    - If `StartCommand` is provided, use it.
    - If `StartCommand` is omitted and `platform == OSPlatform.Windows`, use `<AssemblyName>.exe` when present; otherwise fallback to `dotnet <AssemblyName>.dll`.
    - If `StartCommand` is omitted and `platform == OSPlatform.Linux`, use `dotnet <AssemblyName>.dll`.
    - If no valid startup artifact exists, fail fast with actionable diagnostics.
2. Start remote process via platform-specific command template.
3. Create local SSH forwarders for:
   - app endpoints (HTTP/HTTPS/TCP)
   - vsdbg
   - OTLP
4. Publish endpoint metadata to Aspire dashboard.
5. Emit ordered setup events to resource console.
6. Run health checks and mark resource ready.

### Service Mode (Worker Projects)
Default behavior: debug-capable service startup is always ON for this development-only extension.
Default run-as behavior: use the same account context as the service registration operation when `RunAsCredential` is not explicitly configured.

1. If service registration is configured, install or update service definition after deploy.
    - Linux: create/update systemd unit under `/etc/systemd/system`.
    - Windows: create/update Windows Service definition.
2. Configure service start command from the same startup resolution rules.
3. Apply service run-as credential from service registration options (`username` + secret `password`) when configured.
4. If run-as credential is not configured, use default account context from the service registration operation.
5. Inject debugger-friendly service startup settings so remote debugging attach remains supported.
6. Start/restart the service and validate status before marking resource ready.
7. On stop/redeploy, stop service prior to binary copy to avoid locked files.

## Endpoint Discovery Contract
- Default endpoint discovery comes from the project launch settings associated with `TProject`.
- Discovery precedence:
    1. Endpoints explicitly configured in AppHost fluent calls (for example `WithHttpEndpoint` / `WithHttpsEndpoint`).
    2. Endpoints discovered from the project launch settings profile.
    3. Fail-fast if no usable app endpoint can be inferred and no explicit endpoint is provided.
- Discovered endpoints are treated exactly like explicit endpoints for forwarding, metadata publication, and dashboard visibility.
- Console logs must include endpoint discovery source (explicit vs launch settings) for troubleshooting.

### Stop / Restart
1. Dispose forwarders.
2. Stop remote process.
3. Unpublish endpoint metadata (or mark unavailable).
4. Restart = Stop then Start.

## Endpoint Metadata Contract
- Endpoint metadata is part of resource lifecycle.
- Publish on Start.
- Refresh on reconnect.
- If local port remaps, increment endpoint revision and republish.
- On Stop/Failed, clear or mark unavailable.

## Tunnel Resilience Contract
- Tunnel supervisor monitors app/vsdbg/OTLP forwarders.
- On drop, attempt bounded exponential-backoff reconnect.
- If reconnect succeeds, refresh availability metadata.
- If reconnect fails repeatedly, mark resource degraded/failed with diagnostics.

## Dashboard Observability Contract
Resource console must show ordered preparation steps and statuses:
- SSH connect/auth
- stale process detection/termination
- Debug artifact sync
- service install/update (worker mode)
- service start/restart status (worker mode)
- cert sync
- tunnel create/drop/reconnect attempts
- debugger preparation
- app start
- health check results

Each step should include:
- status (`started`, `succeeded`, `failed`)
- elapsed time
- remote host
- correlation/run id
- actionable error hints (no secret leakage)

In addition, dashboard should show:
- structured logs
- traces
- metrics

## Telemetry and Certificates
- ServiceDefaults/OpenTelemetry remains the baseline model.
- Remote app exports OTLP through forwarded path to local AppHost dashboard receiver.
- Sync AppHost dev TLS cert material to remote target for development trust model where required.

## TDD Execution Model
For each feature slice:
1. Write failing tests.
2. Implement minimum code to pass.
3. Refactor with all tests green.

## Milestone Roadmap
Milestones are intentionally small (target 1-2 days each) and should be completed in order.

### M0: Solution and Test Harness
- Create extension/test project skeleton and fake adapters (SSH/SCP/tunnel).
- Done when baseline test harness runs and CI test command is green.

### M1: Resource API and Required Validation
- Implement `AddRemoteProject<TProject>` and required fluent config validation.
- Done when fail-fast tests pass for missing/invalid required settings.

### M2: Parameter Resolution
- Resolve host, username, remote directory, and secrets from `ResourceParameter`.
- Done when parameter resolution tests pass and secret redaction is validated.

### M3: Debug Build Deployment
- Build Debug outputs, transfer artifacts, verify remote contents.
- Done when deployment tests confirm symbols/configs are copied.

### M4: Pre-Deploy Process Cleanup
- Detect and terminate stale remote process before copy.
- Done when idempotent redeploy tests pass.

### M5: Startup Resolution
- Implement start command defaults (Windows exe-first fallback, Linux dll).
- Done when startup resolution matrix tests pass.

### M6: Core Lifecycle
- Implement start/stop/restart state transitions.
- Done when lifecycle integration tests pass.

### M7: Endpoint Forwarding
- Forward app endpoints and handle preferred local port fallback.
- Done when forwarded local URL reachability tests pass.

### M8: Endpoint Metadata Lifecycle
- Publish/refresh/unpublish endpoint metadata with revisioning.
- Done when metadata lifecycle tests pass.

### M9: Tunnel Supervision
- Add heartbeat and bounded reconnect logic.
- Done when tunnel drop/recovery tests pass.

### M10: Console Operation Logging
- Emit ordered preparation/debug lifecycle events with timing and correlation.
- Done when console contract tests pass.

### M11: Worker Service Registration
- Implement service install/update lifecycle (systemd and Windows Service).
- Done when service registration idempotency tests pass on both platforms.

### M12: Worker Service Run-As Credentials
- Implement run-as credential configuration and fallback behavior.
- Done when run-as credential mapping tests pass.

### M13: Debugger and Telemetry End-to-End
- Validate vsdbg attach and OTLP flow via forwarded paths.
- Done when end-to-end debug + telemetry scenario passes.

### Milestone Completion Rules
- Every milestone starts with failing tests.
- No milestone is complete until unit and integration tests are green.
- If a bug is found, add a failing regression test in the active milestone before fixing.

### TDD Gates
1. Unit tests
2. Integration tests (SSH/SCP/tunnels via harness/fakes)
3. Resource/AppHost behavior tests (state + endpoint visibility)
4. Manual E2E on Linux and Windows remote hosts

## Ordered Test-First Backlog
1. Required `OSPlatform` validation and fail-fast diagnostics.
2. Generic `TProject` resolution and Debug output path validation.
3. Host, remote directory, and `WithCredential(userName, auth)` parameter resolution via `ResourceParameter` (secret handling, no console leakage).
4. Stale process cleanup before deploy.
5. Debug artifact transfer includes symbols/config files.
6. Endpoint forwarding and preferred-port fallback.
7. Endpoint metadata publish/refresh/unpublish and revisioning.
8. Tunnel supervision and reconnect policy.
9. Console step logging and reconnect diagnostics.
10. Start/Stop/Restart lifecycle behavior and failure propagation.
11. OTLP forwarding visibility in dashboard logs/traces/metrics.
12. Windows default startup artifact precedence (`.exe` first, `dotnet .dll` fallback).
13. Endpoint discovery from `TProject` launch settings with explicit endpoint override precedence.
14. Worker service install/update lifecycle (systemd + Windows Service) and idempotency.
15. Worker service debug-capable startup and debugger attach compatibility.
16. Worker service run-as credential configuration (`username` + secret `password`) with fallback to default service account context when omitted.

## Proposed Components
- `RemoteHostResource`
- `RemoteExecutionContext`
- `RemoteDeploymentService`
- `RemoteProcessManager`
- `RemoteServiceManager`
- `RemotePortForwardingService`
- `TunnelSupervisorService`
- `EndpointMetadataPublisher`
- `ResourceOperationLogger`

## Acceptance Criteria
- Remote resource appears in Aspire dashboard.
- `platform` (`OSPlatform`) is required and validated.
- Generic `TProject` is required and used to resolve Debug build artifacts.
- `Host` supports direct string or `ResourceParameter` input and resolves before SSH connection.
- `WithCredential(userName, auth)` supports username as direct string or `ResourceParameter` and resolves before SSH connection.
- `RemoteDirectory` supports direct string or `ResourceParameter` input and resolves before SSH connection.
- Core fluent methods (`WithHost`, `WithCredential`, `WithRemoteDirectory`) provide both `string` and `ResourceParameter` overloads where applicable.
- SSH password auth via `ResourceParameter` works without exposing secret values in console logs.
- Debug build artifacts (with symbols) deploy via SCP.
- With omitted `StartCommand` on Windows, startup uses `<AssemblyName>.exe` when present, otherwise `dotnet <AssemblyName>.dll`.
- Stale remote process is terminated before each deploy.
- Worker projects can be installed/updated as remote services on Linux and Windows.
- Worker service startup always supports debugger attachment while running under service management.
- Worker service run-as identity is configurable as credentials via `WithServiceRegistration(..., configure: ...)`.
- If run-as credentials are omitted, service uses default account context from registration operation.
- Forwarded local endpoint reaches remote web app.
- App endpoints are discoverable from `TProject` launch settings when not explicitly configured.
- Explicit AppHost endpoint configuration overrides discovered launch settings endpoints.
- Endpoint metadata lifecycle is accurate across start/reconnect/stop/failure.
- Console shows ordered preparation and troubleshooting events.
- Tunnel interruption triggers reconnect attempts.
- vsdbg attach works through forwarding.
- OTLP logs/traces/metrics appear in dashboard.
- Linux and Windows E2E scenarios pass.

## Out of Scope (MVP)
- Production hardening/compliance controls.
- WinRM transport.
- Containerized remote execution target.
- Multi-instance scaling.
- Persistent deployment version history.
