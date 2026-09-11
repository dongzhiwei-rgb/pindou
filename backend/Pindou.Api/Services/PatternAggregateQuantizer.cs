// 文件：PatternAggregateQuantizer.cs
// 用途：参考「拼豆图纸算法说明.md」的 pixel-aggregate（像素聚合）算法，把图片转换为自定义色板的拼豆图纸。
// 核心职责：BOX 均值网格采样、四角背景识别、按频次贪心聚合成图片色板；背景按连通传播去除；裁剪由用户自行操作。
// 版权：@董志伟-联系方式-makabak1204
// 最后修改：2026-08-25

using System.Diagnostics;
using Pindou.Api.Models;
using SkiaSharp;

namespace Pindou.Api.Services;

public sealed class PatternAggregateQuantizer
{
    // 参考文档 demo-cat 的经验值：颜色聚类曼哈顿距离阈值。
    private const int ClusterDist = 48;

    public const string AlgorithmName = "像素聚合(pixel-aggregate)";

    public QuantizeResponse Quantize(
        Stream imageStream,
        int width,
        int height,
        int maxColors,
        bool removeBackground,
        double backgroundThreshold,
        PaletteDefinition brandPalette,
        CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        // 元数据校验和像素解码共用一个 codec，降低并发生成时的托管内存峰值。
        using var source = ImageDecodeHelper.DecodeValidated(imageStream);

        cancellationToken.ThrowIfCancellationRequested();
        var grid = SampleBox(source, width, height, cancellationToken);
        var bgDist = backgroundThreshold * 6.0;    // 默认 backgroundThreshold=10 → 60，与参考文档一致
        var bg = DetectBackground(grid, width, height);
        // 从图纸四边向内传播，只有颜色接近背景色且能连到边界的格子才视为背景，避免误删主体内部同色区域。
        var backgroundMask = removeBackground ? MarkConnectedBackground(grid, width, height, bg, bgDist, cancellationToken) : null;

        var colorToIndex = new Dictionary<int, int>();
        var indices = new int[width * height];
        Array.Fill(indices, -1);

        var ordered = new List<KeyValuePair<int, int>>();
        var counter = new Dictionary<int, int>();
        for (var i = 0; i < grid.Length; i++)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var rgb = grid[i];
            if (rgb < 0) continue;   // 留白格不计入色板
            if (backgroundMask is not null && backgroundMask[i]) continue; // 背景已剔除
            counter[rgb] = counter.GetValueOrDefault(rgb) + 1;
        }
        foreach (var pair in counter.OrderByDescending(p => p.Value).ThenBy(p => p.Key))
            ordered.Add(pair);

        var palette = new List<int>();      // 聚合后的图面色板（RGB 编码）
        var paletteFrequency = new List<long>();
        var colorToPalette = new Dictionary<int, int>();
        var limit = Math.Clamp(maxColors, 2, 96);

