// 文件用途：OKLCH 主题转换和对比度检查；WPF 不原生支持 OKLCH，因此在边界转换为 sRGB。
using System.Windows.Markup;
using System.Windows;
using System.Windows.Media;

namespace FlashFind.Desktop.Services;

[MarkupExtensionReturnType(typeof(Duration))]
public sealed class MotionDuration : MarkupExtension
{
    public double Seconds { get; set; } = 0.16;
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Duration(TimeSpan.FromSeconds(SystemParameters.ClientAreaAnimation ? Seconds : 0));
}

[MarkupExtensionReturnType(typeof(SolidColorBrush))]
public sealed class OklchBrush : MarkupExtension
{
    public double L { get; set; }
    public double C { get; set; }
    public double H { get; set; }
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var brush = new SolidColorBrush(Palette.ToColor(L, C, H));
        brush.Freeze(); return brush;
    }
}

public static class Palette
{
    public static Color ToColor(double lightness, double chroma, double hue)
    {
        double radians = hue * Math.PI / 180, a = chroma * Math.Cos(radians), b = chroma * Math.Sin(radians);
        double l = Cube(lightness + 0.3963377774 * a + 0.2158037573 * b);
        double m = Cube(lightness - 0.1055613458 * a - 0.0638541728 * b);
        double s = Cube(lightness - 0.0894841775 * a - 1.2914855480 * b);
        return Color.FromRgb(Channel(4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s),
            Channel(-1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s),
            Channel(-0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s));
    }

    public static double Contrast(Color first, Color second)
    {
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
    private static double Cube(double value) => value * value * value;
    private static byte Channel(double value)
    {
        double gamma = value <= 0.0031308 ? 12.92 * value : 1.055 * Math.Pow(Math.Max(0, value), 1 / 2.4) - 0.055;
        return (byte)Math.Round(Math.Clamp(gamma, 0, 1) * 255);
    }
    private static double Luminance(Color color) => 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    private static double Linear(byte channel)
    {
        double value = channel / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }
}
