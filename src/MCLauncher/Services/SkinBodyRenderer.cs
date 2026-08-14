using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MCLauncher.Services;

/// <summary>
/// Рендерит фронтальный вид персонажа Minecraft из текстурного листа скина (как у Minotar).
/// Поддерживает классические 64x64 и HD 128x128 листы, классическую и slim-модели.
/// </summary>
public static class SkinBodyRenderer
{
    public static BitmapSource? Render(byte[] png, bool slim = false)
    {
        try
        {
            var sheet = new BitmapImage();
            sheet.BeginInit();
            sheet.CacheOption = BitmapCacheOption.OnLoad;
            sheet.StreamSource = new MemoryStream(png);
            sheet.EndInit();
            sheet.Freeze();

            // Координаты текстур считаются от 64x64; для HD-листов (128x128) всё удваивается
            var s = sheet.PixelWidth / 64.0;
            if (sheet.PixelWidth != 64 && sheet.PixelWidth != 128) return null;
            if (sheet.PixelHeight != 64 && sheet.PixelHeight != 128 && sheet.PixelHeight != 32) return null;

            var armW = slim ? 3.0 : 4.0;   // ширина руки (в ед. текстуры)
            const double step = 6;          // пикселей результата на 1 ед. текстуры
            var width = 16 * step;          // 96
            var height = 32 * step;         // 192

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                void Part(double sx, double sy, double sw, double sh, double dx, double dy)
                {
                    var src = new Int32Rect((int)(sx * s), (int)(sy * s), (int)(sw * s), (int)(sh * s));
                    if (src.Width <= 0 || src.Height <= 0) return;
                    var crop = new CroppedBitmap(sheet, src);
                    crop.Freeze();
                    dc.DrawImage(crop, new Rect(dx * step, dy * step, sw * step, sh * step));
                }

                // Голова (передняя грань + слой шляпы)
                Part(8, 8, 8, 8, 4, 0);
                Part(40, 8, 8, 8, 4, 0);
                // Тело
                Part(20, 20, 8, 12, 4, 8);
                // Руки: правая слева, левая справа
                Part(44, 20, armW, 12, 0, 8);
                Part(36, 52, armW, 12, 16 - armW, 8);
                // Ноги: правая слева, левая справа
                Part(4, 20, 4, 12, 4, 20);
                Part(20, 52, 4, 12, 8, 20);
            }

            var rtb = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }
        catch
        {
            return null;
        }
    }
}