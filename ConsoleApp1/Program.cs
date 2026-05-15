using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using SkiaSharp;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

class Program
{
    static async Task Main(string[] args)
    {
        string captchaUrl  = args.Length > 0 ? args[0] : "http://localhost:5000/captcha";
        string outputPath  = "captcha.png";
        string processedPath = "captcha_processed.png";

        Console.WriteLine("============================================");
        Console.WriteLine(" OCR CAPTCHA Client  (Windows.Media.Ocr)");
        Console.WriteLine("============================================");
        Console.WriteLine($"[INFO]  CAPTCHA URL   : {captchaUrl}");

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        // ── Step 1: Download CAPTCHA image ──────────────────────────────
        Console.WriteLine("[INFO]  Downloading CAPTCHA image...");
        byte[] imageBytes;
        try
        {
            imageBytes = await client.GetByteArrayAsync(captchaUrl);
            await File.WriteAllBytesAsync(outputPath, imageBytes);
            Console.WriteLine($"[OK]    Image saved     : {outputPath} ({imageBytes.Length} bytes)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Failed to download: {ex.Message}");
            Console.WriteLine("        Make sure GenerateCaptcha server is running at http://localhost:5000");
            return;
        }

        // ── Step 2: Pre-process — scale 3x + grayscale + sharpen ────────
        Console.WriteLine("[INFO]  Pre-processing image...");
        PreprocessImage(outputPath, processedPath);
        Console.WriteLine($"[OK]    Processed image : {processedPath}");

        // ── Step 3: Run Windows.Media.Ocr ───────────────────────────────
        Console.WriteLine("[INFO]  Running Windows OCR engine...");
        string ocrResult = await RunWindowsOcrAsync(processedPath);
        Console.WriteLine($"[OCR]   Raw result      : \"{ocrResult}\"");

        // ── Step 4: Post-process — digits only ──────────────────────────
        string digits = new string(ocrResult.Where(char.IsDigit).ToArray());
        Console.WriteLine($"[OCR]   Digits only     : \"{digits}\"");

        // ── Step 5: Result ───────────────────────────────────────────────
        Console.WriteLine(digits.Length == 3
            ? $"[RESULT] Detected CAPTCHA : {digits} ✓"
            : $"[RESULT] Detected CAPTCHA : \"{digits}\" (expected 3 digits, got {digits.Length})");

        Console.WriteLine("============================================");
    }

    /// <summary>
    /// Pre-process ảnh bằng SkiaSharp:
    /// 1. Scale lên 4x
    /// 2. Grayscale
    /// 3. Binary threshold (tách chữ khỏi nền)
    /// 4. Invert nếu cần (chữ tối nền sáng)
    /// </summary>
    static void PreprocessImage(string inputPath, string outputPath)
    {
        using var original = SKBitmap.Decode(inputPath);

        // Scale 4x
        int newW = original.Width  * 4;
        int newH = original.Height * 4;
        using var scaled = original.Resize(new SKImageInfo(newW, newH), SKFilterQuality.High);

        // Grayscale + threshold
        using var processed = new SKBitmap(newW, newH);
        for (int y = 0; y < newH; y++)
        {
            for (int x = 0; x < newW; x++)
            {
                SKColor c    = scaled.GetPixel(x, y);
                int    gray  = (int)(c.Red * 0.299 + c.Green * 0.587 + c.Blue * 0.114);

                // Ngưỡng 150: pixel tối hơn → đen (chữ), sáng hơn → trắng (nền)
                SKColor bw = gray < 150 ? SKColors.Black : SKColors.White;
                processed.SetPixel(x, y, bw);
            }
        }

        using var image = SKImage.FromBitmap(processed);
        using var data  = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(outputPath, data.ToArray());
    }

    /// <summary>
    /// Dùng Windows.Media.Ocr — engine tích hợp Windows 10+
    /// Thông minh hơn Tesseract, không cần cài thêm gì.
    /// </summary>
    static async Task<string> RunWindowsOcrAsync(string imagePath)
    {
        try
        {
            // Load ảnh thành SoftwareBitmap
            string fullPath = Path.GetFullPath(imagePath);
            var file        = await StorageFile.GetFileFromPathAsync(fullPath);

            SoftwareBitmap softBitmap;
            using (var stream = await file.OpenAsync(FileAccessMode.Read))
            {
                var decoder = await BitmapDecoder.CreateAsync(stream);
                softBitmap  = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied);
            }

            // Dùng ngôn ngữ English
            var ocrEngine = OcrEngine.TryCreateFromLanguage(
                new Windows.Globalization.Language("en-US"));

            if (ocrEngine == null)
            {
                Console.WriteLine("[WARN]  English OCR language pack not found.");
                Console.WriteLine("        Go to: Settings > Time & Language > Language > English > Options > Download");
                return string.Empty;
            }

            var ocrResult = await ocrEngine.RecognizeAsync(softBitmap);

            // Ghép tất cả các từ nhận được
            string text = string.Join("", ocrResult.Lines
                .SelectMany(l => l.Words)
                .Select(w => w.Text));

            return text;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Windows OCR error: {ex.Message}");
            return string.Empty;
        }
    }
}
