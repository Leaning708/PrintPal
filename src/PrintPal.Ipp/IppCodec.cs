using System.Buffers.Binary;
using System.Text;

namespace PrintPal.Ipp;

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

    public IReadOnlyList<string> StringValues => Values.Select(Encoding.UTF8.GetString).ToArray();

    public int IntValue =>
        Values.Count > 0 && Values[0].Length == 4
            ? BinaryPrimitives.ReadInt32BigEndian(Values[0])
            : 0;

    public bool BoolValue => Values.Count > 0 && Values[0].Length > 0 && Values[0][0] != 0;

    public int RangeLower =>
        Values.Count > 0 && Values[0].Length == 8
            ? BinaryPrimitives.ReadInt32BigEndian(Values[0].AsSpan(0, 4))
            : 0;

    public int RangeUpper =>
        Values.Count > 0 && Values[0].Length == 8
            ? BinaryPrimitives.ReadInt32BigEndian(Values[0].AsSpan(4, 4))
            : 0;

    public int ResolutionX =>
        Values.Count > 0 && Values[0].Length >= 8
            ? BinaryPrimitives.ReadInt32BigEndian(Values[0].AsSpan(0, 4))
            : 0;

    public int ResolutionY =>
        Values.Count > 0 && Values[0].Length >= 8
            ? BinaryPrimitives.ReadInt32BigEndian(Values[0].AsSpan(4, 4))
            : 0;
}

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
    public byte VersionMajor { get; internal set; }

    public byte VersionMinor { get; internal set; }

    public ushort OperationId { get; internal set; }

    public uint RequestId { get; internal set; }

    public List<IppAttribute> Attributes { get; } = new();

    public List<IppCollection> Collections { get; } = new();

    public byte[] Body { get; internal set; } = Array.Empty<byte>();

    public int DocumentOffset { get; internal set; }

    public ReadOnlySpan<byte> Document =>
        DocumentOffset >= Body.Length ? ReadOnlySpan<byte>.Empty : Body.AsSpan(DocumentOffset);

    public int DocumentLength => Math.Max(0, Body.Length - DocumentOffset);

    public IppAttribute? Get(string name) =>
        Attributes.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    public string? GetString(string name) => Get(name)?.StringValue;

    public int GetInt(string name, int fallback = 0) => Get(name)?.IntValue ?? fallback;

    public IppCollection? GetCollection(string name) =>
        Collections.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    public static IppRequest Parse(byte[] body)
    {
        var parsed = IppParser.Parse(body);

        var request = new IppRequest
        {
            VersionMajor = parsed.VersionMajor,
            VersionMinor = parsed.VersionMinor,
            OperationId = (ushort)parsed.SecondField,
            RequestId = parsed.RequestId,
            Body = body,
            DocumentOffset = parsed.DocumentOffset,
        };

        request.Attributes.AddRange(parsed.Attributes);
        request.Collections.AddRange(parsed.Collections);
        return request;
    }
}

public sealed class IppResponse
{
    public byte VersionMajor { get; internal set; }

    public byte VersionMinor { get; internal set; }

    public ushort StatusCode { get; internal set; }

    public uint RequestId { get; internal set; }

    public List<IppAttribute> Attributes { get; } = new();

    public List<IppCollection> Collections { get; } = new();

    public string? StatusMessage => GetString("status-message");

    public IppAttribute? Get(string name) =>
        Attributes.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    public string? GetString(string name) => Get(name)?.StringValue;

    public int GetInt(string name, int fallback = 0) => Get(name)?.IntValue ?? fallback;

    public IppCollection? GetCollection(string name) =>
        Collections.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    public static IppResponse Parse(byte[] body)
    {
        var parsed = IppParser.Parse(body);
        var response = new IppResponse
        {
            VersionMajor = parsed.VersionMajor,
            VersionMinor = parsed.VersionMinor,
            StatusCode = (ushort)parsed.SecondField,
            RequestId = parsed.RequestId,
        };

        response.Attributes.AddRange(parsed.Attributes);
        response.Collections.AddRange(parsed.Collections);
        return response;
    }
}

internal sealed class ParsedIppMessage
{
    public byte VersionMajor { get; init; }
    public byte VersionMinor { get; init; }
    public ushort SecondField { get; init; }
    public uint RequestId { get; init; }
    public List<IppAttribute> Attributes { get; } = new();
    public List<IppCollection> Collections { get; } = new();
    public int DocumentOffset { get; set; }
}

