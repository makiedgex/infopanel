using System.Collections.Generic;
using System.Linq;

namespace InfoPanel.CorsairPanel
{
    public static class CorsairPanelModelDatabase
    {
        public const int CORSAIR_VENDOR_ID = 0x1B1C;
        public const int CORSAIR_PRODUCT_ID_NAUTILUS_RS_LCD = 0x0C57;

        public static readonly (int Vid, int Pid)[] SupportedDevices =
        [
            (CORSAIR_VENDOR_ID, CORSAIR_PRODUCT_ID_NAUTILUS_RS_LCD),
        ];

        public static readonly Dictionary<CorsairPanelModel, CorsairPanelModelInfo> Models = new()
        {
            [CorsairPanelModel.NautilusRsLcd] = new CorsairPanelModelInfo
            {
                Model = CorsairPanelModel.NautilusRsLcd,
                Name = "Corsair Nautilus RS LCD",
                Width = 480,
                Height = 480,
                VendorId = CORSAIR_VENDOR_ID,
                ProductId = CORSAIR_PRODUCT_ID_NAUTILUS_RS_LCD,
            },
        };

        public static CorsairPanelModelInfo? GetModelByVidPid(int vid, int pid)
        {
            return Models.Values.FirstOrDefault(m => m.VendorId == vid && m.ProductId == pid);
        }
    }
}
