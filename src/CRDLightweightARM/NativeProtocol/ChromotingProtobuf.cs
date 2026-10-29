using System.Buffers.Binary;
using System.Text;

namespace CRDLightweightARM.NativeProtocol;

/// <summary>
/// High-performance, zero-dependency Protocol Buffers wire format serializer and deserializer
/// specifically engineered for Chromium's remoting protocol (Chromoting) over WebRTC SCTP data channels.
/// Supports both unframed SCTP payloads (WebrtcDataStreamAdapter) and 4-byte big-endian framed payloads
/// (StreamMessagePipeAdapter).
/// </summary>
public static class ChromotingProtobuf
{
    #region Wire Format Constants

    public const int WireTypeVarint = 0;
    public const int WireTypeFixed64 = 1;
    public const int WireTypeLengthDelimited = 2;
    public const int WireTypeStartGroup = 3;
    public const int WireTypeEndGroup = 4;
    public const int WireTypeFixed32 = 5;

    public static int MakeTag(int fieldNumber, int wireType) => (fieldNumber << 3) | (wireType & 7);

    #endregion

    #region Framing Helpers

    /// <summary>
    /// Prepends the 4-byte big-endian length prefix header (uint32_be) used by Chromium's
    /// StreamMessagePipeAdapter on SCTP data channels.
    /// </summary>
    public static byte[] FrameMessage(byte[] protoPayload)
    {
        ArgumentNullException.ThrowIfNull(protoPayload);
        byte[] framed = new byte[4 + protoPayload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(framed.AsSpan(0, 4), (uint)protoPayload.Length);
        protoPayload.CopyTo(framed.AsSpan(4));
        return framed;
    }

    /// <summary>
    /// Prepends the 4-byte big-endian length prefix header (uint32_be) to a span payload.
    /// </summary>
    public static byte[] FrameMessage(ReadOnlySpan<byte> protoPayload)
    {
        byte[] framed = new byte[4 + protoPayload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(framed.AsSpan(0, 4), (uint)protoPayload.Length);
        protoPayload.CopyTo(framed.AsSpan(4));
        return framed;
    }

    /// <summary>
    /// Strips the 4-byte big-endian length prefix from framed SCTP messages.
    /// If the payload is not framed or is shorter than 4 bytes, returns the entire span.
    /// </summary>
    public static ReadOnlySpan<byte> UnframeMessage(ReadOnlySpan<byte> framedPayload)
    {
        if (framedPayload.Length < 4)
            return framedPayload;

        uint declaredLength = BinaryPrimitives.ReadUInt32BigEndian(framedPayload.Slice(0, 4));
        if (declaredLength == (uint)(framedPayload.Length - 4))
        {
            return framedPayload.Slice(4);
        }

        // Return original if framing does not match exact payload length
        return framedPayload;
    }

    /// <summary>
    /// Encodes a ControlMessage, optionally applying 4-byte big-endian framing.
    /// </summary>
    public static byte[] EncodeControlMessage(ControlMessage msg, bool framed = false)
    {
        ArgumentNullException.ThrowIfNull(msg);
        byte[] bytes = msg.Encode();
        return framed ? FrameMessage(bytes) : bytes;
    }

    /// <summary>
    /// Encodes an EventMessage, optionally applying 4-byte big-endian framing.
    /// </summary>
    public static byte[] EncodeEventMessage(EventMessage msg, bool framed = false)
    {
        ArgumentNullException.ThrowIfNull(msg);
        byte[] bytes = msg.Encode();
        return framed ? FrameMessage(bytes) : bytes;
    }

    #endregion

    #region High-Level Native Resolution Sync

    /// <summary>
    /// Generates native resolution synchronization protobuf messages aligned to even-macroblock
    /// boundaries (width &amp; ~1, height &amp; ~1) for Qualcomm Adreno hardware video decoders.
    /// Produces individual payloads as well as a unified ControlMessage in both raw byte[] and Base64 format.
    /// </summary>
    public static NativeResolutionSyncResult BuildNativeResolutionSyncMessages(
        int physicalWidth,
        int physicalHeight,
        int dpi,
        long screenId = 0,
        int targetFps = 60,
        bool losslessColor = false)
    {
        int alignedWidth = physicalWidth & ~1;
        int alignedHeight = physicalHeight & ~1;
        if (alignedWidth <= 0) alignedWidth = 2;
        if (alignedHeight <= 0) alignedHeight = 2;
        if (dpi <= 0) dpi = 96;

        int dipsWidth = alignedWidth;
        int dipsHeight = alignedHeight;

        var clientRes = new ClientResolution
        {
            WidthDeprecated = alignedWidth,
            HeightDeprecated = alignedHeight,
            DipsWidth = dipsWidth,
            DipsHeight = dipsHeight,
            XDpi = dpi,
            YDpi = dpi,
            ScreenId = screenId
        };

        var trackLayout = new VideoTrackLayout
        {
            MediaStreamId = "screen_stream",
            PositionX = 0,
            PositionY = 0,
            Width = alignedWidth,
            Height = alignedHeight,
            XDpi = dpi,
            YDpi = dpi,
            ScreenId = screenId,
            DisplayName = "Main Display",
            Bpp = 32
        };

        var videoLayout = new VideoLayout
        {
            SupportsFullDesktopCapture = true,
            PrimaryScreenId = screenId
        };
        videoLayout.VideoTracks.Add(trackLayout);

        var videoControl = new VideoControl
        {
            Enable = true,
            LosslessEncode = losslessColor,
            LosslessColor = losslessColor,
            FramerateBoost = true,
            TargetFramerate = (uint)Math.Clamp(targetFps, 15, 120)
        };

        var compositeControlMsg = new ControlMessage
        {
            ClientResolution = clientRes,
            VideoLayout = videoLayout,
            VideoControl = videoControl
        };

        byte[] resBytes = new ControlMessage { ClientResolution = clientRes }.Encode();
        byte[] layoutBytes = new ControlMessage { VideoLayout = videoLayout }.Encode();
        byte[] controlBytes = new ControlMessage { VideoControl = videoControl }.Encode();
        byte[] compositeBytes = compositeControlMsg.Encode();

        return new NativeResolutionSyncResult(
            alignedWidth,
            alignedHeight,
            dpi,
            resBytes,
            layoutBytes,
            controlBytes,
            compositeBytes,
            FrameMessage(compositeBytes)
        );
    }

    /// <summary>
    /// Builds a ControlMessage containing SelectDesktopDisplayRequest for switching remote host displays/monitors.
    /// displayId can be "0", "1", "2", or "all".
    /// </summary>
    public static (byte[] RawBytes, string Base64) BuildSelectDisplayMessage(string displayId)
    {
        var msg = new ControlMessage
        {
            SelectDesktopDisplay = new SelectDesktopDisplayRequest
            {
                Id = displayId
            }
        };
        byte[] bytes = msg.Encode(framed: false);
        return (bytes, Convert.ToBase64String(bytes));
    }

    #endregion
}

#region Native Resolution Sync Result Container

public sealed class NativeResolutionSyncResult
{
    public int AlignedWidth { get; }
    public int AlignedHeight { get; }
    public int Dpi { get; }

    public byte[] ClientResolutionBytes { get; }
    public string ClientResolutionBase64 => Convert.ToBase64String(ClientResolutionBytes);

    public byte[] VideoLayoutBytes { get; }
    public string VideoLayoutBase64 => Convert.ToBase64String(VideoLayoutBytes);

    public byte[] VideoControlBytes { get; }
    public string VideoControlBase64 => Convert.ToBase64String(VideoControlBytes);

    public byte[] ControlMessageBytes { get; }
    public string ControlMessageBase64 => Convert.ToBase64String(ControlMessageBytes);

    public byte[] FramedControlMessageBytes { get; }
    public string FramedControlMessageBase64 => Convert.ToBase64String(FramedControlMessageBytes);

    public NativeResolutionSyncResult(
        int alignedWidth,
        int alignedHeight,
        int dpi,
        byte[] clientResolutionBytes,
        byte[] videoLayoutBytes,
        byte[] videoControlBytes,
        byte[] controlMessageBytes,
        byte[] framedControlMessageBytes)
    {
        AlignedWidth = alignedWidth;
        AlignedHeight = alignedHeight;
        Dpi = dpi;
        ClientResolutionBytes = clientResolutionBytes;
        VideoLayoutBytes = videoLayoutBytes;
        VideoControlBytes = videoControlBytes;
        ControlMessageBytes = controlMessageBytes;
        FramedControlMessageBytes = framedControlMessageBytes;
    }
}

#endregion

#region Zero-Allocation Protobuf Stream Readers & Writers

/// <summary>
/// Fast forward-only Protobuf stream writer writing varints, floats, fixed32/64, strings, and byte arrays.
/// </summary>
public sealed class ProtobufWriter
{
    private byte[] _buffer;
    private int _position;

    public ProtobufWriter(int initialCapacity = 256)
    {
        _buffer = new byte[Math.Max(16, initialCapacity)];
        _position = 0;
    }

    public int Position => _position;

    private void EnsureCapacity(int bytesNeeded)
    {
        if (_position + bytesNeeded > _buffer.Length)
        {
            int newCap = Math.Max(_buffer.Length * 2, _position + bytesNeeded + 64);
            Array.Resize(ref _buffer, newCap);
        }
    }

    public void WriteVarint(ulong value)
    {
        EnsureCapacity(10);
        while (value >= 0x80)
        {
            _buffer[_position++] = (byte)((value & 0x7F) | 0x80);
            value >>= 7;
        }
        _buffer[_position++] = (byte)(value & 0x7F);
    }

    public void WriteTag(int fieldNumber, int wireType)
    {
        WriteVarint((ulong)ChromotingProtobuf.MakeTag(fieldNumber, wireType));
    }

    public void WriteInt32(int tag, int value)
    {
        WriteTag(tag, ChromotingProtobuf.WireTypeVarint);
        WriteVarint((ulong)(long)value);
    }

    public void WriteUInt32(int tag, uint value)
    {
        WriteTag(tag, ChromotingProtobuf.WireTypeVarint);
        WriteVarint(value);
    }

    public void WriteInt64(int tag, long value)
    {
        WriteTag(tag, ChromotingProtobuf.WireTypeVarint);
        WriteVarint((ulong)value);
    }

    public void WriteUInt64(int tag, ulong value)
    {
        WriteTag(tag, ChromotingProtobuf.WireTypeVarint);
        WriteVarint(value);
    }

    public void WriteBool(int tag, bool value)
    {
        WriteTag(tag, ChromotingProtobuf.WireTypeVarint);
        EnsureCapacity(1);
        _buffer[_position++] = value ? (byte)1 : (byte)0;
    }

    public void WriteFloat(int tag, float value)
    {
        WriteTag(tag, ChromotingProtobuf.WireTypeFixed32);
        EnsureCapacity(4);
        BinaryPrimitives.WriteSingleLittleEndian(_buffer.AsSpan(_position, 4), value);
        _position += 4;
    }

    public void WriteString(int tag, string? value)
    {
        if (value is null) return;
        int byteCount = Encoding.UTF8.GetByteCount(value);
        WriteTag(tag, ChromotingProtobuf.WireTypeLengthDelimited);
        WriteVarint((ulong)byteCount);
        EnsureCapacity(byteCount);
        Encoding.UTF8.GetBytes(value, 0, value.Length, _buffer, _position);
        _position += byteCount;
    }

    public void WriteBytes(int tag, ReadOnlySpan<byte> bytes)
    {
        WriteTag(tag, ChromotingProtobuf.WireTypeLengthDelimited);
        WriteVarint((ulong)bytes.Length);
        EnsureCapacity(bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(_position, bytes.Length));
        _position += bytes.Length;
    }

    public void WriteMessage(int tag, byte[]? messageBytes)
    {
        if (messageBytes is null || messageBytes.Length == 0) return;
        WriteBytes(tag, messageBytes);
    }

    public byte[] ToByteArray()
    {
        byte[] result = new byte[_position];
        Array.Copy(_buffer, 0, result, 0, _position);
        return result;
    }

    public ReadOnlySpan<byte> AsSpan() => _buffer.AsSpan(0, _position);
}

/// <summary>
/// Fast zero-copy Protobuf stream reader over ReadOnlySpan&lt;byte&gt;.
/// </summary>
public ref struct ProtobufReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _offset;

    public ProtobufReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _offset = 0;
    }

