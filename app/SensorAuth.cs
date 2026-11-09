using System.Security.Cryptography;
using System.Text;

namespace App;

/// <summary>
/// A setup key unlocks one token per room or queue, derived from SENSOR_KEY. A
/// token copied from one phone can only report for that one space.
/// </summary>
public sealed class SensorAuth(string? setupKey)
{
    public bool Enabled => !string.IsNullOrWhiteSpace(setupKey);

    public bool IsSetupKey(string? candidate) =>
        Enabled && candidate is not null && FixedTimeEquals(candidate, setupKey!);

    public string TokenFor(string target)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(setupKey!), Encoding.UTF8.GetBytes(target));
        return Convert.ToHexStringLower(hash);
    }

    public bool IsValidToken(string target, string? token) =>
        Enabled && token is not null && FixedTimeEquals(token, TokenFor(target));

    // Hashing first makes the comparison independent of the candidate's length.
    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(a)),
            SHA256.HashData(Encoding.UTF8.GetBytes(b)));
}
