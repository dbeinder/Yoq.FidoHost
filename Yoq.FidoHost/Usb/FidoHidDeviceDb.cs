using System.Collections.Generic;

namespace Yoq.FidoHost.Usb
{
    internal static class FidoHidDeviceDb
    {
        private static Dictionary<int, (string, Dictionary<int, string>)> _db =
            new Dictionary<int, (string, Dictionary<int, string>)>
            {
                {
                    0x1050, ("Yubico", new Dictionary<int, string>
                    {
                        {0x0200, "Gnubby"},
                        {0x0113, "YubiKey NEO U2F"},
                        {0x0114, "YubiKey NEO OTP+U2F"},
                        {0x0115, "YubiKey NEO U2F+CCID"},
                        {0x0116, "YubiKey NEO OTP+U2F+CCID"},
                        {0x0120, "Security Key by Yubico"},
                        {0x0410, "YubiKey Plus"},
                        {0x0402, "YubiKey 4 U2F"},
                        {0x0403, "YubiKey 4 OTP+U2F"},
                        {0x0406, "YubiKey 4 U2F+CCID"},
                        {0x0407, "YubiKey 4 OTP+U2F+CCID"}
                    })
                },
                {
                    0x2CCF, ("Hypersecu", new Dictionary<int, string>
                    {
                        {0x0880, "HyperFIDO Mini"},
                    })
                }
            };

        public static bool TryGetName(int vendorId, int productId, out string name)
        {
            name = null;
            if (!_db.TryGetValue(vendorId, out var vendor)) return false;

            name = vendor.Item1;
            if (vendor.Item2.TryGetValue(productId, out var prodName))
                name += " " + prodName;
            return true;
        }
    }
}
