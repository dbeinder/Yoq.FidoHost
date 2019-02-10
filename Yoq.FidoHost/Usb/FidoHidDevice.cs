using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HidLibrary;

namespace Yoq.FidoHost.Usb
{
    //HID Message structure: [ChannelId: 4B][FidoHidMsgType: 1B][data length: 2B][data]
    //Longer messages are split across multiple packets:
    //  FidoHidMsgType of the first message contains the command type and the command flag is set.
    //  In subsequent messages, FidoHidMsgType contains the sequence number (truncated to one 7bits),
    //      the command flag is always zero, and [data length] is omitted.

    internal enum FidoHidMsgType : byte
    {
        CommandFlag = 0x80,
        SequenceIdMask = 0xFF & ~CommandFlag,

        Ping = 0x01 | CommandFlag,
        ApduMessage = 0x03 | CommandFlag,
        Lock = 0x04 | CommandFlag,
        Init = 0x06 | CommandFlag,
        Wink = 0x08 | CommandFlag,
        Error = 0x3F | CommandFlag
    }

    internal enum FidoHidError : byte
    {
        InvalidCmd = 0x01,
        InvalidParameter = 0x02,
        InvalidMessageLength = 0x03,
        InvalidSequenceValue = 0x04,
        MessageTimeout = 0x05,
        ChannelBusy = 0x06
    }

    [Flags]
    public enum FidoHidCapabilities : byte
    {
        Wink = 0x01,
        Lock = 0x02
    }

    internal class FidoHidDevice : IFidoTransport, IDisposable
    {
        private const int HidReportSize = 64;
        private const int HidTimeoutMs = 1000;
        private static readonly byte[] BroadcastChannelId = new byte[4] { 0xFF, 0xFF, 0xFF, 0xFF };

        private readonly Random _nonCryptoRng = new Random();
        private readonly HidDevice _hidDevice;
        private byte[] _channelId;

        protected FidoHidDevice(HidDevice hidDevice)
        {
            _hidDevice = hidDevice;
            _channelId = BroadcastChannelId;
        }

        protected static bool IsFidoHidDevice(IHidDevice device)
            => (ushort)device.Capabilities.UsagePage == 0xf1d0 &&
               (ushort)device.Capabilities.Usage == 0x1;

        protected static IEnumerable<HidDevice> GetFidoHidDevices() => HidDevices.Enumerate().Where(IsFidoHidDevice);

