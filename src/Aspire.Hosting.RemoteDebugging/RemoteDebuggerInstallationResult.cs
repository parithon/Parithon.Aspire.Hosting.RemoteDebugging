using System;

namespace Aspire.Hosting.RemoteDebugging;

public sealed record RemoteDebuggerInstallationResult(bool IsInstalled, Exception? Exception = null);
