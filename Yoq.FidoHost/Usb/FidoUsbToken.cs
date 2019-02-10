using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using U2F.Core.Models;

namespace Yoq.FidoHost.Usb
{
    public class FidoUsbToken : FidoToken
    {
        private FidoHidDevice UsbHidToken => (FidoHidDevice)FidoTransport;
        private FidoUsbToken(FidoHidDevice fidoHidTransport) : base(fidoHidTransport) { }

        /// <summary>
        /// Waits until at least one FIDO token is present, returns one of the available
        /// </summary>
        public static async Task<FidoUsbToken> WaitForFirstToken(CancellationToken cancellationToken)
            => new FidoUsbToken(await FidoHidDevice.WaitForFidoHidDevice(cancellationToken).ConfigureAwait(false));

        /// <summary>
        /// Waits until at least one FIDO token is present, returns a list of all available tokens
        /// </summary>
        public static async Task<IList<FidoUsbToken>> WaitForTokens(CancellationToken cancellationToken)
            => (await FidoHidDevice.WaitForFidoHidDevices(cancellationToken).ConfigureAwait(false))
                .Select(h => new FidoUsbToken(h))
                .ToList();

        /// <summary>
        /// Runs the operation on the first token of the system, retrying on Timeout, Busy, IO Errors, and if no token is present
        /// </summary>
        public static async Task<T> WaitForFirstTokenThen<T>(Func<FidoUsbToken, Task<T>> operation, CancellationToken cancellationToken)
        {
            for (; ; )
            {
                try
                {
                    using (var token = await WaitForFirstToken(cancellationToken).ConfigureAwait(false))
                        return await operation(token).ConfigureAwait(false);
                }
                catch (FidoException fe) when (fe.Type == FidoError.Timeout ||
                                               fe.Type == FidoError.TokenBusy ||
                                               fe.Type == FidoError.InterruptedIO)
                { }
            }
        }
        
        private static int ParallelDeviceRecheckInterval = 5000;

        /// <summary>
        /// Try authenticate on all present tokens in parallel, return the first valid response
        /// </summary>
        /// <param name="ignoreInvalidKeyHandle">If false, throws on encountering token/authStart mismatch</param>
        /// <returns></returns>
        public static Task<AuthenticateResponse> AuthenticateParallel(StartedAuthentication authStart,
            CancellationToken cancellationToken, bool ignoreInvalidKeyHandle = true, string facet = null)
            => RunParallel((tk, ct) => tk.AuthenticateAsync(authStart, ct, true, facet), cancellationToken, ignoreInvalidKeyHandle);

        /// <summary>
        /// Try register on all present tokens in parallel, return the first valid response
        /// </summary>
        public static Task<RegisterResponse> RegisterParallel(StartedRegistration regStart,
            CancellationToken cancellationToken, string facet = null)
            => RunParallel((tk, ct) => tk.RegisterAsync(regStart, facet, ct), cancellationToken, false);

        private static async Task<T> RunParallel<T>(Func<FidoUsbToken, CancellationToken, Task<T>> fn,
            CancellationToken cancellationToken, bool ignoreInvalidKeyHandle)
            where T : class
        {
            for (; ; cancellationToken.ThrowIfCancellationRequested())
            {
                var tokens = await WaitForTokens(cancellationToken).ConfigureAwait(false);

                var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(ParallelDeviceRecheckInterval));
                var merged = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

                var tasks = tokens.Select(async tk =>
                {
                    try { return await fn(tk, merged.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return null; }
                    catch (FidoException fe) when (fe.Type == FidoError.Timeout ||
                                                   fe.Type == FidoError.TokenBusy ||
                                                   fe.Type == FidoError.InterruptedIO)
                    { return null; }
                    catch (FidoException fe) when (fe.Type == FidoError.InvalidKeyHandle)
                    { if (ignoreInvalidKeyHandle) return null; throw; }
                }).ToList();

                while (tasks.Count > 0)
                {
                    var first = await Task.WhenAny(tasks).ConfigureAwait(false);
                    var result = first.Status == TaskStatus.RanToCompletion ? first.Result : null;
                    if (result != null) return result;
                    tasks.Remove(first);
                }
            }
        }

        public bool CanWink => (UsbHidToken.Capabilities & FidoHidCapabilities.Wink) > 0;
        public Task WinkAsync() => UsbHidToken.WinkAsync();

        public async Task WinkAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            if (!CanWink) throw new FidoException(FidoError.UnsupportedOperation);
            var timeout = DateTimeOffset.UtcNow + duration;
            while (timeout > DateTimeOffset.UtcNow && !cancellationToken.IsCancellationRequested)
                await UsbHidToken.WinkAsync();
        }

        public bool CanLock => (UsbHidToken.Capabilities & FidoHidCapabilities.Lock) > 0;
        public async Task<bool> LockAsync(int seconds)
        {
            if (!CanLock) throw new FidoException(FidoError.UnsupportedOperation);
            if (seconds <= 0 || seconds > 10) throw new ArgumentException("Can only lock between 1-10 seconds");
            try { await UsbHidToken.LockAsync((byte)seconds); }
            catch (FidoException) { return false; }
            return true;
        }
        public async Task<bool> ReleaseLockAsync()
        {
            try { await UsbHidToken.LockAsync(0); }
            catch (FidoException) { return false; }
            return true;
        }
        
        public override string ToString() => UsbHidToken.ToString();
    }
}
