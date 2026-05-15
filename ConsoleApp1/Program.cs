using IronOcr;
using SkiaSharp;

class Program
{
    static async Task Main(string[] args)
    {
        string captchaUrl    = args.Length > 0 ? args[0] : "http://localhost:5000/captcha";
        string outputPath    = "captcha.png";
        string processedPath = "captcha_processed.png";

        Console.WriteLine("============================================");
        Console.WriteLine(" OCR CAPTCHA Client  (IronOCR)");
        Console.WriteLine("============================================");
        Console.WriteLine($"[INFO]  CAPTCHA URL   : {captchaUrl}");

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        // ── Step 1: Download CAPTCHA image ───────────────────────────────
        Console.WriteLine("[INFO]  Downloading CAPTCHA image...");
        try
        {
            byte[] imageBytes = await client.GetByteArrayAsync(captchaUrl);
            await File.WriteAllBytesAsync(outputPath, imageBytes);
            Console.WriteLine($"[OK]    Image saved     : {outputPath} ({imageBytes.Length} bytes)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Failed to download: {ex.Message}");
            Console.WriteLine("        Make sure GenerateCaptcha server is running at http://localhost:5000");
            return;
        }

        // ── Step 2: Pre-process bằng SkiaSharp ───────────────────────────
        // Scale 4x + grayscale + threshold → chữ đen nền trắng rõ ràng
        Console.WriteLine("[INFO]  Pre-processing image...");
        PreprocessImage(outputPath, processedPath);
        Console.WriteLine($"[OK]    Processed image : {processedPath}");

        // ── Step 3: Chạy IronOCR ─────────────────────────────────────────
        Console.WriteLine("[INFO]  Running IronOCR engine...");
        string rawResult = RunIronOcr(processedPath);
        Console.WriteLine($"[OCR]   Raw result      : \"{rawResult}\"");

        // ── Step 4: Lọc chỉ lấy chữ số ──────────────────────────────────
        string digits = new string(rawResult.Where(char.IsDigit).ToArray());
        Console.WriteLine($"[OCR]   Digits only     : \"{digits}\"");

        // ── Step 5: Kết quả ──────────────────────────────────────────────
        Console.WriteLine(digits.Length == 3
            ? $"[RESULT] Detected CAPTCHA : {digits} ✓"
            : $"[RESULT] Detected CAPTCHA : \"{digits}\" (expected 3 digits, got {digits.Length})");

        Console.WriteLine("============================================");
    }

    /// <summary>
    /// Pre-process ảnh bằng SkiaSharp:
    /// 1. Scale lên 4x — IronOCR đọc chính xác hơn với ảnh lớn
    /// 2. Grayscale
    /// 3. Binary threshold — chữ đen nền trắng rõ ràng
    /// </summary>
    static void PreprocessImage(string inputPath, string outputPath)
    {
        using var original = SKBitmap.Decode(inputPath);

        int newW = original.Width  * 4;
        int newH = original.Height * 4;
        using var scaled = original.Resize(new SKImageInfo(newW, newH), SKFilterQuality.High);

        using var processed = new SKBitmap(newW, newH);
        for (int y = 0; y < newH; y++)
        {
            for (int x = 0; x < newW; x++)
            {
                SKColor c    = scaled.GetPixel(x, y);
                int    gray  = (int)(c.Red * 0.299 + c.Green * 0.587 + c.Blue * 0.114);
                // Threshold 150: tối hơn → đen (chữ), sáng hơn → trắng (nền)
                processed.SetPixel(x, y, gray < 150 ? SKColors.Black : SKColors.White);
            }
        }

        using var image = SKImage.FromBitmap(processed);
        using var data  = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(outputPath, data.ToArray());
    }

    /// <summary>
    /// Chạy IronOCR với cấu hình tối ưu cho CAPTCHA số:
    /// - Chỉ nhận ký tự 0-9
    /// - Tắt auto-rotate, auto-deskew để không làm lệch thêm
    /// - Scale = 1 (đã pre-process ở trên)
    /// </summary>
    static string RunIronOcr(string imagePath)
    {
        try
        {
            var ocr = new IronTesseract();

            // Chỉ nhận chữ số
            ocr.Configuration.WhiteListCharacters = "0123456789";

            // Tắt các bước tự động có thể làm hỏng ảnh đã xử lý
            ocr.Configuration.TesseractVersion       = TesseractVersion.Tesseract5;
            ocr.Configuration.EngineMode             = TesseractEngineMode.LstmOnly;
            ocr.Configuration.PageSegmentationMode   = TesseractPageSegmentationMode.SingleLine;
            ocr.Configuration.ReadBarCodes           = false;

            using var input = new OcrInput();
            input.LoadImage(imagePath);

            // IronOCR tự động enhance ảnh thêm một lần nữa
            input.EnhanceResolution(300);
            input.Deskew();

            var result = ocr.Read(input);

            Console.WriteLine($"[OCR]   Confidence   : {result.Confidence:F1}%");
            return result.Text.Trim();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] IronOCR error: {ex.Message}");
            return string.Empty;
        }
    }
}