    public bool HasMore => _offset < _buffer.Length;
    public int Remaining => _buffer.Length - _offset;
    public int Offset => _offset;

    public bool ReadTag(out int fieldNumber, out int wireType)
    {
        if (!HasMore)
        {
            fieldNumber = 0;
            wireType = 0;
            return false;
        }

        ulong tag = ReadVarint();
        fieldNumber = (int)(tag >> 3);
        wireType = (int)(tag & 7);
        return fieldNumber > 0;
    }

    public ulong ReadVarint()
    {
        ulong result = 0;
        int shift = 0;
        while (_offset < _buffer.Length)
        {
            byte b = _buffer[_offset++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return result;
            shift += 7;
            if (shift > 64)
                throw new InvalidOperationException("Invalid protobuf varint format (shift overflow).");
        }
        return result;
    }

    public int ReadInt32() => (int)ReadVarint();
    public uint ReadUInt32() => (uint)ReadVarint();
    public long ReadInt64() => (long)ReadVarint();
    public ulong ReadUInt64() => ReadVarint();
    public bool ReadBool() => ReadVarint() != 0;

    public float ReadFloat()
    {
        if (_offset + 4 > _buffer.Length)
            throw new InvalidOperationException("Unexpected end of protobuf buffer while reading float.");
        float val = BinaryPrimitives.ReadSingleLittleEndian(_buffer.Slice(_offset, 4));
        _offset += 4;
        return val;
    }

    public double ReadDouble()
    {
        if (_offset + 8 > _buffer.Length)
            throw new InvalidOperationException("Unexpected end of protobuf buffer while reading double.");
        double val = BinaryPrimitives.ReadDoubleLittleEndian(_buffer.Slice(_offset, 8));
        _offset += 8;
        return val;
    }

    public ReadOnlySpan<byte> ReadLengthDelimitedSpan()
    {
        int length = (int)ReadVarint();
        if (length < 0 || _offset + length > _buffer.Length)
            throw new InvalidOperationException($"Invalid length-delimited payload length {length}.");
        ReadOnlySpan<byte> span = _buffer.Slice(_offset, length);
        _offset += length;
        return span;
    }

    public string ReadString()
    {
        ReadOnlySpan<byte> span = ReadLengthDelimitedSpan();
        return Encoding.UTF8.GetString(span);
    }

    public byte[] ReadBytes()
    {
        ReadOnlySpan<byte> span = ReadLengthDelimitedSpan();
        return span.ToArray();
    }

    public void SkipField(int wireType)
    {
        switch (wireType)
        {
            case ChromotingProtobuf.WireTypeVarint:
                ReadVarint();
                break;
            case ChromotingProtobuf.WireTypeFixed64:
                if (_offset + 8 > _buffer.Length) throw new InvalidOperationException("Buffer underflow on Fixed64.");
                _offset += 8;
                break;
            case ChromotingProtobuf.WireTypeLengthDelimited:
                int len = (int)ReadVarint();
                if (_offset + len > _buffer.Length) throw new InvalidOperationException("Buffer underflow on LengthDelimited.");
                _offset += len;
                break;
            case ChromotingProtobuf.WireTypeFixed32:
                if (_offset + 4 > _buffer.Length) throw new InvalidOperationException("Buffer underflow on Fixed32.");
                _offset += 4;
                break;
            default:
                throw new NotSupportedException($"Unsupported wire type {wireType}.");
        }
    }
}

#endregion

#region Protocol Message Models (remoting/proto/{control,event,internal}.proto)

/// <summary>
/// remoting::protocol::ClientResolution
/// Tags:
/// 1: width_pixels / width_deprecated
/// 2: height_pixels / height_deprecated
/// 3: dips_width / width_deprecated
/// 4: dips_height / height_deprecated
/// 5: x_dpi
/// 6: y_dpi
/// 7: screen_id
/// </summary>
public sealed class ClientResolution
{
    public int? WidthDeprecated { get; set; }
    public int? HeightDeprecated { get; set; }
    public int? DipsWidth { get; set; }
    public int? DipsHeight { get; set; }
    public int? XDpi { get; set; }
    public int? YDpi { get; set; }
    public long? ScreenId { get; set; }

