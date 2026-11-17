using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace KnowledgeMemory.Host.Security;

/// <summary>
/// Browser authentication: a short-lived one-time bootstrap code (issued to the local open-ui command)
/// is exchanged for an HttpOnly SameSite=Strict session cookie plus a CSRF token kept in page memory.
/// </summary>
public sealed class BrowserSessions
{
    public static readonly TimeSpan BootstrapTtl = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan SessionTtl = TimeSpan.FromHours(12);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _bootstrap = new();
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly TimeProvider _time;

    public BrowserSessions(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public sealed record Session(string Id, string Csrf, DateTimeOffset ExpiresAt);

    public string IssueBootstrap()
    {
        Sweep();
        var code = TokenStore.Base64Url(RandomNumberGenerator.GetBytes(32));
        _bootstrap[code] = _time.GetUtcNow() + BootstrapTtl;
        return code;
    }

    /// <summary>One-time: the code is removed whether or not it is still valid.</summary>
    public Session? Exchange(string? code)
    {
        if (string.IsNullOrEmpty(code) || code.Length > 100) return null;
        if (!_bootstrap.TryRemove(code, out var expires) || expires < _time.GetUtcNow()) return null;
        var s = new Session(
            TokenStore.Base64Url(RandomNumberGenerator.GetBytes(32)),
            TokenStore.Base64Url(RandomNumberGenerator.GetBytes(32)),
            _time.GetUtcNow() + SessionTtl);
        _sessions[s.Id] = s;
        return s;
    }

    public Session? Get(string? id)
    {
        if (string.IsNullOrEmpty(id) || !_sessions.TryGetValue(id, out var s)) return null;
        if (s.ExpiresAt < _time.GetUtcNow())
        {
            _sessions.TryRemove(id, out _);
            return null;
        }
        // Sliding expiration.
        var renewed = s with { ExpiresAt = _time.GetUtcNow() + SessionTtl };
        _sessions[id] = renewed;
        return renewed;
    }

    public void Remove(string? id)
    {
        if (id is not null) _sessions.TryRemove(id, out _);
    }

    private void Sweep()
    {
        var now = _time.GetUtcNow();
        foreach (var kv in _bootstrap)
            if (kv.Value < now) _bootstrap.TryRemove(kv.Key, out _);
        foreach (var kv in _sessions)
            if (kv.Value.ExpiresAt < now) _sessions.TryRemove(kv.Key, out _);
    }
}
