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

public sealed record SppFrame(byte Id, byte[] Payload, bool IsResponse)
{
    public override string ToString() =>
        $"id={Id} len={Payload.Length} response={IsResponse} payload={Convert.ToHexString(Payload)}";
}

/// <summary>
/// Frame codec for the Galaxy Buds configuration protocol (SPP/RFCOMM).
///
/// Layout:
///   [0]      0xFD (SOM)
///   [1..2]   little-endian header: bits 0-9 = size, bit 12 = response flag
///            size = 1 (id) + payload + 2 (crc)
///   [3]      message id
///   [4..]    payload
///   [..+2]   CRC-16/XMODEM over id + payload, little-endian
///   [last]   0xDD (EOM)
/// </summary>
public static class SppProtocol
{
    public const byte Som = 0xFD;
    public const byte Eom = 0xDD;
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

    public static byte[] Encode(byte messageId, ReadOnlySpan<byte> payload, bool response = false)
    {
        if (payload.Length > MaxPayload)
            throw new ArgumentOutOfRangeException(nameof(payload), payload.Length, $"Payload exceeds {MaxPayload} bytes.");

        var size = 1 + payload.Length + 2;
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

    public static bool TryDecode(ReadOnlySpan<byte> frame, out SppFrame result)
    {
        result = null!;
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

        public List<SppFrame> Feed(byte[] data, int count = -1)
        {
            if (count < 0) count = data.Length;
            for (var i = 0; i < count; i++) _buffer.Add(data[i]);

            var frames = new List<SppFrame>();

            while (true)
            {
                var somIndex = _buffer.IndexOf(Som);
                if (somIndex < 0)
                {
                    _buffer.Clear();
                    break;
                }
                if (somIndex > 0) _buffer.RemoveRange(0, somIndex);

                if (_buffer.Count < HeaderSize + 3 + 1) break;

                var size = ((_buffer[1] | (_buffer[2] << 8)) & MaxSizeField);
                if (size < 3)
                {
                    _buffer.RemoveAt(0);
                    continue;
                }

                var total = HeaderSize + size + 1;
                if (_buffer.Count < total) break;

                var candidate = _buffer.GetRange(0, total).ToArray();
                if (TryDecode(candidate, out var frame))
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