    public byte[] Encode()
    {
        var writer = new ProtobufWriter();
        if (WidthDeprecated.HasValue) writer.WriteInt32(1, WidthDeprecated.Value);
        if (HeightDeprecated.HasValue) writer.WriteInt32(2, HeightDeprecated.Value);
        if (DipsWidth.HasValue) writer.WriteInt32(3, DipsWidth.Value);
        if (DipsHeight.HasValue) writer.WriteInt32(4, DipsHeight.Value);
        if (XDpi.HasValue) writer.WriteInt32(5, XDpi.Value);
        if (YDpi.HasValue) writer.WriteInt32(6, YDpi.Value);
        if (ScreenId.HasValue) writer.WriteInt64(7, ScreenId.Value);
        return writer.ToByteArray();
    }

    public static ClientResolution Decode(ReadOnlySpan<byte> data)
    {
        var result = new ClientResolution();
        var reader = new ProtobufReader(data);
        while (reader.ReadTag(out int fieldNumber, out int wireType))
        {
            switch (fieldNumber)
            {
                case 1: result.WidthDeprecated = reader.ReadInt32(); break;
                case 2: result.HeightDeprecated = reader.ReadInt32(); break;
                case 3: result.DipsWidth = reader.ReadInt32(); break;
                case 4: result.DipsHeight = reader.ReadInt32(); break;
                case 5: result.XDpi = reader.ReadInt32(); break;
                case 6: result.YDpi = reader.ReadInt32(); break;
                case 7: result.ScreenId = reader.ReadInt64(); break;
                default: reader.SkipField(wireType); break;
            }
        }
        return result;
    }
}

/// <summary>
/// remoting::protocol::VideoTrackLayout
/// Tags:
/// 1: media_stream_id
/// 2: position_x
/// 3: position_y
/// 4: width
/// 5: height
/// 6: x_dpi
/// 7: y_dpi
/// 8: screen_id
/// 9: display_name
/// 10: bpp
/// </summary>
public sealed class VideoTrackLayout
{
    public string? MediaStreamId { get; set; }
    public int? PositionX { get; set; }
    public int? PositionY { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? XDpi { get; set; }
    public int? YDpi { get; set; }
    public long? ScreenId { get; set; }
    public string? DisplayName { get; set; }
    public int? Bpp { get; set; }

    public byte[] Encode()
    {
        var writer = new ProtobufWriter();
        if (MediaStreamId is not null) writer.WriteString(1, MediaStreamId);
        if (PositionX.HasValue) writer.WriteInt32(2, PositionX.Value);
        if (PositionY.HasValue) writer.WriteInt32(3, PositionY.Value);
        if (Width.HasValue) writer.WriteInt32(4, Width.Value);
        if (Height.HasValue) writer.WriteInt32(5, Height.Value);
        if (XDpi.HasValue) writer.WriteInt32(6, XDpi.Value);
        if (YDpi.HasValue) writer.WriteInt32(7, YDpi.Value);
        if (ScreenId.HasValue) writer.WriteInt64(8, ScreenId.Value);
        if (DisplayName is not null) writer.WriteString(9, DisplayName);
        if (Bpp.HasValue) writer.WriteInt32(10, Bpp.Value);
        return writer.ToByteArray();
    }

    public static VideoTrackLayout Decode(ReadOnlySpan<byte> data)
    {
        var result = new VideoTrackLayout();
        var reader = new ProtobufReader(data);
        while (reader.ReadTag(out int fieldNumber, out int wireType))
        {
            switch (fieldNumber)
            {
                case 1: result.MediaStreamId = reader.ReadString(); break;
                case 2: result.PositionX = reader.ReadInt32(); break;
                case 3: result.PositionY = reader.ReadInt32(); break;
                case 4: result.Width = reader.ReadInt32(); break;
                case 5: result.Height = reader.ReadInt32(); break;
                case 6: result.XDpi = reader.ReadInt32(); break;
                case 7: result.YDpi = reader.ReadInt32(); break;
                case 8: result.ScreenId = reader.ReadInt64(); break;
                case 9: result.DisplayName = reader.ReadString(); break;
                case 10: result.Bpp = reader.ReadInt32(); break;
                default: reader.SkipField(wireType); break;
            }
        }
        return result;
    }
}

/// <summary>
/// remoting::protocol::VideoLayout
/// Tags:
/// 1: repeated VideoTrackLayout video_track
/// 2: bool supports_full_desktop_capture
/// 3: int64 primary_screen_id
/// 4: int32 pixel_type
/// </summary>
public sealed class VideoLayout
{
    public List<VideoTrackLayout> VideoTracks { get; } = new();
    public bool? SupportsFullDesktopCapture { get; set; }
    public long? PrimaryScreenId { get; set; }
    public int? PixelType { get; set; }

