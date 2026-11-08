using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using BudsTray;
using BudsTray.Buds;

var failures = 0;

void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail is null ? "" : $"  ({detail})")}");
    if (!ok) failures++;
}

bool CommandIs((byte Id, byte[] Payload)? command, byte id, params byte[] payload)
    => command is { } message && message.Id == id && message.Payload.SequenceEqual(payload);

// 1. CRC-16/XMODEM test vector from the protocol notes.
var crcInput = Convert.FromHexString("6102004B5F01000000010500020013");
var crc = SppProtocol.Crc16(crcInput);
Check("CRC-16/XMODEM vector", crc == 0xF30F, $"0x{crc:X4}");

// 2. Decode a real Galaxy Buds FE extended status frame.
var raw = Convert.FromHexString(
    "FD31106102063A590100110000008F3300004A014A01030004650001001000000101110300000000E80300009D0E1D005000C2CFDD");

var parser = new SppProtocol.StreamParser();
var frames = parser.Feed(raw);
Check("frame count", frames.Count == 1, frames.Count.ToString());
Check("message id is EXTENDED_STATUS_UPDATED", frames.Count == 1 && frames[0].Id == MsgIds.ExtendedStatusUpdated);
Check("payload length", frames.Count == 1 && frames[0].Payload.Length == 46,
    frames.Count == 1 ? frames[0].Payload.Length.ToString() : "n/a");

var status = frames.Count == 1 ? BatteryStatus.FromFrame(frames[0]) : null;
Check("battery L=58 R=89 case=0",
    status is { Left: 58, Right: 89, Case: 0 },
    status?.ToTooltip("Buds") ?? "null");
Check("charging flags are off",
    status is { LeftCharging: false, RightCharging: false, CaseCharging: false });
Check("lowest battery is 58", status?.LowestBattery == 58, status?.LowestBattery?.ToString());
Check("real frame reports both buds present", status is { LeftPresent: true, RightPresent: true, IsSingleBud: false });

// Placement nibbles: 1 = wearing, 2 = idle, 3 = in case, 0 = disconnected.
var placementPayload = (byte[])frames[0].Payload.Clone();
placementPayload[6] = 0x13; // left wearing, right in the case
var leftOnly = BatteryStatus.FromFrame(new SppFrame(MsgIds.ExtendedStatusUpdated, placementPayload, false));
Check("placement 0x13 is left-only", leftOnly is { LeftPresent: true, RightPresent: false, IsSingleBud: true });

placementPayload[6] = 0x32; // left in the case, right idle (out of the case)
var rightOnly = BatteryStatus.FromFrame(new SppFrame(MsgIds.ExtendedStatusUpdated, placementPayload, false));
Check("placement 0x32 is right-only", rightOnly is { LeftPresent: false, RightPresent: true, IsSingleBud: true });

placementPayload[6] = 0x21; // left idle, right wearing: both are out of the case
var idleAndWearing = BatteryStatus.FromFrame(new SppFrame(MsgIds.ExtendedStatusUpdated, placementPayload, false));
Check("placement 0x21 reports both present", idleAndWearing is { LeftPresent: true, RightPresent: true });

placementPayload[6] = 0x33; // both in the case
var bothInCase = BatteryStatus.FromFrame(new SppFrame(MsgIds.ExtendedStatusUpdated, placementPayload, false));
Check("placement 0x33 is not single-bud", bothInCase is { LeftPresent: false, RightPresent: false, IsSingleBud: false });

// The ear type byte identifies the model, which selects the supported modes.
Check("extended frame carries ear type", status?.EarType == 6, status?.EarType?.ToString());

// STATUS_UPDATED payload[0] is a revision on newer models, not the ear type.
var legacyStatusPayload = new byte[8];
legacyStatusPayload[0] = 9;
legacyStatusPayload[1] = 50;
legacyStatusPayload[2] = 60;
legacyStatusPayload[6] = 20;
var statusOnly = BatteryStatus.FromFrame(
    new SppFrame(MsgIds.StatusUpdated, legacyStatusPayload, false), previousEarType: 6);
Check("STATUS_UPDATED keeps the ear type from the extended frame", statusOnly?.EarType == 6,
    statusOnly?.EarType?.ToString());
Check("STATUS_UPDATED does not guess an unknown model",
    BatteryStatus.FromFrame(new SppFrame(MsgIds.StatusUpdated, legacyStatusPayload, false))?.EarType is null);

// 2b. The original Galaxy Buds use a different SPP UUID and a legacy frame
// layout; Buds+ and Buds Live/Pro use the standard UUID with modern frames.
Check("legacy SPP UUID is a candidate",
    BudsClient.ServiceCandidates.Any(c =>
        c.Uuid == BudsClient.SppLegacyServiceUuid && c.Transport == SppTransport.Legacy));
Check("standard SPP UUID uses modern frames",
    BudsClient.ServiceCandidates.Any(c =>
        c.Uuid == BudsClient.SppStandardServiceUuid && c.Transport == SppTransport.Modern));

var legacyManagerInfo = SppProtocol.Encode(
    MsgIds.ManagerInfo, new byte[] { 1, 1, 34 }, transport: SppTransport.Legacy);
Check("legacy frame uses FE/EE markers",
    legacyManagerInfo[0] == SppProtocol.LegacySom && legacyManagerInfo[^1] == SppProtocol.LegacyEom);
Check("legacy frame has type and size bytes",
    legacyManagerInfo[1] == 0 && legacyManagerInfo[2] == 6,
    $"{legacyManagerInfo[1]}/{legacyManagerInfo[2]}");
var legacyRoundTrip = new SppProtocol.StreamParser(SppTransport.Legacy).Feed(legacyManagerInfo);
Check("legacy frame round-trip",
    legacyRoundTrip.Count == 1 &&
    legacyRoundTrip[0].Transport == SppTransport.Legacy &&
    legacyRoundTrip[0].Payload.SequenceEqual(new byte[] { 1, 1, 34 }));

// Original Buds status: earType, L, R, coupled, main, wear state.
var budsStatus = BatteryStatus.FromFrame(new SppFrame(
    MsgIds.StatusUpdated, new byte[] { 0, 77, 66, 1, 0, 17 }, false, SppTransport.Legacy));
