namespace BudsTray.Buds;

public sealed record BatteryStatus(
    int? Left,
    int? Right,
    int? Case,
    bool LeftCharging,
    bool RightCharging,
    bool CaseCharging,
    DateTimeOffset UpdatedAt,
    bool LeftPresent = true,
    bool RightPresent = true,
    int? EarType = null)
{
    /// <summary>Lowest earbud percentage (case is ignored unless both buds are unknown).</summary>
    public int? LowestBattery
    {
        get
        {
            var values = new List<int>(2);
            if (Left is { } l) values.Add(l);
            if (Right is { } r) values.Add(r);
            return values.Count > 0 ? values.Min() : Case;
        }
    }

    /// <summary>At or below this level the battery is considered low (red).</summary>
    public const int LowThreshold = 15;

    /// <summary>
    /// The case reports 0 while it is not answering (the buds relay the case
    /// level only while the case is awake). Treat 0 as "not reported" so menus
    /// and tooltips do not show a misleading 0 %.
    /// </summary>
    public bool CaseReported => Case is > 0 and <= 100;

    /// <summary>True when exactly one bud is out of the case, i.e. single-bud mode.</summary>
    public bool IsSingleBud => LeftPresent != RightPresent;

    public bool AnyBudCharging => LeftCharging || RightCharging;

    public static BatteryStatus? FromFrame(SppFrame frame, int? previousEarType = null)
    {
        var p = frame.Payload;
        var now = DateTimeOffset.Now;

        if (frame.Id == MsgIds.StatusUpdated && p.Length >= 8)
        {
            // L, R, coupled, main, placement, case, charging. On the original
            // Buds payload[0] is the ear type, but on every newer model it is
            // the frame revision, so only an extended frame may set the model.
            return Create(p[1], p[2], p[6], p[7], now, earType: previousEarType);
        }

        if (frame.Id == MsgIds.ExtendedStatusUpdated && p.Length >= 8)
        {
            // revision, earType, L, R, coupled, main, placement, case, ...
            // On Buds FE the charging bitfield sits at offset 43.
            var charging = p.Length > 43 ? p[43] : (byte)0;

            // Placement is a nibble per bud: 0 disconnected, 1 wearing,
            // 2 idle (out of the case), 3 in the case, 4 case closed.
            var placement = p[6];
            var leftPlacement = (placement >> 4) & 0x0F;
            var rightPlacement = placement & 0x0F;

            return Create(p[2], p[3], p[7], charging, now,
                IsBudPresent(leftPlacement), IsBudPresent(rightPlacement), p[1]);
        }

        return null;
    }

    /// <summary>A bud is "present" only when it is out of the case (worn or idle).</summary>
    private static bool IsBudPresent(int placement) => placement is 1 or 2;

    /// <summary>
    /// Refreshes the earbud levels from a Buds FE <c>DEBUG_GET_ALL_DATA</c> reply.
    /// That reply carries the live SOC bytes but not the case level or charging
    /// flags, so those are carried over from the last status frame. The exact
    /// 108-byte length identifies the Buds FE layout; other models are ignored
    /// and keep using their pushed status frames.
    /// </summary>
    public static BatteryStatus? FromDebugData(SppFrame frame, BatteryStatus? previous)
    {
        var p = frame.Payload;
        if (frame.Id != MsgIds.DebugGetAllData || p.Length != 108) return null;

        var left = p[42];
        var right = p[48];
        if (left > 100 || right > 100) return null;

        // A zero SOC byte means "this earbud has not reported yet". The first
        // reply after connecting is only partially initialised (one record can
        // still be all zeros), so keep the last known level for that side
        // instead of flashing a false 0 %. A bud that really reaches 0 % powers
        // off and its last status frame remains the final word.
        if (left == 0 && right == 0) return null;

        var newLeft = left == 0 ? previous?.Left : left;
        var newRight = right == 0 ? previous?.Right : right;
        if (newLeft is null || newRight is null) return null;

        return new BatteryStatus(
            newLeft.Value,
            newRight.Value,
            previous?.Case,
            previous?.LeftCharging ?? false,
            previous?.RightCharging ?? false,
            previous?.CaseCharging ?? false,
            DateTimeOffset.Now,
            previous?.LeftPresent ?? true,
            previous?.RightPresent ?? true,
            previous?.EarType);
    }

    private static BatteryStatus Create(
        byte left,
        byte right,
        byte @case,
        byte charging,
        DateTimeOffset now,
        bool leftPresent = true,
        bool rightPresent = true,
        int? earType = null)
        => new(
            Normalize(left),
            Normalize(right),
            Normalize(@case),
            (@charging & 0x10) != 0,
            (@charging & 0x04) != 0,
            (@charging & 0x01) != 0,
            now,
            leftPresent,
            rightPresent,
            earType);

    private static int? Normalize(byte value) => value <= 100 ? value : null;

    public string ToTooltip(string deviceName)
    {
        // Only what is actually known: a bud that is in the case (or gone) and a
        // case that is not reporting must not clutter the tooltip with "--" or
        // a misleading 0 %.
        if (!LeftPresent && !RightPresent && !CaseReported)
            return $"{deviceName} · in the case{ChargingSuffix}";

        var parts = new List<string> { deviceName };

        if (LeftPresent) parts.Add($"L {Format(Left)}");
        if (RightPresent) parts.Add($"R {Format(Right)}");
        if (CaseReported) parts.Add($"Case {Format(Case)}");

        return string.Join(" · ", parts) + ChargingSuffix;
    }

    /// <summary>
    /// " charging" without a separator, so the tooltip reads
    /// "Case 30% charging" instead of "Case 30% · charging".
    /// </summary>
    private string ChargingSuffix =>
        (LeftPresent && LeftCharging) ||
        (RightPresent && RightCharging) ||
        (CaseReported && CaseCharging)
            ? " charging"
            : "";

    private static string Format(int? value) => value is { } v ? $"{v}%" : "--";
}
