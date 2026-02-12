using System.IO;
using System.Text;

namespace SkipVersionCheck;

/// <summary>
/// Utility for constructing Terraria network packets.
/// Packet format: [short length][byte type][payload...]
/// Ported from the Crossplay plugin by Moneylover3246.
/// </summary>
public class PacketFactory
{
    private readonly MemoryStream _stream;
    private readonly BinaryWriter _writer;

    public PacketFactory(bool writeOffset = true)
    {
        _stream = new MemoryStream();
        _writer = new BinaryWriter(_stream);
        if (writeOffset)
        {
            // Skip past the 3-byte header (2 length + 1 type) to start writing payload
            _writer.BaseStream.Position = 3L;
        }
    }

    public PacketFactory SetType(short type)
    {
        long currentPosition = _writer.BaseStream.Position;
        _writer.BaseStream.Position = 2L;
        _writer.Write(type);
        _writer.BaseStream.Position = currentPosition;
        return this;
    }

    public PacketFactory PackString(string str)
    {
        _writer.Write(str);
        return this;
    }

    public PacketFactory PackByte(byte num)
    {
        _writer.Write(num);
        return this;
    }

    public PacketFactory PackInt16(short num)
    {
        _writer.Write(num);
        return this;
    }

    public PacketFactory PackInt32(int num)
    {
        _writer.Write(num);
        return this;
    }

    public PacketFactory PackSingle(float num)
    {
        _writer.Write(num);
        return this;
    }

    public PacketFactory PackBuffer(byte[] buffer)
    {
        _writer.Write(buffer);
        return this;
    }

    private void UpdateLength()
    {
        long currentPosition = _writer.BaseStream.Position;
        _writer.BaseStream.Position = 0L;
        _writer.Write((short)currentPosition);
        _writer.BaseStream.Position = currentPosition;
    }

    public byte[] GetByteData()
    {
        UpdateLength();
        return _stream.ToArray();
    }
}
