using System.Text;

namespace BudsTray.Buds;

/// <summary>
/// Samsung Galaxy Buds SPP message ids used by this app.
/// </summary>
public static class MsgIds
{
    public const byte StatusUpdated = 96;
    public const byte ExtendedStatusUpdated = 97;
    public const byte NoiseControlsUpdated = 119;
    public const byte NoiseControls = 120;
    public const byte SetAmbientMode = 128;
    public const byte AmbientModeUpdated = 129;
    public const byte SetNoiseReduction = 152;
    public const byte NoiseReductionModeUpdated = 155;
    public const byte DebugGetAllData = 38;
    public const byte ManagerInfo = 136;
}

/// <summary>
/// The two on-the-wire frame layouts. Buds, Buds+ and Buds Pro use the classic
/// 0xFD/0xDD frame; the original Galaxy Buds (2019) uses the older 0xFE/0xEE
/// frame with an explicit type byte.
/// </summary>
public enum SppTransport
{
    Modern,
    Legacy
}

public sealed record SppFrame(
    byte Id,
    byte[] Payload,
    bool IsResponse,
    SppTransport Transport = SppTransport.Modern)
{
    public override string ToString() =>
        $"id={Id} len={Payload.Length} response={IsResponse} transport={Transport} payload={Convert.ToHexString(Payload)}";
}

/// <summary>
/// Frame codec for the Galaxy Buds configuration protocol (SPP/RFCOMM).
///
/// Modern layout (0xFD/0xDD):
///   [0]      0xFD (SOM)
///   [1..2]   little-endian header: bits 0-9 = size, bit 12 = response flag
///            size = 1 (id) + payload + 2 (crc)
///   [3]      message id
///   [4..]    payload
///   [..+2]   CRC-16/XMODEM over id + payload, little-endian
///   [last]   0xDD (EOM)
///
/// Legacy layout (0xFE/0xEE, original Galaxy Buds):
///   [0]      0xFE (SOM)
///   [1]      type: 0 = request, 1 = response
///   [2]      size (1 + payload + 2)
///   [3]      message id
///   [4..]    payload
///   [..+2]   CRC-16/XMODEM over id + payload, little-endian
///   [last]   0xEE (EOM)
/// </summary>
public static class SppProtocol
{
    public const byte Som = 0xFD;
    public const byte Eom = 0xDD;
    public const byte LegacySom = 0xFE;
    public const byte LegacyEom = 0xEE;
    public const int HeaderSize = 3;
    public const int MaxSizeField = 0x3FF;
    public const int MaxPayload = MaxSizeField - 3;