internal static class IppParser
{
    public static ParsedIppMessage Parse(byte[] body)
    {
        if (body.Length < 8)
        {
            throw new InvalidDataException("IPP message is shorter than the 8-byte header.");
        }

        var parsed = new ParsedIppMessage
        {
            VersionMajor = body[0],
            VersionMinor = body[1],
            SecondField = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(2, 2)),
            RequestId = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(4, 4)),
        };

        var position = 8;
        byte group = 0;
        IppAttribute? last = null;
        string? lastCollectionName = null;

        while (position < body.Length)
        {
            var tag = body[position++];

            if (tag == (byte)IppTag.EndOfAttributes)
            {
                break;
            }

            if (tag < 0x10)
            {
                group = tag;
                last = null;
                continue;
            }

            if (tag == (byte)IppTag.BegCollection)
            {
                var (name, valueLength) = ReadAttributeHeader(body, ref position);
                position += valueLength;

                // Repeated collection values carry an empty name after the first;
                // inherit the previous collection's attribute name.
                if (name.Length == 0 && lastCollectionName is not null)
                {
                    name = lastCollectionName;
                }
                else
                {
                    lastCollectionName = name;
                }

                var collection = ParseCollection(body, ref position, group, name);
                parsed.Collections.Add(collection);
                last = null;
                continue;
            }

            if (tag == (byte)IppTag.EndCollection)
            {
                var (_, strayValueLength) = ReadAttributeHeader(body, ref position);
                position += strayValueLength;
                last = null;
                continue;
            }

            var (attrName, attrValueLength) = ReadAttributeHeader(body, ref position);
            var value = new byte[attrValueLength];
            if (attrValueLength > 0)
            {
                Array.Copy(body, position, value, 0, attrValueLength);
            }
            position += attrValueLength;

            if (attrName.Length == 0 && last is not null)
            {
                last.Values.Add(value);
                continue;
            }

            last = new IppAttribute
            {
                Group = group,
                Tag = (IppTag)tag,
                Name = attrName,
            };
            last.Values.Add(value);
            parsed.Attributes.Add(last);
        }

        parsed.DocumentOffset = position;
        return parsed;
    }

    private static IppCollection ParseCollection(
        byte[] body,
        ref int position,
        byte group,
        string name)
    {
        var collection = new IppCollection { Name = name };
        string? memberName = null;

        while (position < body.Length)
        {
            var tag = body[position++];

            if (tag == (byte)IppTag.EndCollection)
            {
                var (_, valueLength) = ReadAttributeHeader(body, ref position);
                position += valueLength;
                return collection;
            }

            if (tag == (byte)IppTag.MemberAttrName)
            {
                var (_, valueLength) = ReadAttributeHeader(body, ref position);
                memberName = valueLength == 0
                    ? string.Empty
                    : Encoding.UTF8.GetString(body, position, valueLength);
                position += valueLength;
                continue;
            }

            if (tag == (byte)IppTag.BegCollection)
            {
                var (_, nestedValueLength) = ReadAttributeHeader(body, ref position);
                position += nestedValueLength;
                var nested = ParseCollection(body, ref position, group, memberName ?? string.Empty);
                collection.Collections.Add(nested);
                continue;
            }

            var (_, attrValueLength) = ReadAttributeHeader(body, ref position);
            var value = new byte[attrValueLength];
            if (attrValueLength > 0)
            {
                Array.Copy(body, position, value, 0, attrValueLength);
            }
            position += attrValueLength;

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

    private static (string Name, ushort ValueLength) ReadAttributeHeader(byte[] data, ref int position)
    {
        var nameLength = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(position, 2));
        position += 2;

        var name = nameLength == 0
            ? string.Empty
            : Encoding.UTF8.GetString(data, position, nameLength);
        position += nameLength;

        var valueLength = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(position, 2));
        position += 2;

        return (name, valueLength);
    }
}

/// <summary>Builds an IPP request or response with the big-endian wire format.</summary>
public sealed class IppMessageBuilder
{
    private readonly MemoryStream _stream = new();

    public IppMessageBuilder(byte versionMajor, byte versionMinor, ushort operationOrStatus, uint requestId)
    {
        _stream.WriteByte(versionMajor);
        _stream.WriteByte(versionMinor);
        WriteUInt16(operationOrStatus);
        WriteUInt32(requestId);
    }

    public void BeginGroup(IppTag group) => _stream.WriteByte((byte)group);

    public void BeginOperationAttributes()
    {
        BeginGroup(IppTag.OperationAttributes);
        AddString(IppTag.Charset, "attributes-charset", "utf-8");
        AddString(IppTag.NaturalLanguage, "attributes-natural-language", "en");
    }

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

    public void BeginCollection(IppTag tag, string name)
    {
        _stream.WriteByte((byte)tag);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        WriteUInt16(checked((ushort)nameBytes.Length));
        _stream.Write(nameBytes);
        WriteUInt16(0);
    }

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
