using System;
using System.Text;

namespace Discord.Rpc.Protocol
{
    /// <summary>
    /// A single Discord IPC frame: an 8-byte little-endian header
    /// (opcode, payload length) followed by a UTF-8 JSON payload.
    /// </summary>
    public readonly struct RpcFrame
    {
        public const int HeaderSize = 8;

        /// <summary>
        /// Discord closes the connection on oversized frames; we reject them before
        /// allocating so a malformed length prefix can't drive a huge allocation.
        /// </summary>
        public const int MaxPayloadSize = 64 * 1024;

        public RpcOpcode Opcode { get; }
        public string Payload { get; }

        public RpcFrame(RpcOpcode opcode, string payload)
        {
            Opcode = opcode;
            Payload = payload ?? throw new ArgumentNullException(nameof(payload));
        }

        public byte[] ToBytes()
        {
            var body = Encoding.UTF8.GetBytes(Payload);
            if (body.Length > MaxPayloadSize)
                throw new InvalidOperationException($"Payload of {body.Length} bytes exceeds the {MaxPayloadSize} byte frame limit.");

            var buffer = new byte[HeaderSize + body.Length];
            WriteInt32LE(buffer, 0, (int)Opcode);
            WriteInt32LE(buffer, 4, body.Length);
            Buffer.BlockCopy(body, 0, buffer, HeaderSize, body.Length);
            return buffer;
        }

        /// <summary>
        /// Parses an 8-byte header. Throws if the declared length is negative or oversized.
        /// </summary>
        public static void ParseHeader(byte[] header, out RpcOpcode opcode, out int payloadLength)
        {
            if (header == null || header.Length < HeaderSize)
                throw new ArgumentException($"Header must be at least {HeaderSize} bytes.", nameof(header));

            opcode = (RpcOpcode)ReadInt32LE(header, 0);
            payloadLength = ReadInt32LE(header, 4);

            if (payloadLength < 0 || payloadLength > MaxPayloadSize)
                throw new InvalidOperationException($"Declared payload length {payloadLength} is out of range.");
        }

        private static void WriteInt32LE(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        private static int ReadInt32LE(byte[] buffer, int offset) =>
            buffer[offset]
            | (buffer[offset + 1] << 8)
            | (buffer[offset + 2] << 16)
            | (buffer[offset + 3] << 24);
    }
}