    public byte[] Encode()
    {
        var writer = new ProtobufWriter();
        foreach (var track in VideoTracks)
        {
            writer.WriteBytes(1, track.Encode());
        }
        if (SupportsFullDesktopCapture.HasValue) writer.WriteBool(2, SupportsFullDesktopCapture.Value);
        if (PrimaryScreenId.HasValue) writer.WriteInt64(3, PrimaryScreenId.Value);
        if (PixelType.HasValue) writer.WriteInt32(4, PixelType.Value);
        return writer.ToByteArray();
    }

    public static VideoLayout Decode(ReadOnlySpan<byte> data)
    {
        var result = new VideoLayout();
        var reader = new ProtobufReader(data);
        while (reader.ReadTag(out int fieldNumber, out int wireType))
        {
            switch (fieldNumber)
            {
                case 1:
                    result.VideoTracks.Add(VideoTrackLayout.Decode(reader.ReadLengthDelimitedSpan()));
                    break;
                case 2:
                    result.SupportsFullDesktopCapture = reader.ReadBool();
                    break;
                case 3:
                    result.PrimaryScreenId = reader.ReadInt64();
                    break;
                case 4:
                    result.PixelType = reader.ReadInt32();
                    break;
                default:
                    reader.SkipField(wireType);
                    break;
            }
        }
        return result;
    }
}

/// <summary>
/// remoting::protocol::VideoControl
/// Tags:
/// 1: enable
/// 2: lossless_encode
/// 3: lossless_color
/// 4: framerate_boost
/// 5: target_framerate
/// </summary>
public sealed class VideoControl
{
    public bool? Enable { get; set; }
    public bool? LosslessEncode { get; set; }
    public bool? LosslessColor { get; set; }
    public bool? FramerateBoost { get; set; }
    public uint? TargetFramerate { get; set; }

