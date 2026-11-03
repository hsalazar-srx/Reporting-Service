using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Reporting.Infrastructure.Domains.SupplyChain.BuyerView;

/// <summary>
/// Opens a Windows network connection to a share with explicit credentials, for as long as the
/// object lives, and closes it on dispose.
///
/// Why: SRXAPP03 (the legacy MSP server) is in the CORP domain, which has no trust with
/// SRXGLOBAL. Reporting-Service's app pool (ReportingAPI) runs as ApplicationPoolIdentity with
/// loadUserProfile=false (checked 2026-11-03), so on the network it is SRXGLOBAL\SRXWEBAPP1$,
/// which SRXAPP03 won't accept, and a cmdkey credential can't reach it. The service therefore
/// connects as the share's local read-only account (SRXAPP03\svc-msp-read) itself.
///
/// No user configured → no connection is made and the caller's own access is used (a developer
/// PC already connected to the share). The password comes from the secrets file, never the repo.
/// </summary>
public sealed class NetworkShareConnection : IDisposable
{
    private const int ResourceTypeDisk = 1;
    private const int ErrorSessionCredentialConflict = 1219;

    private readonly string _shareRoot;
    private bool _disposed;

    private NetworkShareConnection(string shareRoot) => _shareRoot = shareRoot;

    /// <summary>
    /// The share part of a UNC path: <c>\\server\share</c> from <c>\\server\share\sub\folder</c>
    /// (forward slashes accepted). The connection is made to the share, not to a folder in it.
    /// </summary>
    public static string ShareRoot(string uncPath)
    {
        var parts = uncPath.Replace('/', '\\').TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (!uncPath.Replace('/', '\\').StartsWith(@"\\", StringComparison.Ordinal) || parts.Length < 2)
            throw new ArgumentException($"'{uncPath}' is not a UNC path (\\\\server\\share).", nameof(uncPath));
        return $@"\\{parts[0]}\{parts[1]}";
    }

    /// <summary>
    /// Connects to the share holding <paramref name="folder"/> as <paramref name="user"/>, or does
    /// nothing when no user is configured. Throws <see cref="IOException"/> with the Windows error
    /// when the connection is refused, so the sync records a failed run.
    /// </summary>
    public static IDisposable Open(string folder, string? user, string? password)
    {
        if (string.IsNullOrWhiteSpace(user))
            return NoConnection.Instance;

        var shareRoot = ShareRoot(folder);
        var resource = new NetResource { Type = ResourceTypeDisk, RemoteName = shareRoot };
        var code = WNetAddConnection2(ref resource, password, user, 0);
        if (code == 0)
            return new NetworkShareConnection(shareRoot);

        var reason = code == ErrorSessionCredentialConflict
            ? "this process already has a connection to that server under other credentials"
            : new Win32Exception(code).Message;
        throw new IOException($"Could not connect to {shareRoot} as {user}: {reason} (Windows error {code}).");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        WNetCancelConnection2(_shareRoot, 0, true);
    }

    private sealed class NoConnection : IDisposable
    {
        public static readonly NoConnection Instance = new();
        public void Dispose() { }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NetResource netResource, string? password, string? username, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string name, int flags, bool force);
}
