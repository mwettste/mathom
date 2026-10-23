namespace Mathom.Web.Auth;

public class DeviceAuthOptions
{
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan RefreshTokenIdleLifetime { get; set; } = TimeSpan.FromDays(90);
    public TimeSpan RefreshGraceWindow { get; set; } = TimeSpan.FromMinutes(2);
}
