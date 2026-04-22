using System;

namespace Aspire.Hosting.RemoteDebugging.RemoteHost.Transport;

public sealed record RemoteDebuggerInstallationResult(bool IsInstalled, Exception? Exception = null);