    public byte[] Encode()
    {
        var writer = new ProtobufWriter();
        if (Enable.HasValue) writer.WriteBool(1, Enable.Value);
        if (LosslessEncode.HasValue) writer.WriteBool(2, LosslessEncode.Value);
        if (LosslessColor.HasValue) writer.WriteBool(3, LosslessColor.Value);
        if (FramerateBoost.HasValue) writer.WriteBool(4, FramerateBoost.Value);
        if (TargetFramerate.HasValue) writer.WriteUInt32(5, TargetFramerate.Value);
        return writer.ToByteArray();
    }

    public static VideoControl Decode(ReadOnlySpan<byte> data)
    {
        var result = new VideoControl();
        var reader = new ProtobufReader(data);
        while (reader.ReadTag(out int fieldNumber, out int wireType))
        {
            switch (fieldNumber)
            {
                case 1: result.Enable = reader.ReadBool(); break;
                case 2: result.LosslessEncode = reader.ReadBool(); break;
                case 3: result.LosslessColor = reader.ReadBool(); break;
                case 4: result.FramerateBoost = reader.ReadBool(); break;
                case 5: result.TargetFramerate = reader.ReadUInt32(); break;
                default: reader.SkipField(wireType); break;
            }
        }
        return result;
    }
}

/// <summary>
/// remoting::protocol::AudioControl
/// Tags:
/// 1: enable
/// </summary>
public sealed class AudioControl
{
    public bool? Enable { get; set; }

    public byte[] Encode()
    {
        var writer = new ProtobufWriter();
        if (Enable.HasValue) writer.WriteBool(1, Enable.Value);
        return writer.ToByteArray();
    }

    public static AudioControl Decode(ReadOnlySpan<byte> data)
    {
        var result = new AudioControl();
        var reader = new ProtobufReader(data);
        while (reader.ReadTag(out int fieldNumber, out int wireType))
        {
            if (fieldNumber == 1) result.Enable = reader.ReadBool();
            else reader.SkipField(wireType);
        }
        return result;
    }
}

/// <summary>
/// remoting::protocol::Capabilities
/// Tags:
/// 1: capabilities
/// </summary>
public sealed class Capabilities
{
    public string? CapabilitiesString { get; set; }

    public byte[] Encode()
    {
        var writer = new ProtobufWriter();
        if (CapabilitiesString is not null) writer.WriteString(1, CapabilitiesString);
        return writer.ToByteArray();
    }

    public static Capabilities Decode(ReadOnlySpan<byte> data)
    {
        var result = new Capabilities();
        var reader = new ProtobufReader(data);
        while (reader.ReadTag(out int fieldNumber, out int wireType))
        {
            if (fieldNumber == 1) result.CapabilitiesString = reader.ReadString();
            else reader.SkipField(wireType);
        }
        return result;
    }
}

/// <summary>
/// remoting::protocol::SelectDesktopDisplayRequest
/// Tags:
/// 1: id
/// </summary>
public sealed class SelectDesktopDisplayRequest
{
    public string? Id { get; set; }

    public byte[] Encode()
    {
        var writer = new ProtobufWriter();
        if (Id is not null) writer.WriteString(1, Id);
        return writer.ToByteArray();
    }

    public static SelectDesktopDisplayRequest Decode(ReadOnlySpan<byte> data)
    {
        var result = new SelectDesktopDisplayRequest();
        var reader = new ProtobufReader(data);
        while (reader.ReadTag(out int fieldNumber, out int wireType))
        {
            if (fieldNumber == 1) result.Id = reader.ReadString();
            else reader.SkipField(wireType);
        }
        return result;
    }
}

/// <summary>
/// remoting::protocol::ControlMessage
/// Tags:
/// 1: clipboard_event
/// 2: client_resolution
/// 3: video_control (or cursor_shape in older schemas)
/// 4: cursor_shape (or video_control)
/// 5: audio_control
/// 6: capabilities
/// 10: video_layout
/// 11: select_desktop_display
/// </summary>
public sealed class ControlMessage
{
    public byte[]? ClipboardEvent { get; set; }
    public ClientResolution? ClientResolution { get; set; }
    public byte[]? CursorShape { get; set; }
    public VideoControl? VideoControl { get; set; }
    public AudioControl? AudioControl { get; set; }
    public Capabilities? Capabilities { get; set; }
    public VideoLayout? VideoLayout { get; set; }
    public SelectDesktopDisplayRequest? SelectDesktopDisplay { get; set; }

    public byte[] Encode(bool framed = false)
    {
        var writer = new ProtobufWriter();
        if (ClipboardEvent is not null && ClipboardEvent.Length > 0)
            writer.WriteBytes(1, ClipboardEvent);
        if (ClientResolution is not null)
            writer.WriteBytes(2, ClientResolution.Encode());
        if (CursorShape is not null && CursorShape.Length > 0)
            writer.WriteBytes(3, CursorShape);
        if (VideoControl is not null)
            writer.WriteBytes(4, VideoControl.Encode());
        if (AudioControl is not null)
            writer.WriteBytes(5, AudioControl.Encode());
        if (Capabilities is not null)
            writer.WriteBytes(6, Capabilities.Encode());
        if (VideoLayout is not null)
            writer.WriteBytes(10, VideoLayout.Encode());
        if (SelectDesktopDisplay is not null)
            writer.WriteBytes(11, SelectDesktopDisplay.Encode());

        byte[] payload = writer.ToByteArray();
        return framed ? ChromotingProtobuf.FrameMessage(payload) : payload;
    }

