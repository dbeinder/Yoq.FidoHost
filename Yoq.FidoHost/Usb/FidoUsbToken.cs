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
        /// <param name="invalidKeyHandleCnt">Reports the number of plugged in tokens, which report that authStart is not valid for them</param>
        /// <returns></returns>
        public static Task<AuthenticateResponse> AuthenticateParallel(StartedAuthentication authStart,
            CancellationToken cancellationToken, IProgress<int> invalidKeyHandleCnt = null, string facet = null)
            => RunParallel((tk, ct) => tk.AuthenticateAsync(authStart, ct, true, facet), cancellationToken, invalidKeyHandleCnt);

        /// <summary>
        /// Try register on all present tokens in parallel, return the first valid response
        /// </summary>
        public static Task<RegisterResponse> RegisterParallel(StartedRegistration regStart,
            CancellationToken cancellationToken, string facet = null)
            => RunParallel((tk, ct) => tk.RegisterAsync(regStart, facet, ct), cancellationToken);

        private static async Task<T> RunParallel<T>(Func<FidoUsbToken, CancellationToken, Task<T>> fn,
            CancellationToken cancellationToken, IProgress<int> invalidCntReporter = null)
            where T : class
        {
            var lastInvalidCount = 0;
            void UpdateInvalidCount(int count)
            {
                if (count == lastInvalidCount || invalidCntReporter == null) return;
                invalidCntReporter.Report(count);
                lastInvalidCount = count;
            }

            for (; ; cancellationToken.ThrowIfCancellationRequested())
            {
                var tokens = await WaitForTokens(cancellationToken).ConfigureAwait(false);

                var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(ParallelDeviceRecheckInterval));
                var merged = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

                var tasks = tokens.Select(async tk =>
                {
                    try { return (0, await fn(tk, merged.Token).ConfigureAwait(false)); }
                    catch (OperationCanceledException) { return (0, null); }
                    catch (FidoException fe) when (fe.Type == FidoError.Timeout ||
                                                   fe.Type == FidoError.TokenBusy ||
                                                   fe.Type == FidoError.InterruptedIO)
                    { return (0, null); }
                    catch (FidoException fe) when (fe.Type == FidoError.InvalidKeyHandle)
                    { return (1, null); }
                })
                .Concat(new[] { Task.Delay(500, merged.Token).ContinueWith(_ => (-1, (T)null), merged.Token) })
                .ToList();

                var invalidKeyHandleCnt = 0;
                while (tasks.Count > 0)
                {
                    var first = await Task.WhenAny(tasks).ConfigureAwait(false);
                    var result = first.Status == TaskStatus.RanToCompletion ? first.Result : (0, null);
                    if (result.Item2 != null) return result.Item2;
                    tasks.Remove(first);

                    if (result.Item1 == -1) UpdateInvalidCount(invalidKeyHandleCnt);
                    else invalidKeyHandleCnt += result.Item1;
                }
                UpdateInvalidCount(invalidKeyHandleCnt);
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
