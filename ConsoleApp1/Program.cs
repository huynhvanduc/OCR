using System.Drawing;
using System.Drawing.Imaging;
using Tesseract;

class Program
{
    static async Task Main(string[] args)
    {
        string captchaUrl = args.Length > 0 ? args[0] : "http://localhost:5000/captcha";
        string outputPath = "captcha.png";
        string processedPath = "captcha_processed.png";

        Console.WriteLine("============================================");
        Console.WriteLine(" OCR CAPTCHA Client");
        Console.WriteLine("============================================");
        Console.WriteLine($"[INFO]  CAPTCHA URL  : {captchaUrl}");

        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(10);

        // ── Step 1: Download CAPTCHA image ───────────────────────────────
        Console.WriteLine("[INFO]  Downloading CAPTCHA image...");
        byte[] imageBytes;
        try
        {
            imageBytes = await client.GetByteArrayAsync(captchaUrl);
            await File.WriteAllBytesAsync(outputPath, imageBytes);
            Console.WriteLine($"[OK]    Image saved    : {outputPath} ({imageBytes.Length} bytes)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Failed to download: {ex.Message}");
            Console.WriteLine("        Make sure GenerateCaptcha server is running at http://localhost:5000");
            return;
        }

        // ── Step 2: Pre-process image (grayscale + threshold) ────────────
        Console.WriteLine("[INFO]  Pre-processing image for OCR...");
        PreprocessImage(outputPath, processedPath);
        Console.WriteLine($"[OK]    Processed image: {processedPath}");

        // ── Step 3: Run OCR ──────────────────────────────────────────────
        Console.WriteLine("[INFO]  Running Tesseract OCR...");
        string rawResult = RunOcr(processedPath);
        Console.WriteLine($"[OCR]   Raw result     : \"{rawResult}\"");

        // ── Step 4: Post-process — digits only ───────────────────────────
        string digits = new string(rawResult.Where(char.IsDigit).ToArray());
        Console.WriteLine($"[OCR]   Digits only    : \"{digits}\"");

        // ── Step 5: Result ───────────────────────────────────────────────
        if (digits.Length == 3)
            Console.WriteLine($"[RESULT] Detected CAPTCHA: {digits} ✓");
        else
            Console.WriteLine($"[RESULT] Detected CAPTCHA: \"{digits}\" (expected 3 digits, got {digits.Length})");

        Console.WriteLine("============================================");
    }

    /// <summary>
    /// Pre-process ảnh để Tesseract dễ đọc hơn:
    /// 1. Scale lên 3x (Tesseract cần ảnh đủ lớn)
    /// 2. Grayscale
    /// 3. Binary threshold (chuyển trắng/đen rõ ràng)
    /// </summary>
    static void PreprocessImage(string inputPath, string outputPath)
    {
        using var src = new Bitmap(inputPath);

        // Scale lên 3x
        int newW = src.Width  * 3;
        int newH = src.Height * 3;
        using var scaled = new Bitmap(newW, newH);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, newW, newH);
        }

        // Grayscale + Binary threshold
        using var result = new Bitmap(newW, newH);
        for (int y = 0; y < newH; y++)
        {
            for (int x = 0; x < newW; x++)
            {
                Color pixel = scaled.GetPixel(x, y);
                int gray = (int)(pixel.R * 0.299 + pixel.G * 0.587 + pixel.B * 0.114);

                // Threshold: pixel tối hơn 140 → đen (chữ), còn lại → trắng (nền)
                Color bw = gray < 140 ? Color.Black : Color.White;
                result.SetPixel(x, y, bw);
            }
        }

        result.Save(outputPath, ImageFormat.Png);
    }

    /// <summary>
    /// Chạy Tesseract OCR — chế độ chỉ nhận chữ số
    /// </summary>
    static string RunOcr(string imagePath)
    {
        try
        {
            string tessDataPath = Path.Combine(AppContext.BaseDirectory, "tessdata");
            if (!Directory.Exists(tessDataPath))
            {
                Console.WriteLine("[WARN]  tessdata folder not found!");
                Console.WriteLine($"        Create folder and place 'eng.traineddata' in: {tessDataPath}");
                Console.WriteLine("        Download: https://github.com/tesseract-ocr/tessdata/raw/main/eng.traineddata");
                return string.Empty;
            }

            using var engine = new TesseractEngine(tessDataPath, "eng", EngineMode.LstmOnly);

            // Chỉ nhận các chữ số 0-9
            engine.SetVariable("tessedit_char_whitelist", "0123456789");

            // PSM_SINGLE_LINE: coi toàn bộ ảnh là 1 dòng text
            using var img  = Pix.LoadFromFile(imagePath);
            using var page = engine.Process(img, PageSegMode.SingleLine);

            float confidence = page.GetMeanConfidence();
            string text      = page.GetText().Trim();

            Console.WriteLine($"[OCR]   Confidence   : {confidence:P0}");
            return text;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] OCR engine error: {ex.Message}");
            return string.Empty;
        }
    }
}
