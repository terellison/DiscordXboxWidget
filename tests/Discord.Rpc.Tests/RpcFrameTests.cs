using System.Text;
using Discord.Rpc.Protocol;

namespace Discord.Rpc.Tests;

public class RpcFrameTests
{
    [Fact]
    public void ToBytes_WritesLittleEndianHeaderThenUtf8Payload()
    {
        var bytes = new RpcFrame(RpcOpcode.Frame, "{\"a\":1}").ToBytes();

        Assert.Equal(new byte[] { 1, 0, 0, 0 }, bytes[..4]);
        Assert.Equal(new byte[] { 7, 0, 0, 0 }, bytes[4..8]);
        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString(bytes[8..]));
    }

    [Fact]
    public void HeaderLength_CountsUtf8BytesNotCharacters()
    {
        // A multi-byte payload whose char count differs from its byte count. Getting this
        // wrong misaligns every subsequent frame on the pipe rather than failing outright.
        const string payload = "{\"n\":\"Tennieé✨\"}";
        var bytes = new RpcFrame(RpcOpcode.Frame, payload).ToBytes();

        var declared = bytes[4] | (bytes[5] << 8) | (bytes[6] << 16) | (bytes[7] << 24);

        Assert.Equal(Encoding.UTF8.GetByteCount(payload), declared);
        Assert.NotEqual(payload.Length, declared);
        Assert.Equal(RpcFrame.HeaderSize + declared, bytes.Length);
    }

    [Fact]
    public void ParseHeader_RoundTripsOpcodeAndLength()
    {
        var bytes = new RpcFrame(RpcOpcode.Handshake, "{}").ToBytes();

        RpcFrame.ParseHeader(bytes, out var opcode, out var length);

        Assert.Equal(RpcOpcode.Handshake, opcode);
        Assert.Equal(2, length);
    }

    [Fact]
    public void ParseHeader_RejectsNegativeLength()
    {
        // A negative length would otherwise reach Array allocation as a huge unsigned value.
        var header = new byte[] { 1, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF };

        Assert.Throws<InvalidOperationException>(
            () => RpcFrame.ParseHeader(header, out _, out _));
    }

    [Fact]
    public void ParseHeader_RejectsOversizedLength()
    {
        var oversized = BitConverter.GetBytes(RpcFrame.MaxPayloadSize + 1);
        var header = new byte[] { 1, 0, 0, 0, oversized[0], oversized[1], oversized[2], oversized[3] };

        Assert.Throws<InvalidOperationException>(
            () => RpcFrame.ParseHeader(header, out _, out _));
    }

    [Fact]
    public void ParseHeader_RejectsShortBuffer()
    {
        Assert.Throws<ArgumentException>(
            () => RpcFrame.ParseHeader(new byte[] { 1, 0, 0 }, out _, out _));
    }

    [Fact]
    public void ToBytes_RejectsPayloadOverTheFrameLimit()
    {
        var frame = new RpcFrame(RpcOpcode.Frame, new string('x', RpcFrame.MaxPayloadSize + 1));

        Assert.Throws<InvalidOperationException>(() => frame.ToBytes());
    }
}
