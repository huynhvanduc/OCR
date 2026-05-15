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
        int W = 200, H = 90;

        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        var canvas = surface.Canvas;

        // Nền trắng sáng
        canvas.Clear(new SKColor(245, 245, 250));

        // Noise dots nhạt
        using (var dotPaint = new SKPaint { StrokeWidth = 1 })
        {
            for (int i = 0; i < 200; i++)
            {
                dotPaint.Color = new SKColor(
                    (byte)rnd.Next(180, 220),
                    (byte)rnd.Next(180, 220),
                    (byte)rnd.Next(180, 220));
                canvas.DrawPoint(rnd.Next(W), rnd.Next(H), dotPaint);
            }
        }

        // Đường nhiễu nhạt, mỏng
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
                    (byte)rnd.Next(160, 200),
                    (byte)rnd.Next(160, 200),
                    (byte)rnd.Next(160, 200));

                var path = new SKPath();
                float x = 0, y = rnd.Next(10, H - 10);
                path.MoveTo(x, y);
                while (x < W)
                {
                    x += rnd.Next(10, 25);
                    y += rnd.Next(-6, 7);
                    y = Math.Clamp(y, 5, H - 5);
                    path.LineTo(x, y);
                }
                canvas.DrawPath(path, linePaint);
            }
        }

        // Vẽ từng chữ số
        using var textPaint = new SKPaint
        {
            IsAntialias = true,
            TextSize = 56,
            FakeBoldText = true,
            Style = SKPaintStyle.Fill
        };

        float posX = 10;
        foreach (char ch in code)
        {
            // Màu tối đậm, tương phản cao
            textPaint.Color = new SKColor(
                (byte)rnd.Next(0, 60),
                (byte)rnd.Next(0, 60),
                (byte)rnd.Next(100, 180));

            float angle = rnd.Next(-12, 13);   // nghiêng nhẹ -12° ~ +12°
            float posY  = rnd.Next(-5, 6);      // lên xuống ít

            canvas.Save();
            canvas.RotateDegrees(angle, posX + 20, H / 2f);
            canvas.DrawText(ch.ToString(), posX, posY + 65, textPaint);
            canvas.Restore();

            posX += rnd.Next(44, 54);           // sát nhau hơn (55~70 -> 44~54)
        }

        // Wave distortion nhẹ
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

        // Sóng ngang nhẹ
        for (int y = 0; y < H; y++)
        {
            int offsetX = (int)(3 * Math.Sin(2 * Math.PI * y / 40.0));
            for (int x = 0; x < W; x++)
            {
                int nx = x + offsetX;
                if (nx >= 0 && nx < W)
                    pass1.SetPixel(nx, y, src.GetPixel(x, y));
            }
        }

        // Sóng dọc nhẹ
        for (int x = 0; x < W; x++)
        {
            int offsetY = (int)(2 * Math.Sin(2 * Math.PI * x / 50.0));
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
