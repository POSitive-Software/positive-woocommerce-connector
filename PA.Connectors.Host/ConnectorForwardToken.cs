using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace PA.Connectors.Host;

/// <summary>
/// The secret PA.Server puts in <see cref="HeaderName"/> on every /connectors and /hooks forward, so
/// the host can tell the server's requests from any other local process on :5220: only a request
/// carrying it may state a portal user (<c>X-PA-Portal-*</c>). Without it a request is anonymous —
/// the pages' API answers 401; webhooks still authenticate by their HMAC.
/// <para>
/// Kept in <c>%ProgramData%\POSitiveAnywhereV2\connectors\forward.token</c>, made by whichever of the
/// two services starts first (both run as LocalSystem). The file is written with a protected ACL —
/// SYSTEM and Administrators only (plus the creating user when that is not SYSTEM, i.e. a dev run
/// from a console) — and a file found with any other owner or reader is not trusted and replaced.
/// Re-read when the file changes (checked at most every <see cref="RecheckAfter"/>).
/// </para>
/// PA.Server holds an identical copy of this class: the two
/// products share no assembly.
/// </summary>
public sealed class ConnectorForwardToken
{
    public const string HeaderName = "X-PA-Forward-Token";
    public const string PathSettingKey = "Connectors:ForwardTokenPath";
    public static readonly TimeSpan RecheckAfter = TimeSpan.FromSeconds(30);

    public static string DefaultPath => Path.Combine(PA.Connectors.Core.ConnectorPaths.Folder, "forward.token");

    private readonly string? _path;
    private readonly ILogger? _logger;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private string? _value;
    private DateTime _fileTime;
    private DateTimeOffset _nextCheck = DateTimeOffset.MinValue;

    public ConnectorForwardToken(string path, ILogger? logger = null, TimeProvider? time = null)
    {
        _path = path;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    private ConnectorForwardToken(string value)
    {
        _value = value;
        _time = TimeProvider.System;
    }

    /// <summary>A fixed token, no file (tests).</summary>
    public static ConnectorForwardToken Fixed(string value) => new(value);

    /// <summary>The token, or null when it cannot be read or made (logged; forwards are then anonymous).</summary>
    public string? Current()
    {
        if (_path is null)
            return _value;
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            if (now < _nextCheck)
                return _value;
            _nextCheck = now + RecheckAfter;
            try
            {
                if (_value is not null && File.Exists(_path) && File.GetLastWriteTimeUtc(_path) == _fileTime)
                    return _value;
                _value = LoadOrCreate(_path, _logger);
                _fileTime = File.GetLastWriteTimeUtc(_path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _logger?.LogError("The connector forward token at {Path} could not be read or created ({Error}); " +
                                  "connector pages forwarded by the server are treated as anonymous until it can.", _path, ex.Message);
                _value = null;
            }
            return _value;
        }
    }

    /// <summary>Constant-time comparison of a presented header value with the token.</summary>
    public bool Matches(string? presented)
    {
        if (string.IsNullOrEmpty(presented))
            return false;
        var current = Current();
        if (current is null)
            return false;
        var a = Encoding.UTF8.GetBytes(presented);
        var b = Encoding.UTF8.GetBytes(current);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>Reads the token file, or writes a new token when it is missing, malformed or not trusted.</summary>
    internal static string LoadOrCreate(string path, ILogger? logger = null)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (File.Exists(path))
            {
                if (OperatingSystem.IsWindows() && !IsTrusted(path))
                {
                    logger?.LogWarning("The connector forward token at {Path} has an owner or reader other than SYSTEM / Administrators; replacing it.", path);
                    return Create(path, overwrite: true);
                }
                var text = File.ReadAllText(path).Trim();
                if (IsWellFormed(text))
                    return text;
                logger?.LogWarning("The connector forward token at {Path} is malformed; replacing it.", path);
                return Create(path, overwrite: true);
            }
            try
            {
                return Create(path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                // The other service made it first: read theirs.
            }
        }
        throw new IOException($"Could not read or create {path}.");
    }

    internal static bool IsWellFormed(string text) =>
        text.Length is >= 32 and <= 200 && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string Create(string path, bool overwrite)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = Encoding.ASCII.GetBytes(token);
            if (OperatingSystem.IsWindows())
            {
                using var stream = new FileInfo(temp).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096,
                    FileOptions.None, RestrictedSecurity());
                stream.Write(bytes);
            }
            else
            {
                File.WriteAllBytes(temp, bytes);
            }
            File.Move(temp, path, overwrite);
            return token;
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static FileSecurity RestrictedSecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in TrustedSids())
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    /// <summary>SYSTEM, Administrators, and the current user (SYSTEM again for the services).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static HashSet<SecurityIdentifier> TrustedSids()
    {
        var sids = new HashSet<SecurityIdentifier>
        {
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
        };
        if (WindowsIdentity.GetCurrent().User is { } current)
            sids.Add(current);
        return sids;
    }

    /// <summary>Owner and every allowed reader is SYSTEM, Administrators or this process's user.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static bool IsTrusted(string path)
    {
        var trusted = TrustedSids();
        var security = new FileInfo(path).GetAccessControl();
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !trusted.Contains(owner))
            return false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow
                && (rule.IdentityReference is not SecurityIdentifier sid || !trusted.Contains(sid)))
                return false;
        }
        return true;
    }
}
