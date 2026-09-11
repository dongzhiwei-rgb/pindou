// 文件：ColorScience.cs
// 用途：提供 sRGB 到 CIE Lab 的转换及 CIEDE2000 感知色差计算。
// 核心职责：为实体拼豆色号匹配、相近色合并和边缘保护清理提供统一色彩度量。
// 版权：@董志伟-联系方式-makabak1204
// 最后修改：2026-08-21

namespace Pindou.Api.Services;

public readonly record struct LabColor(double L, double A, double B);

public static class ColorScience
{
    /// <summary>
    /// 将 0–255 的 sRGB 分量转换为 D65 白点下的 CIE Lab。
    /// Lab 把亮度与色度分离，适合后续使用感知色差比较不同材质豆子的屏幕近似色。
    /// </summary>
    public static LabColor RgbToLab(double red, double green, double blue)
    {
        var r = PivotRgb(red / 255.0);
        var g = PivotRgb(green / 255.0);
        var b = PivotRgb(blue / 255.0);

        var x = (r * 0.4124564 + g * 0.3575761 + b * 0.1804375) / 0.95047;
        var y = (r * 0.2126729 + g * 0.7151522 + b * 0.0721750) / 1.00000;
        var z = (r * 0.0193339 + g * 0.1191920 + b * 0.9503041) / 1.08883;

        var fx = PivotXyz(x);
        var fy = PivotXyz(y);
        var fz = PivotXyz(z);
        return new LabColor(116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    /// <summary>
    /// 计算 CIEDE2000 色差，补偿 Lab 空间在不同亮度、彩度和色相区域中的感知不均匀。
    /// 返回值越小表示两种颜色在人眼观察下越接近。
    /// </summary>
    public static double DeltaE2000(LabColor first, LabColor second)
    {
        const double degrees = 180.0 / Math.PI;
        const double radians = Math.PI / 180.0;
        var c1 = Math.Sqrt(first.A * first.A + first.B * first.B);
        var c2 = Math.Sqrt(second.A * second.A + second.B * second.B);
        var cBar = (c1 + c2) / 2.0;
        var cBar7 = Math.Pow(cBar, 7);
        var g = 0.5 * (1 - Math.Sqrt(cBar7 / (cBar7 + Math.Pow(25.0, 7))));
        var a1Prime = (1 + g) * first.A;
        var a2Prime = (1 + g) * second.A;
        var c1Prime = Math.Sqrt(a1Prime * a1Prime + first.B * first.B);
        var c2Prime = Math.Sqrt(a2Prime * a2Prime + second.B * second.B);
        var h1Prime = Hue(a1Prime, first.B, degrees);
        var h2Prime = Hue(a2Prime, second.B, degrees);

        var deltaLPrime = second.L - first.L;
        var deltaCPrime = c2Prime - c1Prime;
        var deltaHPrime = DeltaHue(c1Prime, c2Prime, h1Prime, h2Prime);
        var deltaBigHPrime = 2 * Math.Sqrt(c1Prime * c2Prime) * Math.Sin(deltaHPrime * radians / 2);

        var lBarPrime = (first.L + second.L) / 2.0;
        var cBarPrime = (c1Prime + c2Prime) / 2.0;
        var hBarPrime = MeanHue(c1Prime, c2Prime, h1Prime, h2Prime);
        var t = 1
                - 0.17 * Math.Cos((hBarPrime - 30) * radians)
                + 0.24 * Math.Cos(2 * hBarPrime * radians)
                + 0.32 * Math.Cos((3 * hBarPrime + 6) * radians)
                - 0.20 * Math.Cos((4 * hBarPrime - 63) * radians);
        var deltaTheta = 30 * Math.Exp(-Math.Pow((hBarPrime - 275) / 25, 2));
        var cBarPrime7 = Math.Pow(cBarPrime, 7);
        var rc = 2 * Math.Sqrt(cBarPrime7 / (cBarPrime7 + Math.Pow(25.0, 7)));
        var sl = 1 + 0.015 * Math.Pow(lBarPrime - 50, 2) / Math.Sqrt(20 + Math.Pow(lBarPrime - 50, 2));
        var sc = 1 + 0.045 * cBarPrime;
        var sh = 1 + 0.015 * cBarPrime * t;
        var rt = -Math.Sin(2 * deltaTheta * radians) * rc;

        var lTerm = deltaLPrime / sl;
        var cTerm = deltaCPrime / sc;
        var hTerm = deltaBigHPrime / sh;
        return Math.Sqrt(lTerm * lTerm + cTerm * cTerm + hTerm * hTerm + rt * cTerm * hTerm);
    }

    private static double PivotRgb(double value) =>
        value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);

    private static double PivotXyz(double value) =>
        value > 0.008856 ? Math.Pow(value, 1.0 / 3.0) : 7.787 * value + 16.0 / 116.0;

    private static double Hue(double a, double b, double degrees)
    {
        var hue = Math.Atan2(b, a) * degrees;
        return hue >= 0 ? hue : hue + 360;
    }

    private static double DeltaHue(double c1, double c2, double h1, double h2)
    {
        if (c1 * c2 == 0) return 0;
        var delta = h2 - h1;
        if (Math.Abs(delta) <= 180) return delta;
        return delta > 180 ? delta - 360 : delta + 360;
    }

    private static double MeanHue(double c1, double c2, double h1, double h2)
    {
        if (c1 * c2 == 0) return h1 + h2;
        if (Math.Abs(h1 - h2) <= 180) return (h1 + h2) / 2;
        return h1 + h2 < 360 ? (h1 + h2 + 360) / 2 : (h1 + h2 - 360) / 2;
    }
}
