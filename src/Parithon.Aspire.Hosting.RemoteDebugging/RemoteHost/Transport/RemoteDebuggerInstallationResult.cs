using System;

namespace Parithon.Aspire.Hosting.RemoteDebugging.RemoteHost.Transport;

public sealed record RemoteDebuggerInstallationResult(bool IsInstalled, Exception? Exception = null);