Check("original Buds battery", budsStatus is { Left: 77, Right: 66, Case: 0, EarType: 0 });
Check("original Buds has no case reading", budsStatus?.CaseReported == false);
Check("original Buds is not single-bud", budsStatus is { IsSingleBud: false });

// Original Buds extended frame: revision, earType, L, R, ... no case.
var budsExtended = new byte[13];
budsExtended[0] = 3;
budsExtended[1] = 0;
budsExtended[2] = 80;
budsExtended[3] = 70;
budsExtended[7] = 1;
var budsExtendedStatus = BatteryStatus.FromFrame(new SppFrame(
    MsgIds.ExtendedStatusUpdated, budsExtended, false, SppTransport.Legacy));
Check("original Buds extended battery",
    budsExtendedStatus is { Left: 80, Right: 70, Case: 0, EarType: 0 });

// Buds+ reports the case level but has no charging flags.
var plusExtended = new byte[24];
plusExtended[0] = 13;
plusExtended[1] = 1;
plusExtended[2] = 55;
plusExtended[3] = 65;
plusExtended[6] = 0x11;
plusExtended[7] = 42;
var plusStatus = BatteryStatus.FromFrame(
    new SppFrame(MsgIds.ExtendedStatusUpdated, plusExtended, false));
Check("Buds+ case is read", plusStatus is { Case: 42, Left: 55, Right: 65 });
Check("Buds+ has no charging flags", plusStatus is { LeftCharging: false, CaseCharging: false });
Check("Buds+ placement is read", plusStatus is { LeftPresent: true, RightPresent: true });

// A Buds+ STATUS frame is only 7 bytes and used to be ignored completely.
var plusOnly = BatteryStatus.FromFrame(
    new SppFrame(MsgIds.StatusUpdated, new byte[] { 13, 55, 65, 1, 0, 0x11, 42 }, false),
    previousEarType: 1);
Check("Buds+ status frame is parsed", plusOnly is { Left: 55, Right: 65, Case: 42 });

// The charging bitfield moved between generations.
byte[] ExtendedWith(int earType, int revision, int offset, byte value, int length)
{
    var payload = new byte[length];
    payload[0] = (byte)revision;
    payload[1] = (byte)earType;
    payload[2] = 90;
    payload[3] = 80;
    payload[6] = 0x11;
    payload[7] = 50;
    payload[offset] = value;
    return payload;
}

Check("Buds2 charging is read at offset 36",
    BatteryStatus.FromFrame(new SppFrame(MsgIds.ExtendedStatusUpdated,
        ExtendedWith(4, 10, 36, 0x01, 40), false)) is { CaseCharging: true });
Check("Buds2 charging is ignored before revision 10",
    BatteryStatus.FromFrame(new SppFrame(MsgIds.ExtendedStatusUpdated,
        ExtendedWith(4, 9, 36, 0x01, 40), false)) is { CaseCharging: false });
Check("Buds2 Pro charging is read at offset 43",
    BatteryStatus.FromFrame(new SppFrame(MsgIds.ExtendedStatusUpdated,
        ExtendedWith(5, 11, 43, 0x04, 46), false)) is { RightCharging: true });
Check("Buds3 charging is read at offset 42",
    BatteryStatus.FromFrame(new SppFrame(MsgIds.ExtendedStatusUpdated,
        ExtendedWith(8, 2, 42, 0x10, 46), false)) is { LeftCharging: true });
Check("Buds FE charging is read at offset 43",
    BatteryStatus.FromFrame(new SppFrame(MsgIds.ExtendedStatusUpdated,
        ExtendedWith(6, 1, 43, 0x10, 46), false)) is { LeftCharging: true });

// 3. Encode ManagerInfo and round-trip it.
var managerInfo = SppProtocol.Encode(MsgIds.ManagerInfo, new byte[] { 1, 1, 34 });
Check("ManagerInfo frame length", managerInfo.Length == 10, managerInfo.Length.ToString());
var roundTrip = new SppProtocol.StreamParser().Feed(managerInfo);
Check("ManagerInfo round-trip",
    roundTrip.Count == 1 && roundTrip[0].Id == MsgIds.ManagerInfo &&
    roundTrip[0].Payload.SequenceEqual(new byte[] { 1, 1, 34 }));

// 4. Byte-by-byte delivery must parse the same frame.
var dripParser = new SppProtocol.StreamParser();
var dripFrames = new List<SppFrame>();
foreach (var b in raw) dripFrames.AddRange(dripParser.Feed(new[] { b }));
Check("byte-by-byte parsing", dripFrames.Count == 1 && dripFrames[0].Id == MsgIds.ExtendedStatusUpdated);

// 5. Corrupt CRC must be rejected.
var corrupt = (byte[])raw.Clone();
corrupt[^3] ^= 0xFF;
Check("corrupt CRC rejected", new SppProtocol.StreamParser().Feed(corrupt).Count == 0);

// 6. Garbage before the SOM must be skipped.
var noisy = new byte[] { 0x00, 0xFF, 0x12 }.Concat(raw).ToArray();
var noisyFrames = new SppProtocol.StreamParser().Feed(noisy);
Check("garbage resync", noisyFrames.Count == 1 && noisyFrames[0].Id == MsgIds.ExtendedStatusUpdated);

// 7. DEBUG_GET_ALL_DATA reply from a real Buds FE: live SOC at 42/48, case carried over.
var debugPayload = Convert.FromHexString(
    "000201A501B054104F2FE3C754104F2FBE6A00000000000000000000000080060000251400000E0104016300000000006400000000002320F71F0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000");
var previous = new BatteryStatus(50, 60, 24, false, true, false, DateTimeOffset.Now);
var debugStatus = BatteryStatus.FromDebugData(
    new SppFrame(MsgIds.DebugGetAllData, debugPayload, false), previous);
Check("debug data refreshes L=99 R=100", debugStatus is { Left: 99, Right: 100 },
    debugStatus?.ToTooltip("Buds") ?? "null");
