using System.Drawing;
using System.Drawing.Drawing2D;
using QRCoder;
using Upton.Pdm.Application;

namespace Upton.Pdm.Production.PrintAgent;

public static class LabelRenderer
{
    private static string UnitName(string unit) => unit switch
    { "001" => "个", "002" => "台", "003" => "只", "004" => "盒", "005" => "卷", "006" => "捆", "007" => "双",
        "008" => "片", "009" => "桶", "010" => "支", "011" => "套", "012" => "箱", "013" => "包", _ => unit };
    public static Bitmap Render(ProductionLabelSnapshot label, int dpi = 300)
    {
        var width = (int)Math.Round(label.WidthMm * dpi / 25.4);
        var height = (int)Math.Round(label.HeightMm * dpi / 25.4);
        var image = new Bitmap(width, height); image.SetResolution(dpi, dpi);
        using var g = Graphics.FromImage(image); g.Clear(Color.White);
        g.SmoothingMode = SmoothingMode.None;
        using var pen = new Pen(Color.Black, Math.Max(1, dpi / 200f));
        float margin = dpi / 25.4f, w = width - margin * 2, h = height - margin * 2;
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
        void Text(string text, RectangleF box, float size, bool bold = false)
        {
            using var font = new Font("Microsoft YaHei", size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
            g.DrawString(text, font, Brushes.Black, box, format);
        }
        var titleH = h * .15f;
        Text(label.CompanyName, new(margin, margin, w, titleH), h * .050f, true);
        var rowH = h * .135f; var labelW = w * .21f;
        var qrSize = Math.Min(w * .34f, h * .48f);
        var qrX = margin + w - qrSize; var qrY = margin + titleH + rowH * 3;
        var values = new[] { ("项目号", label.ProjectCode), ("名称", label.Name), ("图号", label.DrawingNumber),
            ("数量", $"{label.Quantity:0.######} {UnitName(label.Unit)}"), ("日期", label.PackedAt.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy.MM.dd")) };
        for (var i = 0; i < values.Length; i++)
        {
            var y = margin + titleH + i * rowH;
            var rowWidth = i < 3 ? w : w - qrSize;
            g.DrawRectangle(pen, margin, y, labelW, rowH);
            g.DrawRectangle(pen, margin + labelW, y, rowWidth - labelW, rowH);
            Text(values[i].Item1, new(margin, y, labelW, rowH), h * .056f);
            Text(values[i].Item2, new(margin + labelW, y, rowWidth - labelW, rowH), h * .058f, i == 3);
        }
        using var generator = new QRCodeGenerator(); using var code = generator.CreateQrCode(label.Code, QRCodeGenerator.ECCLevel.M);
        using var qr = new QRCode(code); using var bitmap = qr.GetGraphic(8);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(bitmap, new RectangleF(qrX, qrY, qrSize, qrSize));
        var footerY = margin + titleH + rowH * 5;
        Text($"版本 {label.Revision}  批次 {label.PackageNumber}", new(margin, footerY, w - qrSize, h - (footerY - margin)), h * .039f);
        return image;
    }
}