    public static ControlMessage Decode(ReadOnlySpan<byte> data, bool framed = false)
    {
        if (framed) data = ChromotingProtobuf.UnframeMessage(data);

        var result = new ControlMessage();
        var reader = new ProtobufReader(data);
        while (reader.ReadTag(out int fieldNumber, out int wireType))
        {
            switch (fieldNumber)
            {
                case 1:
                    result.ClipboardEvent = reader.ReadBytes();
                    break;
                case 2:
                    result.ClientResolution = ClientResolution.Decode(reader.ReadLengthDelimitedSpan());
                    break;
                case 3:
                    result.CursorShape = reader.ReadBytes();
                    break;
                case 4:
                    result.VideoControl = VideoControl.Decode(reader.ReadLengthDelimitedSpan());
                    break;
                case 5:
                    result.AudioControl = AudioControl.Decode(reader.ReadLengthDelimitedSpan());
                    break;
                case 6:
                    result.Capabilities = Capabilities.Decode(reader.ReadLengthDelimitedSpan());
                    break;
                case 10:
                    result.VideoLayout = VideoLayout.Decode(reader.ReadLengthDelimitedSpan());
                    break;
                case 11:
                    result.SelectDesktopDisplay = SelectDesktopDisplayRequest.Decode(reader.ReadLengthDelimitedSpan());
                    break;
                default:
                    reader.SkipField(wireType);
                    break;
            }
        }
        return result;
    }
}

/// <summary>
/// remoting::protocol::KeyEvent
/// Tags:
/// 1: keycode
/// 2: pressed
/// 3: usb_keycode
/// 4: lock_states
/// 5: caps_lock_state
/// 6: num_lock_state
/// </summary>
public sealed class KeyEvent
{
    public int? Keycode { get; set; }
    public bool? Pressed { get; set; }
    public uint? UsbKeycode { get; set; }
    public uint? LockStates { get; set; }
    public bool? CapsLockState { get; set; }
    public bool? NumLockState { get; set; }

    public byte[] Encode()
    {
        var writer = new ProtobufWriter();
        if (Keycode.HasValue) writer.WriteInt32(1, Keycode.Value);
        if (Pressed.HasValue) writer.WriteBool(2, Pressed.Value);
        if (UsbKeycode.HasValue) writer.WriteUInt32(3, UsbKeycode.Value);
        if (LockStates.HasValue) writer.WriteUInt32(4, LockStates.Value);
        if (CapsLockState.HasValue) writer.WriteBool(5, CapsLockState.Value);
        if (NumLockState.HasValue) writer.WriteBool(6, NumLockState.Value);
        return writer.ToByteArray();
    }

    public static KeyEvent Decode(ReadOnlySpan<byte> data)
    {
        var result = new KeyEvent();
        var reader = new ProtobufReader(data);
        while (reader.ReadTag(out int fieldNumber, out int wireType))
        {
            switch (fieldNumber)
            {
                case 1: result.Keycode = reader.ReadInt32(); break;
                case 2: result.Pressed = reader.ReadBool(); break;
                case 3: result.UsbKeycode = reader.ReadUInt32(); break;
                case 4: result.LockStates = reader.ReadUInt32(); break;
                case 5: result.CapsLockState = reader.ReadBool(); break;
                case 6: result.NumLockState = reader.ReadBool(); break;
                default: reader.SkipField(wireType); break;
            }
        }
        return result;
    }
}

/// <summary>
/// remoting::protocol::FractionalCoordinate
/// Tags:
/// 1: x float
/// 2: y float
/// 3: screen_id int64
/// </summary>
public sealed class FractionalCoordinate
{
    public float? X { get; set; }
    public float? Y { get; set; }
    public long? ScreenId { get; set; }

    public byte[] Encode()
    {
        var writer = new ProtobufWriter();
        if (X.HasValue) writer.WriteFloat(1, X.Value);
        if (Y.HasValue) writer.WriteFloat(2, Y.Value);
        if (ScreenId.HasValue) writer.WriteInt64(3, ScreenId.Value);
        return writer.ToByteArray();
    }

    public static FractionalCoordinate Decode(ReadOnlySpan<byte> data)
    {
        var result = new FractionalCoordinate();
        var reader = new ProtobufReader(data);
        while (reader.ReadTag(out int fieldNumber, out int wireType))
        {
            switch (fieldNumber)
            {
                case 1: result.X = reader.ReadFloat(); break;
                case 2: result.Y = reader.ReadFloat(); break;
                case 3: result.ScreenId = reader.ReadInt64(); break;
                default: reader.SkipField(wireType); break;
            }
        }
        return result;
    }
}

/// <summary>
/// remoting::protocol::MouseEvent
/// Tags:
/// 1: x
/// 2: y
/// 5: button
/// 6: button_down
/// 7: wheel_delta_x
/// 8: wheel_delta_y
/// 9: wheel_ticks_x
/// 10: wheel_ticks_y
/// 11: delta_x
/// 12: delta_y
/// 13: fractional_coordinate
/// </summary>
public sealed class MouseEvent
{
    public enum MouseButton
    {
        Undefined = 0,
        Left = 1,
        Middle = 2,
        Right = 3,
        Back = 4,
        Forward = 5
    }

    public int? X { get; set; }
    public int? Y { get; set; }
    public MouseButton? Button { get; set; }
    public bool? ButtonDown { get; set; }
    public float? WheelDeltaX { get; set; }
    public float? WheelDeltaY { get; set; }
    public float? WheelTicksX { get; set; }
    public float? WheelTicksY { get; set; }
    public int? DeltaX { get; set; }
    public int? DeltaY { get; set; }
    public FractionalCoordinate? FractionalCoordinate { get; set; }

    public byte[] Encode()
    {
        var writer = new ProtobufWriter();
        if (X.HasValue) writer.WriteInt32(1, X.Value);
        if (Y.HasValue) writer.WriteInt32(2, Y.Value);
        if (Button.HasValue) writer.WriteInt32(5, (int)Button.Value);
        if (ButtonDown.HasValue) writer.WriteBool(6, ButtonDown.Value);
        if (WheelDeltaX.HasValue) writer.WriteFloat(7, WheelDeltaX.Value);
        if (WheelDeltaY.HasValue) writer.WriteFloat(8, WheelDeltaY.Value);
        if (WheelTicksX.HasValue) writer.WriteFloat(9, WheelTicksX.Value);
        if (WheelTicksY.HasValue) writer.WriteFloat(10, WheelTicksY.Value);
        if (DeltaX.HasValue) writer.WriteInt32(11, DeltaX.Value);
        if (DeltaY.HasValue) writer.WriteInt32(12, DeltaY.Value);
        if (FractionalCoordinate is not null)
            writer.WriteBytes(13, FractionalCoordinate.Encode());
        return writer.ToByteArray();
    }