Check("debug data keeps case and charging", debugStatus is { Case: 24, RightCharging: true });
Check("debug data ignores short payloads",
    BatteryStatus.FromDebugData(new SppFrame(MsgIds.DebugGetAllData, new byte[50], false), previous) is null);
Check("debug data ignores zero placeholder",
    BatteryStatus.FromDebugData(new SppFrame(MsgIds.DebugGetAllData, new byte[108], false), previous) is null);

// The first reply after connecting can be only partially initialised: the left
// record is still zero while the right one already reports. Keep the last known
// left level instead of flashing 0 %.
var partialPayload = Convert.FromHexString(
    "000201A501B054104F2FE3C754104F2FBE6A00000000000000000000000000000000450F00000000FA00000000000000640000000000000025200000000000000200000000000000000000000000000000000000000000000000000000000000000000000000000000000000");
var partialStatus = BatteryStatus.FromDebugData(
    new SppFrame(MsgIds.DebugGetAllData, partialPayload, false), previous);
Check("partial debug reply keeps previous left", partialStatus is { Left: 50, Right: 100 },
    partialStatus?.ToTooltip("Buds") ?? "null");
Check("partial debug reply without history is rejected",
    BatteryStatus.FromDebugData(new SppFrame(MsgIds.DebugGetAllData, partialPayload, false), null) is null);

// Presence comes from the last status frame; the debug reply only refreshes SOC.
var singlePrevious = new BatteryStatus(
    50, 60, 24, false, false, false, DateTimeOffset.Now, LeftPresent: true, RightPresent: false);
var singleDebug = BatteryStatus.FromDebugData(
    new SppFrame(MsgIds.DebugGetAllData, debugPayload, false), singlePrevious);
Check("debug data keeps placement", singleDebug is { LeftPresent: true, RightPresent: false });

// 8. Configs written by older builds used numeric enum values.
var legacyConfig = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(
    "{\"DeviceAddress\":\"54104F2FBE6A\",\"Theme\":2}",
    new System.Text.Json.JsonSerializerOptions { Converters = { new AppThemeConverter() } });
Check("legacy numeric theme loads", legacyConfig is { Theme: AppTheme.Dark, DeviceAddress: "54104F2FBE6A" });

var modelConfig = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(
    "{\"LastEarType\":9}",
    new System.Text.Json.JsonSerializerOptions { Converters = { new AppThemeConverter() } });
Check("persisted ear type loads", modelConfig?.LastEarType == 9);

// Taking the channel back from Samsung's app is on by default and can be
// disabled by hand; QuickControls must be stopped before BudsManager because it
// is the process that relaunches it.
var defaultConfig = System.Text.Json.JsonSerializer.Deserialize<AppConfig>("{}");
Check("Galaxy Buds app takeover is on by default", defaultConfig?.TakeOverFromGalaxyBudsApp == true);
var optOutConfig = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(
    "{\"TakeOverFromGalaxyBudsApp\":false}");
Check("Galaxy Buds app takeover can be disabled", optOutConfig?.TakeOverFromGalaxyBudsApp == false);
Check("takeover stops QuickControls before BudsManager",
    GalaxyBudsApp.ProcessNames.SequenceEqual(new[] { "QuickControls", "BudsManager" }));

// 9. The disconnected glyph carries a slash, so it cannot be mistaken for a
// real 0 % battery (which is just the empty shell).
using (var disconnectedIcon = TrayIconRenderer.Render(
           null, null, false, false, connected: false, showPercentage: false,
           lightTheme: false, size: 16).ToBitmap())
using (var emptyIcon = TrayIconRenderer.Render(
           0, 0, false, false, connected: true, showPercentage: false,
           lightTheme: false, size: 16).ToBitmap())
{
    var differs = false;
    for (var y = 0; y < 16 && !differs; y++)
    for (var x = 0; x < 16 && !differs; x++)
        differs = disconnectedIcon.GetPixel(x, y) != emptyIcon.GetPixel(x, y);

    Check("disconnected icon differs from 0 %", differs);
}

// 10. The case only reports a level while it is awake; 0 means "unknown".
Check("case 0 is not reported",
    !new BatteryStatus(50, 60, 0, false, false, false, DateTimeOffset.Now).CaseReported);
Check("case 24 is reported",
    new BatteryStatus(50, 60, 24, false, false, false, DateTimeOffset.Now).CaseReported);

// 11. The tooltip must only mention what is actually known.
var singleTooltip = new BatteryStatus(
    50, 60, 0, false, false, false, DateTimeOffset.Now,
    LeftPresent: true, RightPresent: false).ToTooltip("Buds");
Check("tooltip hides the bud that is not connected",
    singleTooltip.Contains("L 50%") && !singleTooltip.Contains("R ") && !singleTooltip.Contains("Case"),
    singleTooltip);

var reportedTooltip = new BatteryStatus(
    50, 60, 24, false, false, false, DateTimeOffset.Now).ToTooltip("Buds");
Check("tooltip shows a reported case", reportedTooltip.Contains("Case 24%"), reportedTooltip);

var unreportedTooltip = new BatteryStatus(
    50, 60, 0, false, false, false, DateTimeOffset.Now).ToTooltip("Buds");
Check("tooltip hides an unreported case", !unreportedTooltip.Contains("Case"), unreportedTooltip);

var inCaseTooltip = new BatteryStatus(
    50, 60, 0, false, false, false, DateTimeOffset.Now,
    LeftPresent: false, RightPresent: false).ToTooltip("Buds");
Check("tooltip says the buds are in the case",
    inCaseTooltip.Contains("in the case") && !inCaseTooltip.Contains("L ") && !inCaseTooltip.Contains("Case"),
    inCaseTooltip);

// 12. Fields in the hover text are separated by middle dots.
Check("tooltip uses interpuncts",
    reportedTooltip == "Buds · L 50% · R 60% · Case 24%",
    reportedTooltip);
Check("in-case tooltip uses an interpunct",
    inCaseTooltip == "Buds · in the case", inCaseTooltip);

// Charging is appended after a space, not a middle dot: "Case 30% charging".
var caseChargingTooltip = new BatteryStatus(
    50, 60, 30, false, false, true, DateTimeOffset.Now).ToTooltip("Buds");
