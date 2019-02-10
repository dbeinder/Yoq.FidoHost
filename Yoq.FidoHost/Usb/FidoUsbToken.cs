using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Yoq.FidoHost.Usb
{
    public class FidoUsbToken : FidoToken
    {
        private FidoHidDevice UsbToken => (FidoHidDevice)FidoTransport;
        private FidoUsbToken(FidoHidDevice fidoHidTransport) : base(fidoHidTransport) { }

        public static async Task<FidoUsbToken> WaitForUsbToken(CancellationToken cancellationToken)
            => new FidoUsbToken(await FidoHidDevice.WaitForFidoHidDevice(cancellationToken).ConfigureAwait(false));

        public static async Task<IList<FidoUsbToken>> WaitForUsbTokens(CancellationToken cancellationToken)
            => (await FidoHidDevice.WaitForFidoHidDevices(cancellationToken).ConfigureAwait(false))
                .Select(h => new FidoUsbToken(h))
                .ToList();

        public static async Task<T> WaitForUsbTokenAnd<T>(Func<FidoUsbToken, Task<T>> then, CancellationToken cancellationToken)
        {
            for (; ; )
            {
                try
                {
                    using (var token = await WaitForUsbToken(cancellationToken))
                        return await then(token);
                }
                catch (FidoException fe) when (fe.Type == FidoError.Timeout ||
                                               fe.Type == FidoError.TokenBusy || 
                                               fe.Type == FidoError.InterruptedIO)
                { }
            }
        }

        public bool CanWink => (UsbToken.Capabilities & FidoHidCapabilities.Wink) > 0;
        public Task WinkAsync() => UsbToken.WinkAsync();

        public async Task WinkAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            if (!CanWink) throw new FidoException(FidoError.UnsupportedOperation);
            var timeout = DateTimeOffset.UtcNow + duration;
            while (timeout > DateTimeOffset.UtcNow && !cancellationToken.IsCancellationRequested)
                await UsbToken.WinkAsync();
        }

        public bool CanLock => (UsbToken.Capabilities & FidoHidCapabilities.Lock) > 0;
        public async Task<bool> LockAsync(int seconds)
        {
            if (!CanLock) throw new FidoException(FidoError.UnsupportedOperation);
            if (seconds <= 0 || seconds > 10) throw new ArgumentException("Can only lock between 1-10 seconds");
            try { await UsbToken.LockAsync((byte)seconds); }
            catch (FidoException) { return false; }
            return true;
        }
        public async Task<bool> ReleaseLockAsync()
        {
            try { await UsbToken.LockAsync(0); }
            catch (FidoException) { return false; }
            return true;
        }

        public override string ToString() => UsbToken.ToString();
    }
}