    /// <summary>CRC-16/XMODEM: poly 0x1021, init 0x0000, no reflection, no final xor.</summary>
    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 0x8000) != 0
                    ? (ushort)((crc << 1) ^ 0x1021)
                    : (ushort)(crc << 1);
            }
        }
        return crc;
    }

    public static byte[] Encode(
        byte messageId,
        ReadOnlySpan<byte> payload,
        bool response = false,
        SppTransport transport = SppTransport.Modern)
    {
        var size = 1 + payload.Length + 2;

        if (transport == SppTransport.Legacy)
        {
            if (size > byte.MaxValue)
                throw new ArgumentOutOfRangeException(
                    nameof(payload), payload.Length, "Legacy payload exceeds 252 bytes.");

            var legacy = new byte[4 + size];
            legacy[0] = LegacySom;
            legacy[1] = (byte)(response ? 1 : 0);
            legacy[2] = (byte)size;
            legacy[3] = messageId;
            payload.CopyTo(legacy.AsSpan(4));

            var legacyCrc = Crc16(legacy.AsSpan(3, 1 + payload.Length));
            legacy[4 + payload.Length] = (byte)(legacyCrc & 0xFF);
            legacy[5 + payload.Length] = (byte)(legacyCrc >> 8);
            legacy[^1] = LegacyEom;

            return legacy;
        }

        if (payload.Length > MaxPayload)
            throw new ArgumentOutOfRangeException(nameof(payload), payload.Length, $"Payload exceeds {MaxPayload} bytes.");

        var frame = new byte[HeaderSize + size + 1];
        frame[0] = Som;

        var header = (ushort)size;
        if (response) header |= 0x1000;
        frame[1] = (byte)(header & 0xFF);
        frame[2] = (byte)(header >> 8);

        frame[3] = messageId;
        payload.CopyTo(frame.AsSpan(4));

        var crc = Crc16(frame.AsSpan(3, 1 + payload.Length));
        frame[4 + payload.Length] = (byte)(crc & 0xFF);
        frame[5 + payload.Length] = (byte)(crc >> 8);
        frame[^1] = Eom;

        return frame;
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> frame,
        out SppFrame result,
        SppTransport transport = SppTransport.Modern)
    {
        result = null!;

        if (transport == SppTransport.Legacy)
        {
            // SOM + type + size + id + payload + CRC + EOM.
            if (frame.Length < 4 + 3 || frame[0] != LegacySom || frame[^1] != LegacyEom)
                return false;

            var type = frame[1];
            if (type > 1) return false;

            var legacySize = frame[2];
            if (legacySize < 3 || frame.Length != 4 + legacySize)
                return false;

            var legacyPayloadLength = legacySize - 3;
            var legacyId = frame[3];
            var legacyPayload = frame.Slice(4, legacyPayloadLength).ToArray();
            var legacyStoredCrc = (ushort)(frame[4 + legacyPayloadLength] |
                                           (frame[5 + legacyPayloadLength] << 8));
            if (Crc16(frame.Slice(3, 1 + legacyPayloadLength)) != legacyStoredCrc)
                return false;

            result = new SppFrame(legacyId, legacyPayload, type == 1, SppTransport.Legacy);
            return true;
        }

        if (frame.Length < HeaderSize + 3 + 1 || frame[0] != Som || frame[^1] != Eom)
            return false;

        var header = (ushort)(frame[1] | (frame[2] << 8));
        var isResponse = (header & 0x1000) != 0;
        var size = header & MaxSizeField;
        if (size < 3 || frame.Length != HeaderSize + size + 1)
            return false;

        var payloadLength = size - 3;
        var id = frame[3];
        var payload = frame.Slice(4, payloadLength).ToArray();
        var storedCrc = (ushort)(frame[4 + payloadLength] | (frame[5 + payloadLength] << 8));
        if (Crc16(frame.Slice(3, 1 + payloadLength)) != storedCrc)
            return false;

        result = new SppFrame(id, payload, isResponse);
        return true;
    }

    /// <summary>
    /// Splits an incoming byte stream into frames. Handles partial frames, garbage
    /// before the SOM byte and back-to-back frames.
    /// </summary>
    public sealed class StreamParser
    {
        private const int MaxBufferedBytes = 16 * 1024;
        private readonly List<byte> _buffer = new();
        private readonly SppTransport _transport;

        public StreamParser(SppTransport transport = SppTransport.Modern) => _transport = transport;

        public List<SppFrame> Feed(byte[] data, int count = -1)
        {
            if (count < 0) count = data.Length;
            for (var i = 0; i < count; i++) _buffer.Add(data[i]);

            var frames = new List<SppFrame>();
            var som = _transport == SppTransport.Legacy ? LegacySom : Som;

            while (true)
            {
                var somIndex = _buffer.IndexOf(som);
                if (somIndex < 0)
                {
                    _buffer.Clear();
                    break;
                }
                if (somIndex > 0) _buffer.RemoveRange(0, somIndex);

                if (_buffer.Count < 7) break;

                int size;
                int total;
                if (_transport == SppTransport.Legacy)
                {
                    size = _buffer[2];
                    total = 4 + size;
                }
                else
                {
                    size = (_buffer[1] | (_buffer[2] << 8)) & MaxSizeField;
                    total = HeaderSize + size + 1;
                }

                if (size < 3)
                {
                    _buffer.RemoveAt(0);
                    continue;
                }

                if (_buffer.Count < total) break;

                var candidate = _buffer.GetRange(0, total).ToArray();
                if (TryDecode(candidate, out var frame, _transport))
                {
                    _buffer.RemoveRange(0, total);
                    frames.Add(frame);
                }
                else
                {
                    // Corrupt frame: drop one byte and resync on the next SOM.
                    _buffer.RemoveAt(0);
                }
            }

            if (_buffer.Count > MaxBufferedBytes) _buffer.Clear();
            return frames;
        }

        public void Reset() => _buffer.Clear();
    }
}