Check("case charging tooltip has no dot before charging",
    caseChargingTooltip == "Buds · L 50% · R 60% · Case 30% charging",
    caseChargingTooltip);

var inCaseChargingTooltip = new BatteryStatus(
    50, 60, 30, false, false, true, DateTimeOffset.Now,
    LeftPresent: false, RightPresent: false).ToTooltip("Buds");
Check("in-case charging tooltip has no dot before charging",
    inCaseChargingTooltip == "Buds · Case 30% charging",
    inCaseChargingTooltip);

// 13. Sound modes depend on the model.
var ambientOnly = NoiseControlSupport.Ambient | NoiseControlSupport.Off;
var ancOnly = NoiseControlSupport.Anc | NoiseControlSupport.Off;
var ancAmbient = NoiseControlSupport.Anc | NoiseControlSupport.Ambient | NoiseControlSupport.Off;
var adaptive = ancAmbient | NoiseControlSupport.Adaptive;

Check("Buds support Ambient + Off", NoiseControl.SupportFor(0) == ambientOnly);
Check("Buds+ support Ambient + Off", NoiseControl.SupportFor(1) == ambientOnly);
Check("Buds Live supports ANC + Off", NoiseControl.SupportFor(2) == ancOnly);

foreach (var earType in new[] { 3, 4, 5, 6, 7, 8, 10 })
    Check($"ear type {earType} supports ANC + Ambient + Off",
        NoiseControl.SupportFor(earType) == ancAmbient);

foreach (var earType in new[] { 9, 11, 12 })
    Check($"ear type {earType} supports ANC + Ambient + Adaptive + Off",
        NoiseControl.SupportFor(earType) == adaptive);

Check("Buds3 has no Adaptive mode",
    !NoiseControl.IsSupported(NoiseControlMode.Adaptive, 8));
Check("Buds3 FE has no Adaptive mode",
    !NoiseControl.IsSupported(NoiseControlMode.Adaptive, 10));
Check("Buds3 Pro has Adaptive mode",
    NoiseControl.IsSupported(NoiseControlMode.Adaptive, 9));
Check("Buds4 has Adaptive mode",
    NoiseControl.IsSupported(NoiseControlMode.Adaptive, 11));
Check("Buds4 Pro has Adaptive mode",
    NoiseControl.IsSupported(NoiseControlMode.Adaptive, 12));
Check("Buds Live has no Ambient mode",
    !NoiseControl.IsSupported(NoiseControlMode.Ambient, 2));
Check("unknown model has no sound modes",
    NoiseControl.SupportFor(null, "TOYOTA Corolla") == NoiseControlSupport.None);
Check("model name fallback recognises Buds3 Pro",
    NoiseControl.IsSupported(NoiseControlMode.Adaptive, null, "Galaxy Buds3 Pro"));
Check("model name fallback recognises Buds4 Pro",
    NoiseControl.IsSupported(NoiseControlMode.Adaptive, null, "Galaxy Buds4 Pro"));

Check("ANC label", NoiseControl.Label(NoiseControlMode.Anc) == "ANC");
Check("Ambient label", NoiseControl.Label(NoiseControlMode.Ambient) == "Ambient sound");
Check("Adaptive label", NoiseControl.Label(NoiseControlMode.Adaptive) == "Adaptive");
Check("Off label", NoiseControl.Label(NoiseControlMode.Off) == "Off");

// Each generation gets the command it understands.
Check("Buds FE ANC uses NOISE_CONTROLS",
    CommandIs(NoiseControl.CommandFor(NoiseControlMode.Anc, 6), MsgIds.NoiseControls, 1));
Check("Buds3 Pro Adaptive uses NOISE_CONTROLS",
    CommandIs(NoiseControl.CommandFor(NoiseControlMode.Adaptive, 9), MsgIds.NoiseControls, 3));
Check("Buds3 Off uses NOISE_CONTROLS",
    CommandIs(NoiseControl.CommandFor(NoiseControlMode.Off, 8), MsgIds.NoiseControls, 0));
Check("Buds FE Adaptive is rejected",
    NoiseControl.CommandFor(NoiseControlMode.Adaptive, 6) is null);
Check("Buds Live ANC uses SET_NOISE_REDUCTION",
    CommandIs(NoiseControl.CommandFor(NoiseControlMode.Anc, 2), MsgIds.SetNoiseReduction, 1));
Check("Buds Live Off uses SET_NOISE_REDUCTION",
    CommandIs(NoiseControl.CommandFor(NoiseControlMode.Off, 2), MsgIds.SetNoiseReduction, 0));
Check("Buds Live Ambient is rejected",
    NoiseControl.CommandFor(NoiseControlMode.Ambient, 2) is null);
Check("Buds Ambient uses SET_AMBIENT_MODE",
    CommandIs(NoiseControl.CommandFor(NoiseControlMode.Ambient, 0), MsgIds.SetAmbientMode, 1));
Check("Buds+ Off uses SET_AMBIENT_MODE",
    CommandIs(NoiseControl.CommandFor(NoiseControlMode.Off, 1), MsgIds.SetAmbientMode, 0));
Check("Buds ANC is rejected",
    NoiseControl.CommandFor(NoiseControlMode.Anc, 0) is null);

using (var offlineClient = new BudsClient(new AppConfig()))
    Check("mode change while disconnected is ignored",
        !await offlineClient.SetNoiseControlAsync(NoiseControlMode.Anc));

// While the official Galaxy Buds window is open, BudsTray releases the channel
// and must not reconnect until that window is closed again.
using (var pausedClient = new BudsClient(new AppConfig()))
{
    await pausedClient.PauseAsync();
    pausedClient.Start();
    await Task.Delay(100);
    Check("paused client does not connect",
        pausedClient.PausedForGalaxyBudsApp &&
        pausedClient.State == ConnectionState.Disconnected);
}

// 14. The extended status frame reports the current mode in different places.
var modernNoise = new byte[46];
modernNoise[12] = (byte)NoiseControlMode.Adaptive;
Check("Buds3 Pro status reads Adaptive",
    NoiseControl.FromStatusFrame(
        new SppFrame(MsgIds.ExtendedStatusUpdated, modernNoise, false), 9) == NoiseControlMode.Adaptive);

