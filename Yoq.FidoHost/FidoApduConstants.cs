namespace Yoq.FidoHost
{
    public enum FidoApduResponse : ushort
    {
        Ok = 0x9000,
        UserPresenceRequired = 0x6985,
        InvalidKeyHandle = 0x6A80,
        InvalidParam1Or2 = 0x6A86, //currently used to deny signDontEnforceUserPresence
        InvalidLength = 0x6700,
        ClassUnsupported = 0x6E00,
        InstructionUnsupported = 0x6D00
    }

    public enum FidoInstruction : byte
    {
        Register = 0x01,
        Authenticate = 0x02,
        GetVersion = 0x03
    }

    public enum FidoParam1 : byte
    {
        None = 0x00,

        OnlyCheckKeyHandle = 0x07,
        SignEnforceUserPresence = 0x03,
        SignDontEnforceUserPresence = 0x08
    }

    public enum FidoParam2 : byte
    {
        None = 0x00
    }
}