        public static async Task<FidoHidDevice> WaitForFidoHidDevice(CancellationToken cancelToken)
        {
            for (; ; )
            {
                var hidToken = GetFidoHidDevices().FirstOrDefault();
                if (hidToken != null)
                {
                    var fidoHid = await OpenAsync(hidToken).ConfigureAwait(false);
                    if (fidoHid != null) return fidoHid;
                }
                try { await Task.Delay(200, cancelToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return null; }
            }
        }

        public static async Task<IReadOnlyList<FidoHidDevice>> WaitForFidoHidDevices(CancellationToken cancelToken)
        {
            for (; ; )
            {
                var hidTokens = GetFidoHidDevices().ToList();
                if (hidTokens.Count > 0)
                {
                    var fidoTokens = new List<FidoHidDevice>();
                    foreach (var token in hidTokens)
                    {
                        var fidoHid = await OpenAsync(token).ConfigureAwait(false);
                        if (fidoHid != null) fidoTokens.Add(fidoHid);
                    }
                    if (fidoTokens.Count > 0) return fidoTokens;
                }
                try { await Task.Delay(200, cancelToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return null; }
            }
        }

        public static async Task<FidoHidDevice> OpenAsync(HidDevice hidDevice)
        {
            var device = new FidoHidDevice(hidDevice);
            try { await device.InitAsync().ConfigureAwait(false); }
            catch (FidoException fe) when (fe.Type == FidoError.TokenBusy || 
                                           fe.Type == FidoError.InterruptedIO || 
                                           fe.Type == FidoError.Timeout) 
            { return null; }
            return device;
        }

        private async Task InitAsync()
        {
            var nonce = new byte[8];
            _nonCryptoRng.NextBytes(nonce);
            var response = await CallAsync(FidoHidMsgType.Init, nonce).ConfigureAwait(false);

            while (!response.Take(8).SequenceEqual(nonce))
            {
                await Task.Delay(100).ConfigureAwait(false);
                //Log.Debug("Wrong nonce, read again...");
                response = await CallAsync(FidoHidMsgType.Init, nonce).ConfigureAwait(false);
            }

            _channelId = response.Skip(8).Take(4).ToArray();
            U2FHidProtocolVersion = response[12];
            HwVersion = $"{response[13]}.{response[14]}.{response[15]}";
            Capabilities = (FidoHidCapabilities)response[16];
            Name = FidoHidDeviceDb.TryGetName(_hidDevice.Attributes.VendorId, _hidDevice.Attributes.ProductId, out var n) ? n : null;
        }
        
        public int U2FHidProtocolVersion { get; private set; }
        public string HwVersion { get; private set; }
        public string Name { get; private set; }
        public FidoHidCapabilities Capabilities { get; private set; }
        public override string ToString() => (Name != null ? Name + " " : "Unknown Token ")
                                             + $" ::  HW v{HwVersion}, U2FHID: v{U2FHidProtocolVersion}, Capabilities: {Capabilities}";

        public Task WinkAsync() => CallAsync(FidoHidMsgType.Wink);

        public Task<byte[]> PingAsync(byte[] data) => CallAsync(FidoHidMsgType.Ping, data);

        public Task LockAsync(byte seconds) => CallAsync(FidoHidMsgType.Lock, new[] { seconds });

        public async Task<byte[]> ApduMessageAsync(FidoInstruction instruction, FidoParam1 param1 = FidoParam1.None, FidoParam2 param2 = FidoParam2.None, byte[] data = null)
        {
            if (data == null) data = new byte[0];
            var dataLen = data.Length;
            var adpu = new ByteArrayBuilder();
            adpu.Append(new byte[]
            {
                0x00, //CLA byte
                (byte)instruction,
                (byte)param1,
                (byte)param2,
                //some tokens only work with extended length
                (byte)(dataLen >> 16 & 0xff),
                (byte)(dataLen >> 8 & 0xff),
                (byte)(dataLen & 0xff)
            });
            adpu.Append(data);

            //max response size: 65536 byte
            //needed for hyperfido
            adpu.Append(new byte[] { 0x00, 0x00 });

            var apduData = adpu.GetBytes();

            var response = await CallAsync(FidoHidMsgType.ApduMessage, apduData).ConfigureAwait(false);

            var responseData = response.Take(response.Length - 2).ToArray();
            var status = response.Skip(response.Length - 2).Take(2).Reverse().ToArray();

            var statusCode = (FidoApduResponse)BitConverter.ToUInt16(status, 0);

            if (statusCode != FidoApduResponse.Ok) throw new FidoException(statusCode);

            return responseData;
        }

        protected async Task<byte[]> CallAsync(FidoHidMsgType command, byte[] data = null)
        {
            await SendRequestAsync(command, data).ConfigureAwait(false);
            return await ReadResponseAsync(command).ConfigureAwait(false);
        }

        private async Task SendRequestAsync(FidoHidMsgType msgType, byte[] data = null)
        {
            if (data == null) data = new byte[0];

            var size = data.Length;
            var payloadData = data.Take(HidReportSize - 7).ToArray();

            var payloadBuilder = new ByteArrayBuilder();
            payloadBuilder.Append(_channelId);
            payloadBuilder.Append((byte)msgType);
            payloadBuilder.Append((byte)(size >> 8 & 0xff));
            payloadBuilder.Append((byte)(size & 0xff));
            payloadBuilder.Append(payloadData);
            payloadBuilder.AppendZerosTill(HidReportSize);


            var report = _hidDevice.CreateReport();
            report.Data = payloadBuilder.GetBytes();
            if (!await _hidDevice.WriteReportAsync(report, HidTimeoutMs).ConfigureAwait(false))
                throw new FidoException(FidoError.InterruptedIO, "Error writing to token");

            var remainingData = data.Skip(HidReportSize - 7).ToArray();
            var seq = 0;
            while (remainingData.Length > 0)
            {
                payloadData = remainingData.Take(HidReportSize - 5).ToArray();

                payloadBuilder.Clear();
                payloadBuilder.Append(_channelId);
                payloadBuilder.Append((byte)(0x7f & seq));
                payloadBuilder.Append(payloadData);
                payloadBuilder.AppendZerosTill(HidReportSize);

                report = _hidDevice.CreateReport();
                report.Data = payloadBuilder.GetBytes();
                if (!await _hidDevice.WriteReportAsync(report, HidTimeoutMs).ConfigureAwait(false))
                    throw new FidoException(FidoError.InterruptedIO, "Error writing to token");

                remainingData = remainingData.Skip(HidReportSize - 5).ToArray();
                seq++;
            }
        }

        //returns: doRetry?
        private bool CheckHeader(byte[] response, out FidoHidMsgType msgType)
        {
            msgType = FidoHidMsgType.Error;
            if (response.Length < 5) return false;
            //ignore messages for other channels
            if (!response.Take(4).SequenceEqual(_channelId)) return false;
            msgType = (FidoHidMsgType)response[4];
            if (msgType != FidoHidMsgType.Error) return true;

            if (response.Length < 8) throw new FidoException(FidoError.ProtocolViolation, "error message too short");
            var errorCode = (FidoHidError)response[7];
            switch (errorCode)
            {
                case FidoHidError.MessageTimeout: throw new FidoException(FidoError.Timeout);
                case FidoHidError.ChannelBusy: throw new FidoException(FidoError.TokenBusy);
                default: throw new FidoException(FidoError.ProtocolViolation, $"U2FHID Error: [{errorCode}]");
            }
        }

        private async Task<byte[]> ReadResponseAsync(FidoHidMsgType msgType)
        {
            HidReport report;
            FidoHidMsgType recvdMsgType;

            do
            {
                report = await _hidDevice.ReadReportAsync(HidTimeoutMs).ConfigureAwait(false);

                if (report.ReadStatus != HidDeviceData.ReadStatus.Success)
                    throw new FidoException(FidoError.InterruptedIO, $"Error reading from token: {report.ReadStatus}");

            } while (!CheckHeader(report.Data, out recvdMsgType));

            if (msgType != recvdMsgType)
                throw new FidoException(FidoError.ProtocolViolation, $"received {recvdMsgType} instead of {msgType}");

            var dataLength = (report.Data[5] << 8) + report.Data[6];
            var payloadData = report.Data.Skip(7).Take(Math.Min(dataLength, HidReportSize)).ToArray();

            var payload = new ByteArrayBuilder();
            payload.Append(payloadData);
            dataLength -= (int)payload.Length;

            var seq = 0;
            while (dataLength > 0)
            {
                do
                {
                    report = await _hidDevice.ReadReportAsync(HidTimeoutMs).ConfigureAwait(false);

                    if (report.ReadStatus != HidDeviceData.ReadStatus.Success)
                        throw new FidoException(FidoError.InterruptedIO, $"Error reading from token: {report.ReadStatus}");

                } while (!CheckHeader(report.Data, out recvdMsgType));

                if ((recvdMsgType & FidoHidMsgType.CommandFlag) > 0)
                    throw new FidoException(FidoError.ProtocolViolation, "fragmented message continuation had command flag set");

                if ((byte)recvdMsgType != (seq & (byte)FidoHidMsgType.SequenceIdMask))
                    throw new FidoException(FidoError.ProtocolViolation, $"received out-of-sequence message: Recvd:0x{(byte)recvdMsgType:X}, seq-nr:{seq}");

                seq++;
                payloadData = report.Data.Skip(5).Take(Math.Min(dataLength, HidReportSize)).ToArray();

                dataLength -= payloadData.Length;
                payload.Append(payloadData);
            }

            return payload.GetBytes();
        }

        private bool _disposed = false;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _hidDevice.CloseDevice();
        }
    }
}