modernNoise[12] = (byte)NoiseControlMode.Off;
Check("Buds FE status reads Off",
    NoiseControl.FromStatusFrame(
        new SppFrame(MsgIds.ExtendedStatusUpdated, modernNoise, false), 6) == NoiseControlMode.Off);

var liveNoise = new byte[46];
liveNoise[12] = 1;
Check("Buds Live status reads ANC",
    NoiseControl.FromStatusFrame(
        new SppFrame(MsgIds.ExtendedStatusUpdated, liveNoise, false), 2) == NoiseControlMode.Anc);

var budsNoise = new byte[20];
budsNoise[7] = 1;
Check("Buds status reads Ambient",
    NoiseControl.FromStatusFrame(
        new SppFrame(MsgIds.ExtendedStatusUpdated, budsNoise, false), 0) == NoiseControlMode.Ambient);

var plusNoise = new byte[20];
plusNoise[8] = 1;
Check("Buds+ status reads Ambient",
    NoiseControl.FromStatusFrame(
        new SppFrame(MsgIds.ExtendedStatusUpdated, plusNoise, false), 1) == NoiseControlMode.Ambient);

Check("NOISE_CONTROLS_UPDATE reads the mode",
    NoiseControl.FromUpdateFrame(
        new SppFrame(MsgIds.NoiseControlsUpdated, new byte[] { (byte)NoiseControlMode.Ambient }, false))
    == NoiseControlMode.Ambient);
Check("NOISE_CONTROLS_UPDATE rejects unknown modes",
    NoiseControl.FromUpdateFrame(
        new SppFrame(MsgIds.NoiseControlsUpdated, new byte[] { 9 }, false)) is null);
Check("AMBIENT_MODE_UPDATED on",
    NoiseControl.FromUpdateFrame(
        new SppFrame(MsgIds.AmbientModeUpdated, new byte[] { 1 }, false)) == NoiseControlMode.Ambient);
Check("AMBIENT_MODE_UPDATED off",
    NoiseControl.FromUpdateFrame(
        new SppFrame(MsgIds.AmbientModeUpdated, new byte[] { 0 }, false)) == NoiseControlMode.Off);
Check("NOISE_REDUCTION_MODE_UPDATE on",
    NoiseControl.FromUpdateFrame(
        new SppFrame(MsgIds.NoiseReductionModeUpdated, new byte[] { 1 }, false)) == NoiseControlMode.Anc);
Check("NOISE_REDUCTION_MODE_UPDATE off",
    NoiseControl.FromUpdateFrame(
        new SppFrame(MsgIds.NoiseReductionModeUpdated, new byte[] { 0 }, false)) == NoiseControlMode.Off);
Check("NOISE_CONTROLS acknowledgement reads Adaptive",
    NoiseControl.FromUpdateFrame(
        new SppFrame(MsgIds.NoiseControls, new byte[] { (byte)NoiseControlMode.Adaptive }, true))
    == NoiseControlMode.Adaptive);
Check("NOISE_CONTROLS request is not treated as a state update",
    NoiseControl.FromUpdateFrame(
        new SppFrame(MsgIds.NoiseControls, new byte[] { (byte)NoiseControlMode.Adaptive }, false)) is null);

// 15. Charging keeps the remaining level visible: 50 % charging must fill half
// the shell, not the whole interior.
using (var charging50 = TrayIconRenderer.Render(
           50, 50, true, true, connected: true, showPercentage: false,
           lightTheme: false, size: 24).ToBitmap())
using (var charging100 = TrayIconRenderer.Render(
           100, 100, true, true, connected: true, showPercentage: false,
           lightTheme: false, size: 24).ToBitmap())
{
    var green50 = CountChargingGreen(charging50);
    var green100 = CountChargingGreen(charging100);
    Check("charging fill follows the level",
        green50 > 0 && green50 < green100,
        $"{green50} vs {green100} green pixels");
}

// A stale left/right charging bit while both buds sit in the case must not
// leave a lone lightning bolt on an otherwise empty shell.
using (var chargingInCase = TrayIconRenderer.Render(
           null, null, true, true, connected: true, showPercentage: false,
           lightTheme: false, size: 24, leftPresent: false, rightPresent: false).ToBitmap())
using (var emptyInCase = TrayIconRenderer.Render(
           null, null, false, false, connected: true, showPercentage: false,
           lightTheme: false, size: 24, leftPresent: false, rightPresent: false).ToBitmap())
{
    Check("charging without a bud level stays an empty shell", SamePixels(chargingInCase, emptyInCase));
}

// 16. In single-bud mode the icon must describe the bud in use, never the one
// that is still in the case.
using (var singleLeft = TrayIconRenderer.Render(
           80, 60, false, false, connected: true, showPercentage: false,
           lightTheme: false, size: 24, leftPresent: true, rightPresent: false).ToBitmap())
using (var singleLeftOtherLevel = TrayIconRenderer.Render(
           80, 20, false, false, connected: true, showPercentage: false,
           lightTheme: false, size: 24, leftPresent: true, rightPresent: false).ToBitmap())
using (var singleLeftNoRight = TrayIconRenderer.Render(
           80, null, false, false, connected: true, showPercentage: false,
           lightTheme: false, size: 24, leftPresent: true, rightPresent: false).ToBitmap())
{
    Check("single-bud icon ignores the other bud's level",
        SamePixels(singleLeft, singleLeftOtherLevel) && SamePixels(singleLeft, singleLeftNoRight));
}

using (var singleRight = TrayIconRenderer.Render(
           60, 80, false, false, connected: true, showPercentage: false,
           lightTheme: false, size: 24, leftPresent: false, rightPresent: true).ToBitmap())
using (var singleRightOtherLevel = TrayIconRenderer.Render(
           20, 80, false, false, connected: true, showPercentage: false,
           lightTheme: false, size: 24, leftPresent: false, rightPresent: true).ToBitmap())
{
    Check("right-only icon ignores the left bud's level", SamePixels(singleRight, singleRightOtherLevel));
}

