namespace BudsTray.Buds;

/// <summary>Noise-control modes understood by the Samsung SPP protocol.</summary>
public enum NoiseControlMode : byte
{
    Off = 0,
    Anc = 1,
    Ambient = 2,
    Adaptive = 3
}

/// <summary>The sound modes a specific Galaxy Buds model can select.</summary>
[Flags]
public enum NoiseControlSupport
{
    None = 0,
    Off = 1 << 0,
    Anc = 1 << 1,
    Ambient = 1 << 2,
    Adaptive = 1 << 3
}

/// <summary>
/// Model capability matrix and payload parsing for the Buds' noise-control
/// messages. The ear type byte follows GalaxyBudsClient's model order:
/// 0 Buds, 1 Buds+, 2 Buds Live, 3 Buds Pro, 4 Buds2, 5 Buds2 Pro, 6 Buds FE,
/// 7 Buds Core, 8 Buds3, 9 Buds3 Pro, 10 Buds3 FE, 11 Buds4, 12 Buds4 Pro.
///
/// Capabilities:
///   Buds, Buds+                  Ambient + Off
///   Buds Live                    ANC + Off
///   Buds Pro … Buds3 FE          ANC + Ambient + Off
///   Buds3 Pro, Buds4, Buds4 Pro  ANC + Ambient + Adaptive + Off
///
/// Samsung lists Adaptive Noise Control for the Buds3 Pro, Buds4 and Buds4 Pro
/// only; the regular Buds3 and Buds3 FE have ANC and Ambient but no selectable
/// Adaptive mode.
/// </summary>
public static class NoiseControl
{
    public static NoiseControlSupport SupportFor(int? earType, string? deviceName = null)
    {
        var type = earType ?? EarTypeFromName(deviceName);
        if (type is not { } value) return NoiseControlSupport.None;

        return value switch
        {
            // Original Buds and Buds+ have Ambient Sound only.
            0 or 1 => NoiseControlSupport.Ambient | NoiseControlSupport.Off,

            // Buds Live has ANC, but no traditional Ambient Sound mode.
            2 => NoiseControlSupport.Anc | NoiseControlSupport.Off,

            // Buds Pro through Buds3 FE (and Buds Core).
            3 or 4 or 5 or 6 or 7 or 8 or 10 =>
                NoiseControlSupport.Anc | NoiseControlSupport.Ambient | NoiseControlSupport.Off,

            // Buds3 Pro, Buds4 and Buds4 Pro add the Adaptive mode.
            9 or 11 or 12 =>
                NoiseControlSupport.Anc | NoiseControlSupport.Ambient |
                NoiseControlSupport.Adaptive | NoiseControlSupport.Off,

            _ => NoiseControlSupport.None
        };
    }

    public static bool IsSupported(NoiseControlMode mode, int? earType, string? deviceName = null)
        => IsSupported(SupportFor(earType, deviceName), mode);

    public static bool IsSupported(NoiseControlSupport support, NoiseControlMode mode) => mode switch
    {
        NoiseControlMode.Anc => support.HasFlag(NoiseControlSupport.Anc),
        NoiseControlMode.Ambient => support.HasFlag(NoiseControlSupport.Ambient),
        NoiseControlMode.Adaptive => support.HasFlag(NoiseControlSupport.Adaptive),
        NoiseControlMode.Off => support.HasFlag(NoiseControlSupport.Off),
        _ => false
    };

    /// <summary>Buds Pro and newer take the mode as one byte in NOISE_CONTROLS (120).</summary>
    public static bool UsesModeByte(int? earType, string? deviceName = null)
    {
        var type = earType ?? EarTypeFromName(deviceName);
        return type is >= 3 and <= 12;
    }

    public static string Label(NoiseControlMode mode) => mode switch
    {
        NoiseControlMode.Anc => "ANC",
        NoiseControlMode.Ambient => "Ambient sound",
        NoiseControlMode.Adaptive => "Adaptive",
        _ => "Off"
    };