        var orderedIndex = 0;
        foreach (var pair in ordered)
        {
            if ((orderedIndex++ & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
            var rgb = pair.Key;
            if (colorToPalette.TryGetValue(rgb, out var dup)) { colorToIndex[rgb] = dup; continue; }

            var matched = -1;
            for (var idx = 0; idx < palette.Count; idx++)
            {
                if (Manhattan(Decode(rgb), Decode(palette[idx])) < ClusterDist) { matched = idx; break; }
            }

            if (matched >= 0)
            {
                for (var reused = 0; reused < palette.Count; reused++)
                    if (Manhattan(Decode(rgb), Decode(palette[reused])) < ClusterDist) { colorToPalette[rgb] = reused; colorToIndex[rgb] = reused; break; }
            }
            else if (palette.Count < limit)
            {
                colorToPalette[rgb] = palette.Count;
                colorToIndex[rgb] = palette.Count;
                palette.Add(rgb);
                paletteFrequency.Add(pair.Value);
            }
            else
            {
                var nearest = 0;
                var nearestDistance = int.MaxValue;
                for (var idx = 0; idx < palette.Count; idx++)
                {
                    var distance = Manhattan(Decode(rgb), Decode(palette[idx]));
                    if (distance < nearestDistance) { nearestDistance = distance; nearest = idx; }
                }
                colorToPalette[rgb] = nearest;
                colorToIndex[rgb] = nearest;
            }
        }

        if (palette.Count == 0)
            throw new InvalidDataException("图片没有可生成的有效像素。");

        for (var i = 0; i < grid.Length; i++)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var rgb = grid[i];
            if (rgb < 0) continue;   // 留白格保持无豆
            if (backgroundMask is not null && backgroundMask[i]) continue;
            indices[i] = colorToIndex[rgb];
        }

        // 把聚合出的图面色板映射到所选厂商色卡中的最近色号（CIEDE2000 感知色差），
        // 使图纸上的编号/名称与用户选择的品牌色卡版本一致，而非自定义 P1/P2 编号。
        var brandColors = brandPalette.Colors;
        var brandLabs = new LabColor[brandColors.Count];
        for (var j = 0; j < brandColors.Count; j++)
        {
            var bead = brandColors[j];
            brandLabs[j] = bead.Lab.Length >= 3
                ? new LabColor(bead.Lab[0], bead.Lab[1], bead.Lab[2])
                : ColorScience.RgbToLab(bead.Rgb[0], bead.Rgb[1], bead.Rgb[2]);
        }
        var aggregateToBrand = new int[palette.Count];
        for (var i = 0; i < palette.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (r, g, b) = Decode(palette[i]);
            var lab = ColorScience.RgbToLab(r, g, b);
            var bestIndex = 0;
            var bestDistance = double.MaxValue;
            for (var j = 0; j < brandLabs.Length; j++)
            {
                var distance = ColorScience.DeltaE2000(lab, brandLabs[j]);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                bestIndex = j;
            }
            aggregateToBrand[i] = bestIndex;
        }
        // 多个聚合色可能映射到同一品牌色号，去重后得到实际用到的品牌色列表。
        var usedBrandIndexes = aggregateToBrand.Distinct().OrderBy(x => x).ToList();
        var brandToOutput = new Dictionary<int, int>();
        for (var k = 0; k < usedBrandIndexes.Count; k++) brandToOutput[usedBrandIndexes[k]] = k;
        var colors = usedBrandIndexes.Select(index => brandColors[index]).ToList();
        var outputIndices = new int[indices.Length];
        for (var i = 0; i < indices.Length; i++)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (indices[i] < 0) { outputIndices[i] = -1; continue; }
            outputIndices[i] = brandToOutput[aggregateToBrand[indices[i]]];
        }
        indices = outputIndices;

