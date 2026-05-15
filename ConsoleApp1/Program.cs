using System.Net;
using System.Text.Json;

class Program
{
    static async Task Main(string[] args)
    {
        // URL of the CAPTCHA server (can be passed as argument)
        string captchaUrl  = args.Length > 0 ? args[0] : "http://localhost:5000/captcha";
        string answerUrl   = captchaUrl.Replace("/captcha", "/answer");
        string outputPath  = "captcha.png";

        Console.WriteLine("============================================");
        Console.WriteLine(" OCR CAPTCHA Client");
        Console.WriteLine("============================================");
        Console.WriteLine($"[INFO]  CAPTCHA URL : {captchaUrl}");

        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(10);

        // ── Step 1: Download CAPTCHA image ────────────────────────────
        Console.WriteLine("[INFO]  Downloading CAPTCHA image...");
        byte[] imageBytes;
        try
        {
            imageBytes = await client.GetByteArrayAsync(captchaUrl);
            await File.WriteAllBytesAsync(outputPath, imageBytes);
            Console.WriteLine($"[OK]    Image saved  : {outputPath} ({imageBytes.Length} bytes)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Failed to download image: {ex.Message}");
            Console.WriteLine("        Make sure GenerateCaptcha server is running at http://localhost:5000");
            return;
        }

        // ── Step 2: Fetch ground-truth answer (for accuracy check) ────
        string? groundTruth = null;
        try
        {
            var json = await client.GetStringAsync(answerUrl);
            groundTruth = JsonDocument.Parse(json).RootElement.GetProperty("code").GetString();
            Console.WriteLine($"[INFO]  Ground truth : {groundTruth}");
        }
        catch
        {
            Console.WriteLine("[WARN]  Could not fetch ground truth answer (optional).");
        }

        // ── Step 3: Run OCR ───────────────────────────────────────────
        Console.WriteLine("[INFO]  Running OCR...");
        string ocrResult = RunOcr(outputPath);
        Console.WriteLine($"[OCR]   Raw result   : \"{ocrResult}\"");

        // ── Step 4: Post-process — keep digits only ───────────────────
        string digits = new string(ocrResult.Where(char.IsDigit).ToArray());
        Console.WriteLine($"[OCR]   Digits only  : \"{digits}\"");

        // ── Step 5: Accuracy check ────────────────────────────────────
        if (groundTruth != null)
        {
            bool correct = digits == groundTruth;
            Console.WriteLine($"[RESULT] Match : {(correct ? "YES ✓" : "NO ✗")}  (OCR: \"{digits}\" | Truth: \"{groundTruth}\")");
        }

        Console.WriteLine("============================================");
    }

    static string RunOcr(string imagePath)
    {
        // Tesseract OCR — digits only mode
        try
        {
            string tessDataPath = Path.Combine(AppContext.BaseDirectory, "tessdata");
            if (!Directory.Exists(tessDataPath))
            {
                Console.WriteLine("[WARN]  tessdata folder not found. Returning filename as placeholder.");
                Console.WriteLine("        Download tessdata from: https://github.com/tesseract-ocr/tessdata");
                Console.WriteLine($"        Place 'eng.traineddata' in: {tessDataPath}");
                return "[tessdata missing]";
            }

            using var engine = new Tesseract.TesseractEngine(tessDataPath, "eng", Tesseract.EngineMode.Default);
            engine.SetVariable("tessedit_char_whitelist", "0123456789"); // digits only

            using var img = Tesseract.Pix.LoadFromFile(imagePath);
            using var page = engine.Process(img);
            return page.GetText().Trim();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] OCR engine error: {ex.Message}");
            return string.Empty;
        }
    }
}
