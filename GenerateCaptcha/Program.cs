using System.Net;
using SkiaSharp;

class Program
{
    static readonly Random rnd = new Random();
    static string lastCode = "";

    static async Task Main(string[] args)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add("http://localhost:5000/");
        listener.Start();
        Console.WriteLine("=================================================");
        Console.WriteLine(" CAPTCHA Server đang chạy:");
        Console.WriteLine("   http://localhost:5000/captcha  -> ảnh PNG");
        Console.WriteLine("   http://localhost:5000/answer   -> code thật");
        Console.WriteLine(" Nhấn Ctrl+C để dừng.");
        Console.WriteLine("=================================================");

        while (true)
        {
            var ctx = await listener.GetContextAsync();
            _ = Task.Run(() => HandleRequest(ctx));
        }
    }

    static void HandleRequest(HttpListenerContext ctx)
    {
        string path = ctx.Request.Url?.AbsolutePath ?? "";

        try
        {
            if (path == "/captcha")
            {
                string code = rnd.Next(100, 999).ToString();
                lastCode = code;

                byte[] imgBytes = GenerateCaptcha(code);
                ctx.Response.ContentType = "image/png";
                ctx.Response.ContentLength64 = imgBytes.Length;
                ctx.Response.OutputStream.Write(imgBytes, 0, imgBytes.Length);
                Console.WriteLine($"[CAPTCHA] Đã sinh: {code}");
            }
            else if (path == "/answer")
            {
                string json = $"{{\"code\":\"{lastCode}\"}}";
                byte[] buf = System.Text.Encoding.UTF8.GetBytes(json);
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = buf.Length;
                ctx.Response.OutputStream.Write(buf, 0, buf.Length);
                Console.WriteLine($"[ANSWER]  Trả về: {lastCode}");
            }
            else
            {
                string help = "Dùng: /captcha để lấy ảnh | /answer để lấy code";
                byte[] buf = System.Text.Encoding.UTF8.GetBytes(help);
                ctx.Response.StatusCode = 404;
                ctx.Response.ContentType = "text/plain";
                ctx.Response.ContentLength64 = buf.Length;
                ctx.Response.OutputStream.Write(buf, 0, buf.Length);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] {ex.Message}");
        }
        finally
        {
            ctx.Response.OutputStream.Close();
        }
    }

    static byte[] GenerateCaptcha(string code)
    {
        int W = 180, H = 100;

        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        var canvas = surface.Canvas;

        // Nền trắng sáng
        canvas.Clear(new SKColor(245, 245, 250));

        // Noise dots nhạt
        using (var dotPaint = new SKPaint { StrokeWidth = 1 })
        {
            for (int i = 0; i < 250; i++)
            {
                dotPaint.Color = new SKColor(
                    (byte)rnd.Next(170, 215),
                    (byte)rnd.Next(170, 215),
                    (byte)rnd.Next(170, 215));
                canvas.DrawPoint(rnd.Next(W), rnd.Next(H), dotPaint);
            }
        }

        // Đường nhiễu cong đi ngang qua chữ
        using (var linePaint = new SKPaint
        {
            StrokeWidth = 1,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        })
        {
            for (int i = 0; i < 3; i++)
            {
                linePaint.Color = new SKColor(
                    (byte)rnd.Next(150, 195),
                    (byte)rnd.Next(150, 195),
                    (byte)rnd.Next(150, 195));

                var path = new SKPath();
                float x = 0, y = rnd.Next(20, H - 20);
                path.MoveTo(x, y);
                while (x < W)
                {
                    x += rnd.Next(8, 20);
                    y += rnd.Next(-8, 9);
                    y = Math.Clamp(y, 5, H - 5);
                    path.LineTo(x, y);
                }
                canvas.DrawPath(path, linePaint);
            }
        }

        // Vẽ từng chữ số
        // - nghiêng mạnh: -40° ~ +40° (không bao giờ thẳng)
        // - khoảng cách sát/chồng nhau: 28~38px (font 50-62px nên chồng lên nhau)
        // - Y lên xuống mạnh: ±22px
        // - font size random mỗi chữ
        float posX = 15;
        foreach (char ch in code)
        {
            float fontSize = rnd.Next(48, 63);  // size random mỗi chữ

            using var textPaint = new SKPaint
            {
                IsAntialias  = true,
                TextSize     = fontSize,
                FakeBoldText = true,
                Style        = SKPaintStyle.Fill,
                Color        = new SKColor(
                    (byte)rnd.Next(0,  50),
                    (byte)rnd.Next(0,  50),
                    (byte)rnd.Next(90, 175))
            };

            // Luôn nghiêng, không thẳng: -40~-15 hoặc +15~+40
            int angle = rnd.Next(0, 2) == 0
                ? rnd.Next(-40, -14)
                : rnd.Next(15, 41);

            // Y lên xuống mạnh — không thẳng hàng
            float baseY = 68f + rnd.Next(-22, 23);

            canvas.Save();
            canvas.RotateDegrees(angle, posX + fontSize / 2f, H / 2f);
            canvas.DrawText(ch.ToString(), posX, baseY, textPaint);
            canvas.Restore();

            // Bước nhảy nhỏ hơn font → chữ sát/chồng nhau
            posX += rnd.Next(28, 39);
        }

        // Wave distortion vừa phải
        using var snapshot = surface.Snapshot();
        using var bitmap   = SKBitmap.FromImage(snapshot);
        var distorted      = WaveDistort(bitmap, W, H);

        using var finalImage = SKImage.FromBitmap(distorted);
        using var data       = finalImage.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    static SKBitmap WaveDistort(SKBitmap src, int W, int H)
    {
        var pass1 = new SKBitmap(W, H);
        var dst   = new SKBitmap(W, H);

        // Sóng ngang
        for (int y = 0; y < H; y++)
        {
            int offsetX = (int)(4 * Math.Sin(2 * Math.PI * y / 35.0));
            for (int x = 0; x < W; x++)
            {
                int nx = x + offsetX;
                if (nx >= 0 && nx < W)
                    pass1.SetPixel(nx, y, src.GetPixel(x, y));
            }
        }

        // Sóng dọc
        for (int x = 0; x < W; x++)
        {
            int offsetY = (int)(3 * Math.Sin(2 * Math.PI * x / 45.0));
            for (int y = 0; y < H; y++)
            {
                int ny = y + offsetY;
                if (ny >= 0 && ny < H)
                    dst.SetPixel(x, ny, pass1.GetPixel(x, y));
            }
        }

        return dst;
    }
}
