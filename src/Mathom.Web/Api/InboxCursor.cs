using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace Mathom.Web.Api;

// Opaque keyset cursor over (CreatedAt, Id): base64url of "{UtcTicks}:{Id:N}".
public readonly record struct InboxCursor(DateTimeOffset CreatedAt, Guid Id)
{
    public string Encode() =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{CreatedAt.UtcTicks}:{Id:N}"));

    public static bool TryDecode(string? value, out InboxCursor cursor)
    {
        cursor = default;
        if (string.IsNullOrEmpty(value)) return false;
        try
        {
            var text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(value));
            var parts = text.Split(':');
            if (parts.Length != 2) return false;
            if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)) return false;
            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) return false;
            if (parts[1].Length != 32 || !Guid.TryParseExact(parts[1], "N", out var id)) return false;
            cursor = new InboxCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