    public static MouseEvent Decode(ReadOnlySpan<byte> data)
    {
        var result = new MouseEvent();
        var reader = new ProtobufReader(data);
        while (reader.ReadTag(out int fieldNumber, out int wireType))
        {
            switch (fieldNumber)
            {
                case 1: result.X = reader.ReadInt32(); break;
                case 2: result.Y = reader.ReadInt32(); break;
                case 5: result.Button = (MouseButton)reader.ReadInt32(); break;
                case 6: result.ButtonDown = reader.ReadBool(); break;
                case 7: result.WheelDeltaX = reader.ReadFloat(); break;
                case 8: result.WheelDeltaY = reader.ReadFloat(); break;
                case 9: result.WheelTicksX = reader.ReadFloat(); break;
                case 10: result.WheelTicksY = reader.ReadFloat(); break;
                case 11: result.DeltaX = reader.ReadInt32(); break;
                case 12: result.DeltaY = reader.ReadInt32(); break;
                case 13: result.FractionalCoordinate = FractionalCoordinate.Decode(reader.ReadLengthDelimitedSpan()); break;
                default: reader.SkipField(wireType); break;
            }
        }
        return result;
    }
}

/// <summary>
/// remoting::protocol::EventMessage
/// Tags:
/// 1: timestamp int64
/// 3: key_event
/// 4: mouse_event
/// </summary>
public sealed class EventMessage
{
    public long? Timestamp { get; set; }
    public KeyEvent? KeyEvent { get; set; }
    public MouseEvent? MouseEvent { get; set; }

    public byte[] Encode(bool framed = false)
    {
        var writer = new ProtobufWriter();
        if (Timestamp.HasValue) writer.WriteInt64(1, Timestamp.Value);
        if (KeyEvent is not null) writer.WriteBytes(3, KeyEvent.Encode());
        if (MouseEvent is not null) writer.WriteBytes(4, MouseEvent.Encode());

        byte[] payload = writer.ToByteArray();
        return framed ? ChromotingProtobuf.FrameMessage(payload) : payload;
    }

    public static EventMessage Decode(ReadOnlySpan<byte> data, bool framed = false)
    {
        if (framed) data = ChromotingProtobuf.UnframeMessage(data);

        var result = new EventMessage();
        var reader = new ProtobufReader(data);
        while (reader.ReadTag(out int fieldNumber, out int wireType))
        {
            switch (fieldNumber)
            {
                case 1: result.Timestamp = reader.ReadInt64(); break;
                case 3: result.KeyEvent = KeyEvent.Decode(reader.ReadLengthDelimitedSpan()); break;
                case 4: result.MouseEvent = MouseEvent.Decode(reader.ReadLengthDelimitedSpan()); break;
                default: reader.SkipField(wireType); break;
            }
        }
        return result;
    }
}

#endregion

#region USB HID Keycode Mapper

/// <summary>
/// Complete USB HID Usage Page 0x07 (Keyboard/Keypad) mapper for Windows Virtual Keys (VK_*)
/// and system chords (Ctrl+Alt+Del, Win+L, Win+D, Ctrl+Shift+Esc).
/// Encodes keycodes into Chromium's exact format: (0x070000 | usage).
/// </summary>
public static class UsbHidKeycodeMapper
{
    public const uint PageKeyboard = 0x070000;

    // Modifiers
    public const uint UsbLeftControl = PageKeyboard | 0xE0;
    public const uint UsbLeftShift = PageKeyboard | 0xE1;
    public const uint UsbLeftAlt = PageKeyboard | 0xE2;
    public const uint UsbLeftGui = PageKeyboard | 0xE3;
    public const uint UsbRightControl = PageKeyboard | 0xE4;
    public const uint UsbRightShift = PageKeyboard | 0xE5;
    public const uint UsbRightAlt = PageKeyboard | 0xE6;
    public const uint UsbRightGui = PageKeyboard | 0xE7;

    // Keypad and Special
    public const uint UsbEnter = PageKeyboard | 0x28;
    public const uint UsbEscape = PageKeyboard | 0x29;
    public const uint UsbBackspace = PageKeyboard | 0x2A;
    public const uint UsbTab = PageKeyboard | 0x2B;
    public const uint UsbSpace = PageKeyboard | 0x2C;
    public const uint UsbDelete = PageKeyboard | 0x4C;
    public const uint UsbCapsLock = PageKeyboard | 0x39;
    public const uint UsbNumLock = PageKeyboard | 0x53;
    public const uint UsbScrollLock = PageKeyboard | 0x47;
    public const uint UsbPrintScreen = PageKeyboard | 0x46;
    public const uint UsbPause = PageKeyboard | 0x48;
    public const uint UsbInsert = PageKeyboard | 0x49;
    public const uint UsbHome = PageKeyboard | 0x4A;
    public const uint UsbPageUp = PageKeyboard | 0x4B;
    public const uint UsbEnd = PageKeyboard | 0x4D;
    public const uint UsbPageDown = PageKeyboard | 0x4E;
    public const uint UsbRightArrow = PageKeyboard | 0x4F;
    public const uint UsbLeftArrow = PageKeyboard | 0x50;
    public const uint UsbDownArrow = PageKeyboard | 0x51;
    public const uint UsbUpArrow = PageKeyboard | 0x52;

    public enum SystemChord
    {
        CtrlAltDel,
        WinL,
        WinD,
        CtrlShiftEsc
    }

