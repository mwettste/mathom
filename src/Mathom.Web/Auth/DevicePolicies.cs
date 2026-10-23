namespace Mathom.Web.Auth;

public static class DevicePolicies
{
    // Only phone sessions may provision watches or manage devices.
    public const string PhoneSession = "PhoneSession";
}
