using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HidLibrary;
using U2F.Core.Models;
using Yoq.FidoHost;
using Yoq.FidoHost.Usb;
using CryptoLib = U2F.Core.Crypto;

namespace Yoq.FidoHost.Test
{
    class Program
    {
        private static CancellationTokenSource cts;
        static void Main(string[] args)
        {
            cts = new CancellationTokenSource();
            var task = Task.Run(async () => await Run(cts.Token));
            Console.WriteLine("Press ESC to cancel");
            Console.CancelKeyPress += Console_CancelKeyPress;
            while (!(task.IsCanceled || task.IsCompleted || task.IsFaulted)) Thread.Sleep(50);
            Console.WriteLine("Run task finished, press any key to exit>");
            Console.ReadKey();
        }

        private static void Console_CancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            Console.WriteLine("Cancellation token triggered!");
            cts.Cancel();
        }

        static async Task Run(CancellationToken cancel)
        {
            var appId = "foo";
            
            Console.WriteLine("Multi Token Mode, (parallel)");
            try
            {
                var regStartX = CryptoLib.U2F.StartRegistration(appId);
                Console.Write("Registration... ");
                var regRespX = await FidoUsbToken.RegisterParallel(regStartX, cancel);
                var deviceRegX = CryptoLib.U2F.FinishRegistration(regStartX, regRespX);
                Console.WriteLine($"Done [{deviceRegX.GetAttestationCertificate().Subject}]");

                var authStartX = CryptoLib.U2F.StartAuthentication(appId, deviceRegX);
                Console.Write("Authentication... ");
                var authRespX = await FidoUsbToken.AuthenticateParallel(authStartX, cancel);
                var counter = CryptoLib.U2F.FinishAuthentication(authStartX, authRespX, deviceRegX);
                Console.WriteLine($"Done, Counter: {counter}");
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                Console.ReadKey();
            }
            
            Console.WriteLine("Multi Token Mode test done\n");

            try
            {
                Console.Write("Waiting for tokens... ");
                var tokens = await FidoUsbToken.WaitForTokens(cancel);

                Console.WriteLine($"Found {tokens.Count} tokens");

                for (var n = 0; n < tokens.Count; n++) Console.WriteLine($"{n + 1}) {tokens[n]}");
                Console.Write(">");
                var input = Console.ReadLine();
                var idx = string.IsNullOrEmpty(input) ? 0 : int.TryParse(input, out var x) ? x - 1 : 0;
                var usb = tokens[idx];

                var version = await usb.GetProtocolVersionAsync();
                Console.WriteLine($"Token implements FIDO protocol: {version}");

                if (usb.CanWink)
                {
                    Console.Write("Testing winking... ");
                    await usb.WinkAsync(TimeSpan.FromSeconds(1), cancel);
                    Console.WriteLine("Done");
                }

                var regStart = CryptoLib.U2F.StartRegistration(appId);
                Console.Write("Registration... ");
                var regResp = await usb.RegisterAsync(regStart, cancel);
                var deviceReg = CryptoLib.U2F.FinishRegistration(regStart, regResp);
                Console.WriteLine($"Done [{deviceReg.GetAttestationCertificate().Subject}]");

                var authStart = CryptoLib.U2F.StartAuthentication(appId, deviceReg);
                Console.Write("Authentication... ");
                var authResp = await usb.AuthenticateAsync(authStart, cancel);
                var counter = CryptoLib.U2F.FinishAuthentication(authStart, authResp, deviceReg);
                Console.WriteLine($"Done, Counter: {counter}");


                var khStart = CryptoLib.U2F.StartAuthentication(appId, deviceReg);
                Console.Write("Checking good Keyhandle... ");
                var khResp = await usb.CheckKeyHandleAsync(authStart, cancel);
                Console.WriteLine($"Done, valid handle: {khResp}");

                Console.Write("Checking bad Keyhandle... ");
                var borkedAuthStart = new StartedAuthentication(authStart.Challenge, authStart.AppId, "X" + authStart.KeyHandle.Substring(1));
                khResp = await usb.CheckKeyHandleAsync(borkedAuthStart, cancel);
                Console.WriteLine($"Done, valid handle: {khResp}");

                var xAuthStart = CryptoLib.U2F.StartAuthentication(appId, deviceReg);
                Console.Write("Authentication without user presence... ");
                try
                {
                    var xAuthResp = await usb.AuthenticateAsync(xAuthStart, cancel, false);
                    Console.WriteLine($"Done");
                    var xCounter = CryptoLib.U2F.FinishAuthentication(xAuthStart, xAuthResp, deviceReg);
                    Console.WriteLine($"Counter: {xCounter}");
                }
                catch (FidoException fe) when (fe.Type == FidoError.UnsupportedOperation) { Console.WriteLine($"Unsupported by HW"); }


                if (false && usb.CanLock)
                {
                    Console.Write("Testing locking: (10sec)... ");
                    var lockSucc = await usb.LockAsync(10);
                    Console.WriteLine($"Success: {lockSucc}");

                    await Task.Delay(TimeSpan.FromSeconds(11));
                    Console.Write("Lock expired, locking again for 10sec... ");
                    lockSucc = await usb.LockAsync(10);
                    Console.WriteLine($"Success: {lockSucc}");
                    await Task.Delay(TimeSpan.FromSeconds(1));

                    Console.Write("Testing ReleaseLock... ");
                    var relSucc = await usb.ReleaseLockAsync();
                    Console.WriteLine($"Success: {relSucc}");

                    Console.Write("Testing ReleaseLock... ");
                    relSucc = await usb.ReleaseLockAsync();
                    Console.WriteLine($"Success: {relSucc}");
                }

                Console.Write("Remove&reinsert token> [press enter]");
                Console.ReadKey();
                Console.WriteLine();

                tokens = await FidoUsbToken.WaitForTokens(cancel);
                usb = tokens[idx];

                Console.Write("Checking good Keyhandle... ");
                khResp = await usb.CheckKeyHandleAsync(authStart, cancel);
                Console.WriteLine($"Done, valid handle: {khResp}");

                Console.Write("Authentication... ");
                authResp = await usb.AuthenticateAsync(authStart, cancel);
                counter = CryptoLib.U2F.FinishAuthentication(authStart, authResp, deviceReg);
                Console.WriteLine($"Done, Counter: {counter}");

                Console.Write("Checking good Keyhandle... ");
                khResp = await usb.CheckKeyHandleAsync(authStart, cancel);
                Console.WriteLine($"Done, valid handle: {khResp}");

                while (Console.KeyAvailable) Console.ReadKey(true);
                Console.WriteLine("\nTesting polling for token mode, token can be removed at will. Press any key to exit");
                while (!Console.KeyAvailable)
                {
                    var regStart1 = CryptoLib.U2F.StartRegistration(appId);
                    Console.Write("Registration ... ");
                    var regResp1 = await FidoUsbToken.WaitForFirstTokenThen(async tk => await tk.RegisterAsync(regStart1, cancel), cancel);
                    var deviceReg1 = CryptoLib.U2F.FinishRegistration(regStart1, regResp1);
                    Console.WriteLine($"Done [{deviceReg1.GetAttestationCertificate().Subject}]");


                    var authStart1 = CryptoLib.U2F.StartAuthentication(appId, deviceReg1);
                    Console.Write("Authentication... ");
                    var authResp1 = await FidoUsbToken.WaitForFirstTokenThen(async tk => await tk.AuthenticateAsync(authStart1, cancel), cancel);
                    var counter1 = CryptoLib.U2F.FinishAuthentication(authStart1, authResp1, deviceReg1);
                    Console.WriteLine($"Done, Counter: {counter1}");
                }
            }
            catch (Exception e) { Console.WriteLine(e); }
            Console.WriteLine("Test finished");
        }
    }
}