        cancellationToken.ThrowIfCancellationRequested();
        var usage = indices
            .Where(index => index >= 0)
            .GroupBy(index => index)
            .Select(group =>
            {
                var color = colors[group.Key];
                return new UsageItem(group.Key, color.Code, color.Name, color.Hex, group.Count());
            })
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Code)
            .ToList();

        timer.Stop();
        return new QuantizeResponse(
            width,
            height,
            brandPalette.BrandId,
            brandPalette.Id,
            colors,
            indices,
            usage,
            usage.Count,
            usage.Sum(item => item.Count),
            timer.ElapsedMilliseconds,
            AlgorithmName);
    }

    // contain 等比缩放居中采样：保持原图宽高比，内容区外的格子置为留白(-1)，避免图案被拉伸变形。
    private static int[] SampleBox(SKBitmap source, int width, int height, CancellationToken cancellationToken)
    {
        var output = new int[width * height];
        Array.Fill(output, -1);   // 默认整图留白
        var scale = Math.Min((double)width / source.Width, (double)height / source.Height);
        var contentW = Math.Max(1, (int)Math.Round(source.Width * scale));
        var contentH = Math.Max(1, (int)Math.Round(source.Height * scale));
        var offsetX = (width - contentW) / 2;
        var offsetY = (height - contentH) / 2;
        for (var gy = 0; gy < contentH; gy++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var y0 = gy * source.Height / contentH;
            var y1 = Math.Max(y0 + 1, (gy + 1) * source.Height / contentH);
            for (var gx = 0; gx < contentW; gx++)
            {
                var x0 = gx * source.Width / contentW;
                var x1 = Math.Max(x0 + 1, (gx + 1) * source.Width / contentW);
                long sumR = 0, sumG = 0, sumB = 0;
                var count = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var color = source.GetPixel(sx, sy);
                        sumR += color.Red;
                        sumG += color.Green;
                        sumB += color.Blue;
                        count++;
                    }
                }
                if (count == 0) continue;
                output[(gy + offsetY) * width + (gx + offsetX)] = Encode((byte)(sumR / count), (byte)(sumG / count), (byte)(sumB / count));
            }
        }
        return output;
    }

    // 取图纸四角各 3×3 区域中出现最多的颜色作为背景主色，比单格判断更抗噪点。
    private static int DetectBackground(int[] grid, int width, int height)
    {
        var counts = new Dictionary<int, int>();
        foreach (var (cx, cy) in new[] { (0, 0), (width - 1, 0), (0, height - 1), (width - 1, height - 1) })
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    var x = Math.Clamp(cx + dx, 0, width - 1);
                    var y = Math.Clamp(cy + dy, 0, height - 1);
                    var rgb = grid[y * width + x];
                    if (rgb < 0) continue;   // 留白格不参与背景识别
                    counts[rgb] = counts.GetValueOrDefault(rgb) + 1;
                }
            }
        }
        // 四角全部留白(-1)时无统计项，回退为整图第一个非留白格作为背景色。
        if (counts.Count == 0)
        {
            for (var i = 0; i < grid.Length; i++)
                if (grid[i] >= 0) return grid[i];
            return -1;
        }
        return counts.OrderByDescending(p => p.Value).ThenBy(p => p.Key).First().Key;
    }

    // 连通背景传播：从图纸四边向内 BFS，只有颜色接近背景色且能连到边界的格子才判定为背景，
    // 主体内部恰好接近背景色的区域不会被误删。
    private static bool[] MarkConnectedBackground(
        int[] grid,
        int width,
        int height,
        int bg,
        double bgDist,
        CancellationToken cancellationToken)
    {
        var isBackground = new bool[grid.Length];
        var queue = new Queue<int>();
        for (var x = 0; x < width; x++)
        {
            queue.Enqueue(x);
            queue.Enqueue((height - 1) * width + x);
        }
        for (var y = 0; y < height; y++)
        {
            queue.Enqueue(y * width);
            queue.Enqueue(y * width + width - 1);
        }
        var processed = 0;
        while (queue.Count > 0)
        {
            if ((processed++ & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var idx = queue.Dequeue();
            if (isBackground[idx]) continue;
            // 留白格(-1)直接视为背景并继续向内容区传播，避免阻断边界背景的连通去除。
            if (grid[idx] < 0) { isBackground[idx] = true; }
            else if (Manhattan(Decode(grid[idx]), Decode(bg)) >= bgDist) continue;
            else isBackground[idx] = true;
            var x = idx % width;
            var y = idx / width;
            if (x > 0) queue.Enqueue(idx - 1);
            if (x < width - 1) queue.Enqueue(idx + 1);
            if (y > 0) queue.Enqueue(idx - width);
            if (y < height - 1) queue.Enqueue(idx + width);
        }
        return isBackground;
    }

    private static int Encode(byte r, byte g, byte b) => (r << 16) | (g << 8) | b;
    private static (int, int, int) Decode(int rgb) => ((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
    private static int Manhattan((int, int, int) a, (int, int, int) b) => Math.Abs(a.Item1 - b.Item1) + Math.Abs(a.Item2 - b.Item2) + Math.Abs(a.Item3 - b.Item3);
}
