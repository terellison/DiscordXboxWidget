namespace Discord.Rpc.Protocol
{
    /// <summary>
    /// Opcodes for the Discord IPC framing layer. Sent as a little-endian int32
    /// in the first 4 bytes of every frame header.
    /// </summary>
    public enum RpcOpcode
    {
        Handshake = 0,
        Frame = 1,
        Close = 2,
        Ping = 3,
        Pong = 4,
    }
}
