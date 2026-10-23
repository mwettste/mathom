using System;
using System.Collections.Generic;

namespace Mathom.Web.Domain;

public enum DeviceKind { Phone, Watch, Other }

public enum DeviceTokenType { Access, Refresh }

// One signed-in native device. Tokens are opaque and stored only as SHA-256 hashes.
public class DeviceSession
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public DeviceKind Kind { get; set; }
    public Guid? ProvisionedBySessionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public List<DeviceToken> Tokens { get; set; } = [];
}

public class DeviceToken
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public DeviceSession Session { get; set; } = null!;
    public DeviceTokenType Type { get; set; }
    public string Hash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? SupersededAt { get; set; }
}
