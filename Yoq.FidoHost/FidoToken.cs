using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using U2F.Core.Models;
using U2F.Core.Utils;

namespace Yoq.FidoHost
{
    public interface IFidoTransport : IDisposable
    {
        Task<byte[]> ApduMessageAsync(FidoInstruction instruction,
            FidoParam1 param1 = FidoParam1.None,
            FidoParam2 param2 = FidoParam2.None, byte[] data = null);
    }

    public class FidoToken : IDisposable
    {
        protected readonly IFidoTransport FidoTransport;

        public FidoToken(IFidoTransport fidoTransport) => FidoTransport = fidoTransport;

        protected static async Task<T> RetryTillUserPresence<T>(Func<Task<T>> func, CancellationToken cancellationToken)
        {
            for (; ; cancellationToken.ThrowIfCancellationRequested())
            {
                try
                { return await func().ConfigureAwait(false); }
                catch (FidoException ex) when (ex.StatusCode == FidoApduResponse.UserPresenceRequired)
                { await Task.Delay(100, cancellationToken); }
                catch (FidoException fe) when (fe.StatusCode == FidoApduResponse.InvalidParam1Or2)
                { throw new FidoException(FidoError.UnsupportedOperation, "token does not support sign without user presence"); }
            }
        }

        public async Task<string> GetProtocolVersionAsync()
        {
            try
            {
                var versionBytes = await FidoTransport.ApduMessageAsync(FidoInstruction.GetVersion).ConfigureAwait(false);
                return Encoding.ASCII.GetString(versionBytes);
            }
            catch (FidoException ex)
            {
                if (ex.StatusCode == FidoApduResponse.InstructionUnsupported) return "v0";
                throw;
            }
        }

        public Task<RegisterResponse> RegisterAsync(StartedRegistration request, CancellationToken cancellationToken)
            => RegisterAsync(request, null, cancellationToken);

        public async Task<RegisterResponse> RegisterAsync(StartedRegistration request, string facet, CancellationToken cancellationToken)
        {
            CheckVersionOrThrow(request.Version);

            var sha256 = new SHA256Managed();
            var appParam = sha256.ComputeHash(Encoding.ASCII.GetBytes(request.AppId));

            var clientData = GetRegistrationClientData(request.Challenge, facet);
            var challengeParam = sha256.ComputeHash(Encoding.ASCII.GetBytes(clientData));
            var data = challengeParam.Concat(appParam).ToArray();

            var response = await RetryTillUserPresence(
                async () => await FidoTransport.ApduMessageAsync(FidoInstruction.Register, FidoParam1.None, FidoParam2.None, data).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);

            var registrationDataBase64 = response.ByteArrayToBase64String();
            var clientDataBase64 = Encoding.ASCII.GetBytes(clientData).ByteArrayToBase64String();

            return new RegisterResponse(registrationDataBase64, clientDataBase64);
        }

        private (string clientData, byte[] message) BuildAuthMessage(StartedAuthentication request, string facet)
        {
            CheckVersionOrThrow(request.Version);

            var sha256 = new SHA256Managed();
            var appParam = sha256.ComputeHash(Encoding.ASCII.GetBytes(request.AppId));

            var clientDataString = GetAuthenticationClientData(request.Challenge, facet);
            var clientParam = sha256.ComputeHash(Encoding.ASCII.GetBytes(clientDataString));

            var keyHandleDecoded = request.KeyHandle.Base64StringToByteArray();

            var byteArrayBuilder = new ByteArrayBuilder();
            byteArrayBuilder.Append(clientParam);
            byteArrayBuilder.Append(appParam);
            byteArrayBuilder.Append((byte)keyHandleDecoded.Length);
            byteArrayBuilder.Append(keyHandleDecoded);
            return (clientDataString, byteArrayBuilder.GetBytes());
        }

        public async Task<bool> CheckKeyHandleAsync(StartedAuthentication request, CancellationToken cancellationToken, string facet = null)
        {
            var (_, message) = BuildAuthMessage(request, facet);
            try { await FidoTransport.ApduMessageAsync(FidoInstruction.Authenticate, FidoParam1.OnlyCheckKeyHandle, FidoParam2.None, message); }
            catch (FidoException fe) when (fe.StatusCode == FidoApduResponse.UserPresenceRequired) { return true; }
            catch (FidoException fe) when (fe.StatusCode == FidoApduResponse.InvalidKeyHandle) { return false; }
            throw new FidoException(FidoError.ProtocolViolation, "token should always answer with an error");
        }

        public async Task<AuthenticateResponse> AuthenticateAsync(StartedAuthentication request, CancellationToken cancellationToken, bool enforceUserPresence = true, string facet = null)
        {
            var (clientDataString, message) = BuildAuthMessage(request, facet);
            var eup = enforceUserPresence ? FidoParam1.SignEnforceUserPresence : FidoParam1.SignDontEnforceUserPresence;

            var response = await RetryTillUserPresence(
                async () => await FidoTransport.ApduMessageAsync(FidoInstruction.Authenticate, eup, FidoParam2.None, message).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);

            var responseBase64 = response.ByteArrayToBase64String();
            var clientDataBase64 = Encoding.ASCII.GetBytes(clientDataString).ByteArrayToBase64String();

            return new AuthenticateResponse(clientDataBase64, responseBase64, request.KeyHandle);
        }

        private static string GetRegistrationClientData(string challenge, string facet)
            => JsonConvert.SerializeObject(new
            {
                typ = "navigator.id.finishEnrollment",
                challenge = challenge,
                origin = facet
            });

        private static string GetAuthenticationClientData(string challenge, string facet)
            => JsonConvert.SerializeObject(new
            {
                typ = "navigator.id.getAssertion",
                challenge = challenge,
                origin = facet
            });

        private static void CheckVersionOrThrow(string version)
        {
            if (version != U2F.Core.Crypto.U2F.U2FVersion) 
                throw new ArgumentException($"Request has unsupported U2F version: {version}, expected: {U2F.Core.Crypto.U2F.U2FVersion}");
        }

        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            FidoTransport.Dispose();
        }
    }
}
