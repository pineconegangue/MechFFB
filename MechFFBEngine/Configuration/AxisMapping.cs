namespace MechFFBEngine.Configuration;

/// <summary>A direction as the pilot feels it on the stick.</summary>
public enum PushDirection
{
    AwayFromYou,
    TowardYou,
    Left,
    Right
}

/// <summary>
/// How the engine's canonical force direction is turned into this device's X/Y axes.
///
/// Canonical frame (what the engine emits, before mapping) is the original "Invert Axis ON"
/// output, verified physically on the author's stick:
///   (0,+1) = toward you    (0,-1) = away from you    (+1,0) = left    (-1,0) = right
/// i.e. angle 9000 = toward, 27000 = away, 0 = left, 18000 = right (centidegrees, cos/sin).
///
/// The mapping is applied as: swap X/Y first, then flip each device axis. Together the three
/// flags cover all 8 ways a 2-axis device can be oriented. The old "Invert Axis OFF" setting
/// is exactly FlipX + FlipY (a 180° turn).
/// </summary>
public sealed class AxisMapping
{
    public bool FlipX { get; set; }
    public bool FlipY { get; set; }
    public bool SwapXY { get; set; }

    [System.Text.Json.Serialization.JsonIgnore] // helper, not a setting
    public bool IsIdentity => !FlipX && !FlipY && !SwapXY;

    public AxisMapping Clone() => new() { FlipX = FlipX, FlipY = FlipY, SwapXY = SwapXY };

    /// <summary>Map a canonical direction vector onto the device's axes.</summary>
    public (double X, double Y) Apply(double x, double y)
    {
        if (SwapXY) (x, y) = (y, x);
        if (FlipX) x = -x;
        if (FlipY) y = -y;
        return (x, y);
    }

    /// <summary>Canonical vector for a felt direction (see class summary).</summary>
    public static (int X, int Y) CanonicalVector(PushDirection d) => d switch
    {
        PushDirection.AwayFromYou => (0, -1),
        PushDirection.TowardYou => (0, 1),
        PushDirection.Left => (1, 0),
        _ => (-1, 0) // Right
    };

    // Calibration plays these two canonical pushes RAW (no mapping) and asks where they went.
    public const int CalibrationAwayAngle = 27000;  // canonical "away from you"
    public const int CalibrationRightAngle = 18000; // canonical "right"

    /// <summary>
    /// Work out the mapping from a calibration: which way the stick actually moved for the raw
    /// canonical "away" push and the raw canonical "right" push. Returns null if both pushes
    /// went along the same line (the answers can't describe a 2-axis device).
    /// </summary>
    public static AxisMapping? FromCalibration(PushDirection awayPushFelt, PushDirection rightPushFelt)
    {
        // D = what the device does: raw vector -> felt direction (in the canonical frame).
        // Raw pushes were (0,-1) and (-1,0), so D's columns are D(e_x) = -right, D(e_y) = -away.
        var away = CanonicalVector(awayPushFelt);
        var right = CanonicalVector(rightPushFelt);
        int d00 = -right.X, d10 = -right.Y, d01 = -away.X, d11 = -away.Y;

        // Must be a signed permutation (one push vertical, the other horizontal)
        if (Math.Abs(d00 * d11 - d01 * d10) != 1)
            return null;

        // We need the mapping M with D(M(c)) = c, i.e. M = D^-1 = D^T for a signed permutation.
        int m00 = d00, m01 = d10, m10 = d01, m11 = d11;
        if (m00 != 0)
            return new AxisMapping { FlipX = m00 < 0, FlipY = m11 < 0 };
        return new AxisMapping { SwapXY = true, FlipX = m01 < 0, FlipY = m10 < 0 };
    }

    public override string ToString()
    {
        if (IsIdentity) return "no flips";
        var parts = new List<string>();
        if (FlipX) parts.Add("Flip X");
        if (FlipY) parts.Add("Flip Y");
        if (SwapXY) parts.Add("X/Y swapped");
        return string.Join(", ", parts);
    }
}