    /// <summary>
    /// Maps a Win32 Virtual Key code (VK_*) to the corresponding USB HID 32-bit keycode (0x070000 | usage).
    /// </summary>
    public static uint FromVirtualKey(int vkKey, bool isExtended = false)
    {
        // Letters A-Z (0x41 - 0x5A) -> HID 0x04 - 0x1D
        if (vkKey >= 0x41 && vkKey <= 0x5A)
        {
            return PageKeyboard | (uint)(0x04 + (vkKey - 0x41));
        }

        // Numbers 1-9 (0x31 - 0x39) -> HID 0x1E - 0x26
        if (vkKey >= 0x31 && vkKey <= 0x39)
        {
            return PageKeyboard | (uint)(0x1E + (vkKey - 0x31));
        }

        // Number 0 (0x30) -> HID 0x27
        if (vkKey == 0x30) return PageKeyboard | 0x27;

        // Function keys F1-F12 (0x70 - 0x7B) -> HID 0x3A - 0x45
        if (vkKey >= 0x70 && vkKey <= 0x7B)
        {
            return PageKeyboard | (uint)(0x3A + (vkKey - 0x70));
        }

        // Function keys F13-F24 (0x7C - 0x87) -> HID 0x68 - 0x73
        if (vkKey >= 0x7C && vkKey <= 0x87)
        {
            return PageKeyboard | (uint)(0x68 + (vkKey - 0x7C));
        }

        // Numpad 0-9 (0x60 - 0x69) -> HID 0x62 (0), 0x59 - 0x61 (1-9)
        if (vkKey == 0x60) return PageKeyboard | 0x62;
        if (vkKey >= 0x61 && vkKey <= 0x69)
        {
            return PageKeyboard | (uint)(0x59 + (vkKey - 0x61));
        }

        return vkKey switch
        {
            0x08 => UsbBackspace,
            0x09 => UsbTab,
            0x0D => isExtended ? (PageKeyboard | 0x58) : UsbEnter, // Numpad Enter vs Enter
            0x10 or 0xA0 => UsbLeftShift,
            0xA1 => UsbRightShift,
            0x11 or 0xA2 => isExtended ? UsbRightControl : UsbLeftControl,
            0xA3 => UsbRightControl,
            0x12 or 0xA4 => isExtended ? UsbRightAlt : UsbLeftAlt,
            0xA5 => UsbRightAlt,
            0x13 => UsbPause,
            0x14 => UsbCapsLock,
            0x1B => UsbEscape,
            0x20 => UsbSpace,
            0x21 => UsbPageUp,
            0x22 => UsbPageDown,
            0x23 => UsbEnd,
            0x24 => UsbHome,
            0x25 => UsbLeftArrow,
            0x26 => UsbUpArrow,
            0x27 => UsbRightArrow,
            0x28 => UsbDownArrow,
            0x2C => UsbPrintScreen,
            0x2D => UsbInsert,
            0x2E => UsbDelete,
            0x5B => UsbLeftGui,
            0x5C => UsbRightGui,
            0x5D => PageKeyboard | 0x65, // ContextMenu / Apps
            0x6A => PageKeyboard | 0x55, // Numpad Multiply
            0x6B => PageKeyboard | 0x57, // Numpad Add
            0x6D => PageKeyboard | 0x56, // Numpad Subtract
            0x6E => PageKeyboard | 0x63, // Numpad Decimal
            0x6F => PageKeyboard | 0x54, // Numpad Divide
            0x90 => UsbNumLock,
            0x91 => UsbScrollLock,
            0xBA => PageKeyboard | 0x33, // Semicolon / Colon
            0xBB => PageKeyboard | 0x2E, // Equals / Plus
            0xBC => PageKeyboard | 0x36, // Comma / Less
            0xBD => PageKeyboard | 0x2D, // Minus / Underscore
            0xBE => PageKeyboard | 0x37, // Period / Greater
            0xBF => PageKeyboard | 0x38, // Slash / Question
            0xC0 => PageKeyboard | 0x35, // Grave / Tilde
            0xDB => PageKeyboard | 0x2F, // Left Bracket / Brace
            0xDC => PageKeyboard | 0x31, // Backslash / Pipe
            0xDD => PageKeyboard | 0x30, // Right Bracket / Brace
            0xDE => PageKeyboard | 0x34, // Quote / Double Quote
            _ => 0
        };
    }

    /// <summary>
    /// Returns the sequence of USB HID keycodes required to trigger a system chord.
    /// </summary>
    public static uint[] GetChordUsbKeycodes(SystemChord chord)
    {
        return chord switch
        {
            SystemChord.CtrlAltDel => new[] { UsbLeftControl, UsbLeftAlt, UsbDelete },
            SystemChord.WinL => new[] { UsbLeftGui, PageKeyboard | 0x0F /* L */ },
            SystemChord.WinD => new[] { UsbLeftGui, PageKeyboard | 0x07 /* D */ },
            SystemChord.CtrlShiftEsc => new[] { UsbLeftControl, UsbLeftShift, UsbEscape },
            _ => Array.Empty<uint>()
        };
    }

    /// <summary>
    /// Builds full KeyDown then KeyUp EventMessages for executing a chord over the SCTP event channel.
    /// </summary>
    public static List<EventMessage> BuildChordEventMessages(SystemChord chord, bool framed = false)
    {
        uint[] keys = GetChordUsbKeycodes(chord);
        var messages = new List<EventMessage>(keys.Length * 2);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Key down in order
        foreach (uint usbKey in keys)
        {
            messages.Add(new EventMessage
            {
                Timestamp = now,
                KeyEvent = new KeyEvent
                {
                    UsbKeycode = usbKey,
                    Pressed = true
                }
            });
        }

        // Key up in reverse order
        for (int i = keys.Length - 1; i >= 0; i--)
        {
            messages.Add(new EventMessage
            {
                Timestamp = now + 1,
                KeyEvent = new KeyEvent
                {
                    UsbKeycode = keys[i],
                    Pressed = false
                }
            });
        }

        return messages;
    }
}

#endregion