using (var unknownSingle = TrayIconRenderer.Render(
           null, 80, false, false, connected: true, showPercentage: false,
           lightTheme: false, size: 24, leftPresent: true, rightPresent: false).ToBitmap())
using (var emptySingle = TrayIconRenderer.Render(
           null, null, false, false, connected: true, showPercentage: false,
           lightTheme: false, size: 24, leftPresent: true, rightPresent: false).ToBitmap())
{
    Check("single-bud icon shows no other bud when its level is unknown",
        SamePixels(unknownSingle, emptySingle));
}

if (args.Contains("--icons"))
    return RenderIconSheet();

if (args.Contains("--menu"))
    return RenderMenuPreview();

if (args.Contains("--probe"))
    return await ProbeStatusRequests();

if (args.Contains("--devices"))
    return await ListDevices();

if (args.Contains("--connect"))
    return await LiveTest();

if (args.Contains("--noise"))
    return await LiveNoiseTest();

Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : $"{failures} TEST(S) FAILED");
return failures == 0 ? 0 : 1;

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "BudsTray")))
        dir = dir.Parent;

    return dir?.FullName ?? Directory.GetCurrentDirectory();
}

static bool SamePixels(Bitmap a, Bitmap b)
{
    if (a.Width != b.Width || a.Height != b.Height) return false;

    for (var y = 0; y < a.Height; y++)
    for (var x = 0; x < a.Width; x++)
        if (a.GetPixel(x, y) != b.GetPixel(x, y))
            return false;

    return true;
}

static int CountChargingGreen(Bitmap bitmap)
{
    var count = 0;
    for (var y = 0; y < bitmap.Height; y++)
    for (var x = 0; x < bitmap.Width; x++)
    {
        var color = bitmap.GetPixel(x, y);
        if (color.A > 200 && color.G > 100 && color.G > color.R + 50 && color.G > color.B + 30)
            count++;
    }

    return count;
}

