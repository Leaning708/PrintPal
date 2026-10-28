using System.Buffers.Binary;
using System.Text;

namespace PrintPal.IppProbe.Ipp;

/// <summary>
/// The subset of IPP/1.1 + IPP/2.0 tags this probe needs.
/// Values are kept as raw bytes so the probe can log exactly what the Windows
/// IPP class driver sends without guessing its encoding.
/// </summary>
public enum IppTag : byte
{
    OperationAttributes = 0x01,
    JobAttributes = 0x02,
    EndOfAttributes = 0x03,
    PrinterAttributes = 0x04,
    UnsupportedAttributes = 0x05,

    Integer = 0x21,
    Boolean = 0x22,
    Enum = 0x23,
    OctetString = 0x30,
    DateTime = 0x31,
    Resolution = 0x32,
    RangeOfInteger = 0x33,
    BegCollection = 0x34,
    TextWithLanguage = 0x35,
    NameWithLanguage = 0x36,
    EndCollection = 0x37,
    TextWithoutLanguage = 0x41,
    NameWithoutLanguage = 0x42,
    Keyword = 0x44,
    Uri = 0x45,
    UriScheme = 0x46,
    Charset = 0x47,
    NaturalLanguage = 0x48,
    MimeMediaType = 0x49,
    MemberAttrName = 0x4A,
}

public sealed class IppAttribute
{
    public byte Group { get; init; }
    public IppTag Tag { get; init; }
    public string Name { get; init; } = string.Empty;
    public List<byte[]> Values { get; } = new();

    public string StringValue =>
        Values.Count == 0 ? string.Empty : Encoding.UTF8.GetString(Values[0]);

    public IReadOnlyList<string> StringValues =>
        Values.Select(Encoding.UTF8.GetString).ToArray();

    public int IntValue =>
        Values.Count > 0 && Values[0].Length == 4
            ? BinaryPrimitives.ReadInt32BigEndian(Values[0])
            : 0;
}

