using System;

namespace Yoq.FidoHost
{
    public enum FidoError
    {
        UserPresenceRequired,
        InvalidKeyHandle,

        InterruptedIO,
        TokenBusy,
        Timeout,
        UnsupportedOperation,
        ProtocolViolation
    }

    public class FidoException : Exception
    {
        public FidoError Type { get; }
        public FidoApduResponse StatusCode { get; }

        internal FidoException(FidoApduResponse apduStatus)
        {
            StatusCode = apduStatus;
            switch (apduStatus)
            {
                case FidoApduResponse.Ok: throw new ArgumentException("APDU was OK");
                case FidoApduResponse.UserPresenceRequired: Type = FidoError.UserPresenceRequired; break;
                case FidoApduResponse.InvalidKeyHandle: Type = FidoError.InvalidKeyHandle; break;

                case FidoApduResponse.InstructionUnsupported:
                case FidoApduResponse.ClassUnsupported:
                case FidoApduResponse.InvalidParam1Or2: //used to deny signDontEnforceUserPresence (p1=0x08)
                    Type = FidoError.UnsupportedOperation;
                    break;

                case FidoApduResponse.InvalidLength: Type = FidoError.ProtocolViolation; break;
                default:
                    Type = FidoError.ProtocolViolation;
                    break;
            }
        }

        internal FidoException(FidoError type)
        {
            StatusCode = FidoApduResponse.Ok;
            Type = type;
        }

        internal FidoException(FidoError type, string message) : base(message)
        {
            StatusCode = FidoApduResponse.Ok;
            Type = type;
        }

        public override string ToString() => $"Type: [{Type}],{(StatusCode != FidoApduResponse.Ok ? $" {StatusCode}," : "")} Message: " + base.ToString();
    }
}