static int RenderIconSheet()
{
    var outDirectory = Path.Combine(FindRepoRoot(), "artifacts", "icons");
    Directory.CreateDirectory(outDirectory);

    var items = new (string Label, int? Left, int? Right, bool LeftCharging, bool RightCharging, bool Connected, bool ShowPercentage, bool LeftPresent, bool RightPresent)[]
    {
        ("100 / 100", 100, 100, false, false, true, false, true, true),
        ("75 / 75", 75, 75, false, false, true, false, true, true),
        ("50 / 50", 50, 50, false, false, true, false, true, true),
        ("25 / 25", 25, 25, false, false, true, false, true, true),
        ("16 / 16", 16, 16, false, false, true, false, true, true),
        ("15 / 15 low", 15, 15, false, false, true, false, true, true),
        ("10 / 10 low", 10, 10, false, false, true, false, true, true),
        ("0 / 0", 0, 0, false, false, true, false, true, true),
        ("100 / 50 dual", 100, 50, false, false, true, false, true, true),
        ("89 / 58 dual", 89, 58, false, false, true, false, true, true),
        ("60 / 20 dual", 60, 20, false, false, true, false, true, true),
        ("55 / 50 single", 55, 50, false, false, true, false, true, true),
        ("both charging", 50, 50, true, true, true, false, true, true),
        ("left charging", 80, 80, true, false, true, false, true, true),
        ("charging low", 15, 15, true, true, true, false, true, true),
        ("left only", 80, 60, false, false, true, false, true, false),
        ("right only", 80, 60, false, false, true, false, false, true),
        ("left only low", 12, 60, false, false, true, false, true, false),
        ("left only charging", 80, 60, true, false, true, false, true, false),
        ("both in case", 80, 60, false, false, true, false, false, false),
        ("disconnected", null, null, false, false, false, false, true, true),
        ("text 100", 100, 100, false, false, true, true, true, true),
        ("text 87", 87, 87, false, false, true, true, true, true),
        ("text 7", 7, 7, false, false, true, true, true, true),
        ("text charging", 87, 87, true, true, true, true, true, true),
        ("text left only", 87, 60, false, false, true, true, true, false)
    };

    var sizes = new[] { 16, 24 };
    var styles = new[] { ("Windows 11 style", true), ("Windows 10 style", false) };
    const int columns = 6;
    const int cellWidth = 150;
    const int cellHeight = 108;
    const int margin = 20;
    const int headerHeight = 80;
    const int styleHeaderHeight = 38;
    const int themeHeaderHeight = 34;
    const int sizeHeaderHeight = 24;

    var rowsPerSize = (items.Length + columns - 1) / columns;
    var themeHeight = themeHeaderHeight + sizes.Length * (sizeHeaderHeight + rowsPerSize * cellHeight);
    var width = columns * cellWidth + margin * 2;
    var height = headerHeight + styles.Length * (styleHeaderHeight + 2 * themeHeight) + margin;

    using var sheet = new Bitmap(width, height, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(sheet);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.InterpolationMode = InterpolationMode.NearestNeighbor;
    g.PixelOffsetMode = PixelOffsetMode.Half;

    using var titleFont = new Font("Segoe UI", 22f, FontStyle.Bold, GraphicsUnit.Pixel);
    using var subtitleFont = new Font("Segoe UI", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
    using var themeFont = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel);
    using var sizeFont = new Font("Segoe UI", 12f, FontStyle.Bold, GraphicsUnit.Pixel);
    using var labelFont = new Font("Segoe UI", 12f, FontStyle.Regular, GraphicsUnit.Pixel);

    g.Clear(Color.FromArgb(255, 24, 24, 24));
    using (var titleBrush = new SolidBrush(Color.White))
        g.DrawString("BudsTray icon preview", titleFont, titleBrush, margin, 14);

    using (var subtitleBrush = new SolidBrush(Color.FromArgb(255, 168, 168, 168)))
    {
        g.DrawString("Native battery glyph · Windows 11 and Windows 10 variants · two rows when left/right differ by more than 5% · green with a cut-out bolt while charging",
            subtitleFont, subtitleBrush, margin + 2, 40);
        g.DrawString("16 px = 100% DPI · 24 px = 150% DPI · upscaled so pixels are visible",
            subtitleFont, subtitleBrush, margin + 2, 58);
    }

    var y = headerHeight;
    foreach (var style in styles)
    {
        using (var styleBand = new SolidBrush(Color.FromArgb(255, 40, 44, 48)))
            g.FillRectangle(styleBand, 0, y, width, styleHeaderHeight);

        using (var styleBrush = new SolidBrush(Color.FromArgb(255, 0, 230, 118)))
            g.DrawString(style.Item1, themeFont, styleBrush, margin, y + 10);

        y += styleHeaderHeight;

        foreach (var lightTheme in new[] { false, true })
        {
            var bandColor = lightTheme ? Color.FromArgb(255, 243, 243, 243) : Color.FromArgb(255, 32, 32, 32);
            var textColor = lightTheme ? Color.FromArgb(255, 26, 26, 26) : Color.FromArgb(255, 232, 232, 232);
            var mutedColor = lightTheme ? Color.FromArgb(255, 96, 96, 96) : Color.FromArgb(255, 160, 160, 160);

            using (var band = new SolidBrush(bandColor))
                g.FillRectangle(band, 0, y, width, themeHeight);

            using (var themeBrush = new SolidBrush(textColor))
                g.DrawString(lightTheme ? "Light taskbar" : "Dark taskbar", themeFont, themeBrush, margin, y + 8);

            var innerY = y + themeHeaderHeight;
            foreach (var size in sizes)
            {
                using (var sizeBrush = new SolidBrush(mutedColor))
                    g.DrawString($"{size} px{(size == 16 ? "  (100% DPI)" : "  (150% DPI)")}",
                        sizeFont, sizeBrush, margin, innerY + 4);

                var gridTop = innerY + sizeHeaderHeight;
                for (var i = 0; i < items.Length; i++)
                {
                    var item = items[i];
                    var column = i % columns;
                    var row = i / columns;
                    var cellX = margin + column * cellWidth;
                    var cellY = gridTop + row * cellHeight;

                    using var icon = TrayIconRenderer.Render(
                        item.Left, item.Right, item.LeftCharging, item.RightCharging,
                        item.Connected, item.ShowPercentage, lightTheme, size, style.Item2,
                        item.LeftPresent, item.RightPresent);
                    using var bitmap = icon.ToBitmap();

                    var display = size == 16 ? 64 : 72;
                    var iconX = cellX + (cellWidth - display) / 2f;
                    var iconY = cellY + 8;
                    g.DrawImage(bitmap, iconX, iconY, display, display);

                    using var labelBrush = new SolidBrush(textColor);
                    using var labelFormat = new StringFormat { Alignment = StringAlignment.Center };
                    g.DrawString(item.Label, labelFont, labelBrush,
                        new RectangleF(cellX, iconY + display + 4, cellWidth, 18), labelFormat);
                }

                innerY += sizeHeaderHeight + rowsPerSize * cellHeight;
            }

            y += themeHeight;
        }
    }

    var sheetPath = Path.Combine(outDirectory, "icon-preview.png");
    sheet.Save(sheetPath, ImageFormat.Png);

    // Also emit the raw 16 px icons that the notification area actually uses.
    foreach (var style in styles)
    {
        var prefix = style.Item2 ? "win11" : "win10";
        foreach (var lightTheme in new[] { false, true })
        {
            foreach (var item in items)
            {
                var name = item.Label
                    .Replace("%", "pct")
                    .Replace(" / ", "-")
                    .Replace("/", "-")
                    .Replace("+", "plus")
                    .Replace(" ", "-");
                using var icon = TrayIconRenderer.Render(
                    item.Left, item.Right, item.LeftCharging, item.RightCharging,
                    item.Connected, item.ShowPercentage, lightTheme, 16, style.Item2,
                    item.LeftPresent, item.RightPresent);
                using var bitmap = icon.ToBitmap();
                bitmap.Save(
                    Path.Combine(outDirectory, $"{prefix}-{(lightTheme ? "light" : "dark")}-16px-{name}.png"),
                    ImageFormat.Png);
            }
        }
    }

    Console.WriteLine($"Wrote {sheetPath}");
    return 0;
}

static int RenderMenuPreview()
{
    var outDirectory = Path.Combine(FindRepoRoot(), "artifacts", "menu");
    Directory.CreateDirectory(outDirectory);

    var statuses = new[]
    {
        ("", new BatteryStatus(58, 89, 26, false, true, false, DateTimeOffset.Now, EarType: 6),
            NoiseControlMode.Anc),
        ("-nocase", new BatteryStatus(57, 52, 0, false, false, false, DateTimeOffset.Now, EarType: 6),
            NoiseControlMode.Ambient),
        ("-adaptive", new BatteryStatus(64, 63, 40, false, false, false, DateTimeOffset.Now, EarType: 9),
            NoiseControlMode.Adaptive)
    };

    foreach (var (suffix, status, mode) in statuses)
    foreach (var (name, theme) in new[] { ("dark", AppTheme.Dark), ("light", AppTheme.Light) })
    {
        using var host = new Form
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(40, 40),
            Size = new Size(260, 260),
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            Opacity = 0.01,
            BackColor = Color.Black
        };

        host.Show();
        Application.DoEvents();

        using var menu = new TrayMenu(
            () => true,
            () => status,
            () => mode,
            () => status.EarType == 9 ? "Galaxy Buds3 Pro" : "Galaxy Buds FE",
            _ => { },
            new AppConfig { Theme = theme },
            () => { });

        menu.Show(host, new Point(24, 24));
        Application.DoEvents();
        Thread.Sleep(300);
        Application.DoEvents();

        var bounds = menu.Bounds;
        using (var bitmap = new Bitmap(bounds.Width, bounds.Height))
        {
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);

            bitmap.Save(Path.Combine(outDirectory, $"tray-menu-{name}{suffix}.png"), ImageFormat.Png);
        }

        menu.Close();
        host.Close();
        Application.DoEvents();
        Thread.Sleep(100);
    }

    Console.WriteLine("Wrote tray-menu-dark.png, tray-menu-light.png, the -nocase and -adaptive variants");
    return 0;
}