/// <summary>One IPP collection value, including nested collections.</summary>
public sealed class IppCollection
{
    public string Name { get; init; } = string.Empty;
    public Dictionary<string, List<IppAttribute>> Members { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<IppCollection> Collections { get; } = new();

    public IppAttribute? Get(string name) =>
        Members.TryGetValue(name, out var list) ? list.FirstOrDefault() : null;

    public string? GetString(string name) => Get(name)?.StringValue;

    public int GetInt(string name, int fallback = 0) => Get(name)?.IntValue ?? fallback;

    public IppCollection? GetCollection(string name) =>
        Collections.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

public sealed class IppRequest
{
    public byte VersionMajor { get; private set; }
    public byte VersionMinor { get; private set; }
    public ushort OperationId { get; private set; }
    public uint RequestId { get; private set; }
    public List<IppAttribute> Attributes { get; } = new();
    public byte[] Body { get; private set; } = Array.Empty<byte>();
    public int DocumentOffset { get; private set; }

    public ReadOnlySpan<byte> Document =>
        DocumentOffset >= Body.Length ? ReadOnlySpan<byte>.Empty : Body.AsSpan(DocumentOffset);

    public int DocumentLength => Math.Max(0, Body.Length - DocumentOffset);

    public static string OperationName(ushort operationId) => operationId switch
    {
        0x0002 => "Print-Job",
        0x0004 => "Validate-Job",
        0x0005 => "Create-Job",
        0x0006 => "Send-Document",
        0x0007 => "Send-URI",
        0x0008 => "Cancel-Job",
        0x0009 => "Get-Job-Attributes",
        0x000A => "Get-Jobs",
        0x000B => "Get-Printer-Attributes",
        0x000C => "Hold-Job",
        0x000D => "Release-Job",
        0x000E => "Restart-Job",
        0x0010 => "Pause-Printer",
        0x0011 => "Resume-Printer",
        0x0012 => "Purge-Jobs",
        _ => $"0x{operationId:X4}",
    };

    public string OperationNameText => OperationName(OperationId);

    public IppAttribute? Get(string name) =>
        Attributes.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<IppAttribute> GetAll(string name) =>
        Attributes.Where(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    public string? GetString(string name) => Get(name)?.StringValue;

    public int GetInt(string name, int fallback = 0)
    {
        var attr = Get(name);
        return attr is null ? fallback : attr.IntValue;
    }

    /// <summary>
    /// Top-level IPP collections, in wire order. Windows' IPP Class Driver sends
    /// media as media-col/media-size collections rather than the media keyword.
    /// </summary>
    public List<IppCollection> Collections { get; } = new();

    public IppCollection? GetCollection(string name) =>
        Collections.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    public static IppRequest Parse(byte[] body)
    {
        if (body.Length < 8)
        {
            throw new InvalidDataException("IPP body is shorter than the 8-byte header.");
        }

        var request = new IppRequest { Body = body };
        var pos = 0;
        request.VersionMajor = body[pos++];
        request.VersionMinor = body[pos++];
        request.OperationId = ReadUInt16(body, ref pos);
        request.RequestId = ReadUInt32(body, ref pos);

        byte group = 0;
        IppAttribute? last = null;

        while (pos < body.Length)
        {
            var tag = body[pos++];

            if (tag == (byte)IppTag.EndOfAttributes)
            {
                break;
            }

            // Group delimiter tags are all below 0x10.
            if (tag < 0x10)
            {
                group = tag;
                last = null;
                continue;
            }

            if (tag == (byte)IppTag.BegCollection)
            {
                var (collectionName, collectionValueLength) = ReadAttributeHeader(body, ref pos);
                pos += collectionValueLength;
                var collection = ParseCollection(body, ref pos, group, collectionName);
                request.Collections.Add(collection);
                last = null;
                continue;
            }

            if (tag == (byte)IppTag.EndCollection)
            {
                // Should only occur inside ParseCollection; tolerate a stray one.
                var (_, strayValueLength) = ReadAttributeHeader(body, ref pos);
                pos += strayValueLength;
                last = null;
                continue;
            }

            var (name, valueLength) = ReadAttributeHeader(body, ref pos);
            var value = new byte[valueLength];
            if (valueLength > 0)
            {
                Array.Copy(body, pos, value, 0, valueLength);
            }
            pos += valueLength;

            // IPP multi-valued attributes repeat with a zero-length name. Append to
            // the previous attribute instead of creating a nameless one.
            if (name.Length == 0 && last is not null)
            {
                last.Values.Add(value);
                continue;
            }

            last = new IppAttribute
            {
                Group = group,
                Tag = (IppTag)tag,
                Name = name,
            };
            last.Values.Add(value);
            request.Attributes.Add(last);
        }

        request.DocumentOffset = pos;
        return request;
    }

    /// <summary>
    /// Parses a collection body after the begCollection tag. Called recursively for
    /// nested collections such as media-col -> media-size.
    /// </summary>
    private static IppCollection ParseCollection(byte[] body, ref int pos, byte group, string name)
    {
        var collection = new IppCollection { Name = name };
        string? memberName = null;

        while (pos < body.Length)
        {
            var tag = body[pos++];

            if (tag == (byte)IppTag.EndCollection)
            {
                var (_, valueLength) = ReadAttributeHeader(body, ref pos);
                pos += valueLength;
                return collection;
            }

            if (tag == (byte)IppTag.MemberAttrName)
            {
                var (_, valueLength) = ReadAttributeHeader(body, ref pos);
                memberName = valueLength == 0
                    ? string.Empty
                    : Encoding.UTF8.GetString(body, pos, valueLength);
                pos += valueLength;
                continue;
            }

            if (tag == (byte)IppTag.BegCollection)
            {
                var (_, nestedValueLength) = ReadAttributeHeader(body, ref pos);
                pos += nestedValueLength;
                var nested = ParseCollection(body, ref pos, group, memberName ?? string.Empty);
                collection.Collections.Add(nested);
                continue;
            }

            var (_, attributeValueLength) = ReadAttributeHeader(body, ref pos);
            var value = new byte[attributeValueLength];
            if (attributeValueLength > 0)
            {
                Array.Copy(body, pos, value, 0, attributeValueLength);
            }
            pos += attributeValueLength;

            if (memberName is null)
            {
                continue;
            }

            if (!collection.Members.TryGetValue(memberName, out var list))
            {
                list = new List<IppAttribute>();
                collection.Members[memberName] = list;
            }

            var attribute = new IppAttribute
            {
                Group = group,
                Tag = (IppTag)tag,
                Name = memberName,
            };
            attribute.Values.Add(value);
            list.Add(attribute);
        }

        return collection;
    }

    private static (string Name, ushort ValueLength) ReadAttributeHeader(byte[] data, ref int pos)
    {
        var nameLength = ReadUInt16(data, ref pos);
        var name = nameLength == 0
            ? string.Empty
            : Encoding.UTF8.GetString(data, pos, nameLength);
        pos += nameLength;
        var valueLength = ReadUInt16(data, ref pos);
        return (name, valueLength);
    }

    private static ushort ReadUInt16(byte[] data, ref int pos)
    {
        var value = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2));
        pos += 2;
        return value;
    }

    private static uint ReadUInt32(byte[] data, ref int pos)
    {
        var value = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4));
        pos += 4;
        return value;
    }
}

