using System;

namespace TclControlWin;

/// <summary>
/// Reverse-engineered from a btsnoop capture of TCL Home &lt;-&gt; TCL S45H soundbar.
/// Frame: 52 43 ("RC") | type(1) | seq(u16 LE) | counter(u16 LE) | 06 00 | attrId(u16 LE) | value(u16 LE, low byte signed) | 00
/// Status notifications echo the same shape with attrId = setAttrId + StatusOffset.
/// Ported from the Android TclProtocol.kt implementation - keep the two in sync.
/// </summary>
public static class TclProtocol
{
    public static readonly Guid ServiceUuid = Guid.Parse("0000f500-0000-1000-8000-00805f9b34fb");
    public static readonly Guid WriteCharUuid = Guid.Parse("e49a25e0-f69a-11e8-8eb2-f2801f1b9fd1");
    public static readonly Guid NotifyCharUuid = Guid.Parse("e49a28e1-f69a-11e8-8eb2-f2801f1b9fd1");

    private const byte MagicHi = 0x52;
    private const byte MagicLo = 0x43;
    private const byte TypeRequest = 0x01;
    private const byte TypeResponse = 0x02;
    private const int ReqSeq = 1;
    private const int StatusOffset = 0x82;

    public static class Attr
    {
        public const int GetStatus = 0x01;
        public const int Volume = 0x02;
        public const int Bass = 0x04;
        public const int Treble = 0x05;
        public const int PowerQuery = 0x06;
        public const int Mute = 0x0e;
        public const int Source = 0x0f;
        public const int SoundMode = 0x11;
        public const int AtmosDts = 0x12;
        public const int GetDeviceInfo = 0x18;
    }

    /// <summary>
    /// The "counter" field is not a sequence number - it's a checksum: the low byte of the sum of the
    /// 7 inner-payload bytes (06 00 attrIdLo attrIdHi valueLo valueHi 00). The device NACKs (ack payload
    /// "01 ff" instead of "01 aa") any frame whose counter doesn't match this checksum.
    /// </summary>
    private static int Checksum(byte[] inner)
    {
        int sum = 0;
        foreach (var b in inner) sum += b;
        return sum & 0xFF;
    }

    public static byte[] BuildSetCommand(int attrId, int value)
    {
        byte valueLo = (byte)(value & 0xFF);
        byte valueHi = 0x00; // observed high byte is always 0x00, even for negative (low-byte-signed) values
        byte[] inner =
        {
            0x06, 0x00,
            (byte)(attrId & 0xFF), (byte)((attrId >> 8) & 0xFF),
            valueLo, valueHi,
            0x00
        };
        int c = Checksum(inner);
        var frame = new byte[7 + inner.Length];
        frame[0] = MagicHi;
        frame[1] = MagicLo;
        frame[2] = TypeRequest;
        frame[3] = (byte)(ReqSeq & 0xFF);
        frame[4] = (byte)((ReqSeq >> 8) & 0xFF);
        frame[5] = (byte)(c & 0xFF);
        frame[6] = 0x00;
        Array.Copy(inner, 0, frame, 7, inner.Length);
        return frame;
    }

    public enum IncomingKind { Status, Ack, Raw, Unrecognized }

    public sealed class Incoming
    {
        public IncomingKind Kind { get; init; }
        public int AttrId { get; init; }
        public int SetAttrId { get; init; }
        public int Value { get; init; }
        public int Seq { get; init; }
        public int Counter { get; init; }
        public byte[] Bytes { get; init; } = Array.Empty<byte>();
    }

    public static Incoming ParseIncoming(byte[] data)
    {
        if (data.Length < 7 || data[0] != MagicHi || data[1] != MagicLo || data[2] != TypeResponse)
        {
            return new Incoming { Kind = IncomingKind.Unrecognized, Bytes = data };
        }

        int seq = data[3] | (data[4] << 8);
        int ctr = data[5] | (data[6] << 8);
        var rest = data[7..];

        // Simple numeric status echo: 06 00 <attrId u16 LE> <value u16 LE, low byte signed> 00
        if (rest.Length == 7 && rest[0] == 0x06 && rest[1] == 0x00)
        {
            int attrId = rest[2] | (rest[3] << 8);
            int value = unchecked((sbyte)rest[4]); // signed low byte
            int setAttrId = attrId >= StatusOffset ? attrId - StatusOffset : attrId;
            return new Incoming { Kind = IncomingKind.Status, AttrId = attrId, SetAttrId = setAttrId, Value = value };
        }

        // Generic short ack seen after every write (seq=4, counter=171, payload="01 aa")
        if (rest.Length <= 4)
        {
            return new Incoming { Kind = IncomingKind.Ack, Bytes = rest };
        }

        return new Incoming { Kind = IncomingKind.Raw, Seq = seq, Counter = ctr, Bytes = rest };
    }

    public static string AttrName(int attrId) => attrId switch
    {
        Attr.Volume => "Volume",
        Attr.Bass => "Bass",
        Attr.Treble => "Treble",
        Attr.PowerQuery => "Power",
        Attr.Mute => "Mute",
        Attr.Source => "Source",
        Attr.SoundMode => "Sound Mode",
        Attr.AtmosDts => "Atmos/DTS",
        Attr.GetStatus => "GetStatus",
        Attr.GetDeviceInfo => "DeviceInfo",
        _ => $"0x{attrId:x2}"
    };
}