static async Task<int> ProbeStatusRequests()
{
    using var client = new BudsClient(new AppConfig());
    var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    client.FrameReceived += frame =>
        Console.WriteLine($"  <- {DateTime.Now:HH:mm:ss.fff} id={frame.Id} len={frame.Payload.Length} " +
                          (frame.Id == 38
                              ? Convert.ToHexString(frame.Payload)
                              : Convert.ToHexString(frame.Payload.AsSpan(0, Math.Min(12, frame.Payload.Length)))));
    client.BatteryUpdated += status => Console.WriteLine($"  battery {status.ToTooltip("Buds")}");
    client.StateChanged += (state, error) =>
    {
        Console.WriteLine($"  state {state}{(error is null ? "" : " — " + error)}");
        if (state == ConnectionState.Connected) connected.TrySetResult();
    };

    client.Start();
    var reached = await Task.WhenAny(connected.Task, Task.Delay(TimeSpan.FromSeconds(15)));
    if (reached != connected.Task)
    {
        Console.WriteLine("PROBE: no connection");
        return 2;
    }

    await Task.Delay(1500);

    var candidates = new (string Name, byte Id, byte[] Payload)[]
    {
        ("DEBUG_GET_ALL_DATA #1", 38, Array.Empty<byte>()),
        ("DEBUG_GET_ALL_DATA #2", 38, Array.Empty<byte>()),
        ("MANAGER_INFO", MsgIds.ManagerInfo, new byte[] { 1, 1, 34 })
    };

    foreach (var (name, id, payload) in candidates)
    {
        Console.WriteLine($"---> {name} (id {id})");
        await client.SendMessageAsync(id, payload);
        await Task.Delay(TimeSpan.FromSeconds(4));
    }

    await Task.Delay(TimeSpan.FromSeconds(4));
    Console.WriteLine("PROBE: done");
    return 0;
}

static async Task<int> ListDevices()
{
    var infos = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(
        Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromPairingState(true));

    Console.WriteLine($"Paired Bluetooth devices: {infos.Count}");
    foreach (var info in infos)
    {
        var device = await Windows.Devices.Bluetooth.BluetoothDevice.FromIdAsync(info.Id);
        if (device == null)
        {
            Console.WriteLine($"  {info.Name}: <could not open>");
            continue;
        }

        Console.WriteLine($"  {device.Name} [{device.ConnectionStatus}] {device.BluetoothAddress:X12}");

        foreach (var mode in new[]
                 {
                     Windows.Devices.Bluetooth.BluetoothCacheMode.Cached,
                     Windows.Devices.Bluetooth.BluetoothCacheMode.Uncached
                 })
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var result = await device.GetRfcommServicesForIdAsync(
                    Windows.Devices.Bluetooth.Rfcomm.RfcommServiceId.FromUuid(BudsClient.SppServiceUuid), mode);
                Console.WriteLine($"    SPP {mode}: {result.Services.Count} service(s) in {stopwatch.ElapsedMilliseconds} ms");
                foreach (var service in result.Services)
                    Console.WriteLine($"      uuid={service.ServiceId.Uuid} channel={service.ConnectionServiceName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    SPP {mode}: {ex.GetType().Name} 0x{ex.HResult:X8} in {stopwatch.ElapsedMilliseconds} ms — {ex.Message}");
            }
        }
    }

    return 0;
}

static async Task<int> LiveTest()
{
    Console.WriteLine();
    Console.WriteLine("Attempting a live connection to paired Galaxy Buds (20s timeout)...");

    using var client = new BudsClient(new AppConfig());
    var received = new TaskCompletionSource<BatteryStatus>(TaskCreationOptions.RunContinuationsAsynchronously);

    client.BatteryUpdated += status => received.TrySetResult(status);
    client.StateChanged += (state, error) =>
        Console.WriteLine($"  state: {state}{(error is null ? "" : " — " + error)}");

    client.Start();

    var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(20)));
    if (completed == received.Task)
    {
        var status = await received.Task;
        Console.WriteLine($"LIVE OK: {status.ToTooltip("Buds")}");
        return 0;
    }

    Console.WriteLine("LIVE: no battery frame received. Are the Buds paired, awake, and not held by another app?");
    return 2;
}

static async Task<int> LiveNoiseTest()
{
    Console.WriteLine();
    Console.WriteLine("Connecting to paired Galaxy Buds to exercise the sound-mode commands (20s timeout)...");

    using var client = new BudsClient(new AppConfig());
    var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    BatteryStatus? latest = null;

    client.StateChanged += (state, error) =>
    {
        Console.WriteLine($"  state: {state}{(error is null ? "" : " - " + error)}");
        if (state == ConnectionState.Connected) connected.TrySetResult();
    };
    client.BatteryUpdated += status =>
    {
        latest = status;
        Console.WriteLine($"  status: {status.ToTooltip("Buds")} (ear {status.EarType})");
    };
    client.NoiseControlChanged += mode => Console.WriteLine($"  mode: {mode}");
    client.FrameReceived += frame =>
    {
        if (frame.Id is MsgIds.StatusUpdated or MsgIds.ExtendedStatusUpdated or
            MsgIds.NoiseControlsUpdated or MsgIds.NoiseControls or
            MsgIds.AmbientModeUpdated or MsgIds.SetAmbientMode or
            MsgIds.NoiseReductionModeUpdated or MsgIds.SetNoiseReduction)
            Console.WriteLine($"  <- {frame}");
    };

    client.Start();

    var reached = await Task.WhenAny(connected.Task, Task.Delay(TimeSpan.FromSeconds(20)));
    if (reached != connected.Task)
    {
        Console.WriteLine("NOISE: no connection");
        return 2;
    }

    await Task.Delay(2000);

    if (latest is { LeftPresent: false, RightPresent: false })
    {
        Console.WriteLine("NOISE: both buds are in the case, where the buds force Off. " +
                          "Wear one bud and run this mode again to test ANC.");
        return 3;
    }

    foreach (var mode in new[] { NoiseControlMode.Anc, NoiseControlMode.Off })
    {
        Console.WriteLine($"---> {mode}");
        var sent = await client.SetNoiseControlAsync(mode);
        Console.WriteLine($"     sent={sent}");
        await Task.Delay(2500);
    }

    Console.WriteLine("NOISE: done");
    return 0;
}
