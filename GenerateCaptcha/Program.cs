using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

class Program
{
    static readonly Random rnd = Random.Shared;
    static string lastCode = "";

    static async Task Main(string[] args)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add("http://localhost:5000/");
        listener.Start();
        Console.WriteLine("CAPTCHA Server đang chạy tại http://localhost:5000/captcha");
        Console.WriteLine("Nhấn Ctrl+C để dừng.");

        while (true)
        {
            var ctx = await listener.GetContextAsync();
            _ = Task.Run(() =>
            {
                try
                {
                    HandleRequest(ctx);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[ERROR] {ex.Message}");
                }
            });
        }
    }

    static void HandleRequest(HttpListenerContext ctx)
    {
        try
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "";

            if (path == "/captcha")
            {
                string code = rnd.Next(100, 1000).ToString();
                Interlocked.Exchange(ref lastCode, code);

                byte[] imgBytes = GenerateCaptcha(code);
                ctx.Response.ContentType = "image/png";
                ctx.Response.ContentLength64 = imgBytes.Length;
                ctx.Response.OutputStream.Write(imgBytes, 0, imgBytes.Length);
                Console.WriteLine($"[CAPTCHA] Đã sinh: {code}");
            }
            else if (path == "/answer")
            {
                string json = $"{{\"code\":\"{Volatile.Read(ref lastCode)}\"}}";
                byte[] buf = Encoding.UTF8.GetBytes(json);
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = buf.Length;
                ctx.Response.OutputStream.Write(buf, 0, buf.Length);
            }
            else
            {
                ctx.Response.StatusCode = 404;
            }
        }
        finally
        {
            ctx.Response.OutputStream.Close();
        }
    }

    static byte[] GenerateCaptcha(string code)
    {
        int W = 160, H = 60;

        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        var canvas = surface.Canvas;

        canvas.Clear(new SKColor(
            (byte)rnd.Next(220, 246),
            (byte)rnd.Next(220, 246),
            (byte)rnd.Next(220, 246)));

        using (var dotPaint = new SKPaint { StrokeWidth = 1 })
        {
            for (int i = 0; i < 600; i++)
            {
                dotPaint.Color = new SKColor(
                    (byte)rnd.Next(100, 221),
                    (byte)rnd.Next(100, 221),
                    (byte)rnd.Next(100, 221));
                canvas.DrawPoint(rnd.Next(W), rnd.Next(H), dotPaint);
            }
        }

        using (var linePaint = new SKPaint { StrokeWidth = 2, IsAntialias = true, Style = SKPaintStyle.Stroke })
        {
            for (int i = 0; i < 5; i++)
            {
                linePaint.Color = new SKColor(
                    (byte)rnd.Next(80, 161),
                    (byte)rnd.Next(80, 161),
                    (byte)rnd.Next(80, 161));
                using var path = new SKPath();
                float x = 0, y = rnd.Next(10, H - 10);
                path.MoveTo(x, y);
                while (x < W)
                {
                    x += rnd.Next(5, 15);
                    y += rnd.Next(-8, 9);
                    y = Math.Clamp(y, 5, H - 5);
                    path.LineTo(x, y);
                }
                canvas.DrawPath(path, linePaint);
            }
        }

        using var textPaint = new SKPaint
        {
            IsAntialias = true,
            TextSize = 38,
            FakeBoldText = true,
            Style = SKPaintStyle.Fill
        };

        float posX = 8;
        foreach (char ch in code)
        {
            textPaint.Color = new SKColor(
                (byte)rnd.Next(0, 81),
                (byte)rnd.Next(0, 81),
                (byte)rnd.Next(80, 181));

            float angle = rnd.Next(-40, 41);
            float posY = rnd.Next(-8, 9);

            canvas.Save();
            canvas.RotateDegrees(angle, posX + 20, H / 2f);
            canvas.DrawText(ch.ToString(), posX, posY + 42, textPaint);
            canvas.Restore();

            posX += rnd.Next(38, 51);
        }

        using var snapshot = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(snapshot);
        using var distorted = WaveDistort(bitmap, W, H);

        using var finalImage = SKImage.FromBitmap(distorted);
        using var data = finalImage.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    static SKBitmap WaveDistort(SKBitmap src, int W, int H)
    {
        var dst = new SKBitmap(W, H);
        var pass1 = new SKBitmap(W, H);

        for (int y = 0; y < H; y++)
        {
            int offsetX = (int)(6 * Math.Sin(2 * Math.PI * y / 30.0));
            for (int x = 0; x < W; x++)
            {
                int nx = x + offsetX;
                if (nx >= 0 && nx < W)
                {
                    pass1.SetPixel(nx, y, src.GetPixel(x, y));
                }
            }
        }

        for (int x = 0; x < W; x++)
        {
            int offsetY = (int)(4 * Math.Sin(2 * Math.PI * x / 40.0));
            for (int y = 0; y < H; y++)
            {
                int ny = y + offsetY;
                if (ny >= 0 && ny < H)
                {
                    dst.SetPixel(x, ny, pass1.GetPixel(x, y));
                }
            }
        }

        pass1.Dispose();
        return dst;
    }
}