    /// <summary>
    /// Builds the message that selects <paramref name="mode"/>, or null when the
    /// model does not support it. Buds Pro and newer take the mode byte in
    /// NOISE_CONTROLS; older models toggle one boolean.
    /// </summary>
    public static (byte Id, byte[] Payload)? CommandFor(
        NoiseControlMode mode,
        int? earType,
        string? deviceName = null)
    {
        var support = SupportFor(earType, deviceName);
        if (!IsSupported(support, mode)) return null;

        if (UsesModeByte(earType, deviceName))
            return (MsgIds.NoiseControls, new[] { (byte)mode });

        if (support.HasFlag(NoiseControlSupport.Anc))
        {
            // Buds Live: ANC on/off only.
            return (MsgIds.SetNoiseReduction,
                new[] { (byte)(mode == NoiseControlMode.Anc ? 1 : 0) });
        }

        // Original Buds / Buds+: Ambient Sound on/off only.
        return (MsgIds.SetAmbientMode,
            new[] { (byte)(mode == NoiseControlMode.Ambient ? 1 : 0) });
    }

    /// <summary>
    /// Reads the current mode from an EXTENDED_STATUS_UPDATED frame. Each
    /// generation stores it differently: ambient flag for Buds/Buds+, ANC flag
    /// for Buds Live, and a mode byte for everything from Buds Pro onwards.
    /// </summary>
    public static NoiseControlMode? FromStatusFrame(SppFrame frame, int? earType)
    {
        if (frame.Id != MsgIds.ExtendedStatusUpdated) return null;

        var payload = frame.Payload;
        var type = earType ?? (payload.Length > 1 ? payload[1] : (int?)null);
        if (type is not { } value) return null;

        return value switch
        {
            0 => payload.Length > 7 ? BooleanMode(payload[7], NoiseControlMode.Ambient) : null,
            1 => payload.Length > 8 ? BooleanMode(payload[8], NoiseControlMode.Ambient) : null,
            2 => payload.Length > 12 ? BooleanMode(payload[12], NoiseControlMode.Anc) : null,
            >= 3 and <= 12 => payload.Length > 12 ? ParseMode(payload[12]) : null,
            _ => null
        };
    }

    /// <summary>Reads a mode from one of the asynchronous noise-control updates.</summary>
    public static NoiseControlMode? FromUpdateFrame(SppFrame frame)
    {
        var payload = frame.Payload;
        if (payload.Length == 0) return null;

        return frame.Id switch
        {
            MsgIds.NoiseControlsUpdated => ParseMode(payload[0]),
            MsgIds.AmbientModeUpdated => BooleanMode(payload[0], NoiseControlMode.Ambient),
            MsgIds.NoiseReductionModeUpdated => BooleanMode(payload[0], NoiseControlMode.Anc),

            // The buds acknowledge a NOISE_CONTROLS request with the applied mode.
            MsgIds.NoiseControls when frame.IsResponse => ParseMode(payload[0]),

            _ => null
        };
    }

    private static NoiseControlMode? ParseMode(byte value)
        => value <= (byte)NoiseControlMode.Adaptive ? (NoiseControlMode)value : null;

    private static NoiseControlMode? BooleanMode(byte value, NoiseControlMode on) => value switch
    {
        0 => NoiseControlMode.Off,
        1 => on,
        _ => null
    };

    /// <summary>Fallback when a model has not sent its ear type yet.</summary>
    private static int? EarTypeFromName(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return null;

        var name = deviceName.ToLowerInvariant();

        // Most specific names first, so "Buds3 Pro" is not read as "Buds3".
        if (name.Contains("buds4 pro")) return 12;
        if (name.Contains("buds4")) return 11;
        if (name.Contains("buds3 pro")) return 9;
        if (name.Contains("buds3 fe")) return 10;
        if (name.Contains("buds3")) return 8;
        if (name.Contains("buds2 pro")) return 5;
        if (name.Contains("buds2")) return 4;
        if (name.Contains("buds live")) return 2;
        if (name.Contains("buds pro")) return 3;
        if (name.Contains("buds core")) return 7;
        if (name.Contains("buds fe")) return 6;
        if (name.Contains("buds+") || name.Contains("buds plus")) return 1;
        if (name.Contains("buds")) return 0;

        return null;
    }
}