/// <summary>Builds an IPP response body with the big-endian wire format.</summary>
public sealed class IppResponseBuilder
{
    private readonly MemoryStream _stream = new();

    public IppResponseBuilder(byte versionMajor, byte versionMinor, ushort statusCode, uint requestId)
    {
        _stream.WriteByte(versionMajor);
        _stream.WriteByte(versionMinor);
        WriteUInt16(statusCode);
        WriteUInt32(requestId);
    }

    public void BeginGroup(IppTag group) => _stream.WriteByte((byte)group);

    public void Add(IppTag tag, string name, byte[] value)
    {
        _stream.WriteByte((byte)tag);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        WriteUInt16(checked((ushort)nameBytes.Length));
        _stream.Write(nameBytes);
        WriteUInt16(checked((ushort)value.Length));
        _stream.Write(value);
    }

    public void AddString(IppTag tag, string name, string value) =>
        Add(tag, name, Encoding.UTF8.GetBytes(value));

    public void AddInt(IppTag tag, string name, int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        Add(tag, name, bytes);
    }

    public void AddBool(IppTag tag, string name, bool value) =>
        Add(tag, name, new[] { value ? (byte)1 : (byte)0 });

    public void AddRange(IppTag tag, string name, int lower, int upper)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(0, 4), lower);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4, 4), upper);
        Add(tag, name, bytes);
    }

    public void AddResolution(IppTag tag, string name, int xDpi, int yDpi, byte units)
    {
        var bytes = new byte[9];
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(0, 4), xDpi);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4, 4), yDpi);
        bytes[8] = units;
        Add(tag, name, bytes);
    }

    /// <summary>
    /// Starts an IPP collection value (tag 0x34). A collection is encoded as
    /// begCollection, then repeated memberAttrName + value pairs, then endCollection.
    /// </summary>
    public void BeginCollection(IppTag tag, string name)
    {
        _stream.WriteByte((byte)tag);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        WriteUInt16(checked((ushort)nameBytes.Length));
        _stream.Write(nameBytes);
        WriteUInt16(0);
    }

    /// <summary>Adds one member to the collection currently being encoded.</summary>
    public void AddCollectionMember(string memberName, IppTag valueTag, byte[] value)
    {
        _stream.WriteByte((byte)IppTag.MemberAttrName);
        WriteUInt16(0);
        var memberBytes = Encoding.UTF8.GetBytes(memberName);
        WriteUInt16(checked((ushort)memberBytes.Length));
        _stream.Write(memberBytes);

        _stream.WriteByte((byte)valueTag);
        WriteUInt16(0);
        WriteUInt16(checked((ushort)value.Length));
        _stream.Write(value);
    }

    public void AddCollectionMemberInt(string memberName, int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        AddCollectionMember(memberName, IppTag.Integer, bytes);
    }

    public void AddCollectionMemberKeyword(string memberName, string value) =>
        AddCollectionMember(memberName, IppTag.Keyword, Encoding.UTF8.GetBytes(value));

    public void EndCollection()
    {
        _stream.WriteByte((byte)IppTag.EndCollection);
        WriteUInt16(0);
        WriteUInt16(0);
    }

    public byte[] End()
    {
        _stream.WriteByte((byte)IppTag.EndOfAttributes);
        return _stream.ToArray();
    }

    private void WriteUInt16(ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        _stream.Write(buffer);
    }

    private void WriteUInt32(uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        _stream.Write(buffer);
    }
}
