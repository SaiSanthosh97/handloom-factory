using Android.Runtime;
using Xamarin.Google.MLKit.Vision.BarCode;
using Xamarin.Google.MLKit.Vision.Barcode.Common;
using Xamarin.Google.MLKit.Vision.Common;
using AndroidApp = Android.App.Application;
using MlBarcode = Xamarin.Google.MLKit.Vision.Barcode.Common.Barcode;

namespace HandloomFactory.Device.Vision;

/// <summary>
/// Reads QR codes from a photo with Google ML Kit, on the phone (nothing is uploaded). Used by a worker's phone to
/// read the owner's link QR code. Adapted from Travel Vault's ML Kit reader.
/// </summary>
public static class QrReader
{
    /// <returns>The text of every QR code found in the photo (usually one).</returns>
    public static async Task<IReadOnlyList<string>> ReadAsync(byte[] photo, CancellationToken cancellationToken = default)
    {
        // Written to a private temporary file so ML Kit reads the photo's rotation; deleted straight away.
        var path = Path.Combine(AndroidApp.Context.CacheDir!.AbsolutePath, "qr-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(path, photo, cancellationToken);
        try
        {
            using var input = InputImage.FromFilePath(AndroidApp.Context, Android.Net.Uri.FromFile(new Java.IO.File(path))!);
            using var scanner = BarcodeScanning.Client;
            try
            {
                var result = await scanner.Process(input).AsTask().WaitAsync(cancellationToken);
                if (result?.JavaCast<JavaList>() is not { } barcodes)
                {
                    return [];
                }

                return barcodes
                    .Cast<Java.Lang.Object>()
                    .Select(o => o.JavaCast<MlBarcode>())
                    .Where(b => b?.Format == MlBarcode.FormatQrCode && !string.IsNullOrEmpty(b.RawValue))
                    .Select(b => b!.RawValue!)
                    .ToList();
            }
            finally
            {
                scanner.Close();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
