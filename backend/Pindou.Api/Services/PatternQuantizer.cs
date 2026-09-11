// 文件：PatternQuantizer.cs
// 用途：把上传图片转换为指定厂商色卡和板子尺寸可制作的拼豆图纸。
// 核心职责：网格采样、背景识别、感知色差匹配、误差扩散、候选色优化及边缘保护去杂色。
// 版权：@董志伟-联系方式-makabak1204
// 最后修改：2026-08-25

using System.Diagnostics;
using Pindou.Api.Models;
using SkiaSharp;

namespace Pindou.Api.Services;

public sealed class PatternQuantizer
{
    /// <summary>
    /// 按指定网格与实体色卡生成图纸。处理顺序固定为采样、背景标记、候选色选择、
    /// 感知色差映射和空间去杂色，保证各强度档位可以重复得到一致结果。
    /// </summary>
    /// <remarks>
    /// 宽高上限由 API 层限制为 160；内部数组最多 25,600 项，避免大图请求造成不可控内存占用。
    /// </remarks>
    public QuantizeResponse Quantize(
        Stream imageStream,
        PaletteDefinition palette,
        int width,
        int height,
        int maxColors,
        bool dither,
        bool removeBackground,
        double backgroundThreshold,
        int noiseSuppression,
        CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        // 元数据校验和像素解码共用一个 codec，避免上传内容经历 MemoryStream、ToArray、SKData 三次复制。
        using var source = ImageDecodeHelper.DecodeValidated(imageStream);

        cancellationToken.ThrowIfCancellationRequested();
        var samples = SampleToGrid(source, width, height, cancellationToken);
        var transparent = samples.Select(p => p.Alpha < 40).ToArray();
        if (removeBackground)
            RemoveConnectedBackground(samples, transparent, width, height, backgroundThreshold, cancellationToken);

        var paletteLabs = palette.Colors
            .Select(c => c.Lab.Length >= 3
                ? new LabColor(c.Lab[0], c.Lab[1], c.Lab[2])
                : ColorScience.RgbToLab(c.Rgb[0], c.Rgb[1], c.Rgb[2]))
            .ToArray();

        var nearestMatches = new int[width * height];
        Array.Fill(nearestMatches, -1);
        void MatchSample(int index)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!transparent[index]) nearestMatches[index] = FindNearest(samples[index], paletteLabs, null);
        }

        // 首次全色卡匹配占量化计算的大部分时间。单请求最多使用一半处理器（上限四个线程），
        // 与服务端量化并发门配合，给心跳、存档和 SSE 留出调度余量，避免计算请求拖死整站。
        if (samples.Length >= 4096 && Environment.ProcessorCount > 1)
        {
            Parallel.For(
                0,
                samples.Length,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(Math.Max(1, Environment.ProcessorCount / 2), 4),
                    CancellationToken = cancellationToken
                },
                MatchSample);
        }
        else
        {
            for (var i = 0; i < samples.Length; i++) MatchSample(i);
        }

        var counts = new Dictionary<int, int>();
        for (var i = 0; i < nearestMatches.Length; i++)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var match = nearestMatches[i];
            if (match < 0) continue;
            counts[match] = counts.GetValueOrDefault(match) + 1;
        }

        var suppressionLevel = Math.Clamp(noiseSuppression, 0, 3);
        var candidates = SelectCandidates(counts, paletteLabs, maxColors, suppressionLevel);
        if (candidates.Length == 0)
            throw new InvalidDataException("图片没有可生成的有效像素。");

        var cells = dither
            ? Dither(samples, transparent, width, height, palette.Colors, paletteLabs, candidates, cancellationToken)
            : Remap(samples, transparent, paletteLabs, candidates, cancellationToken);
        if (suppressionLevel > 0)
            cells = SuppressNoise(cells, samples, transparent, width, height, paletteLabs, suppressionLevel, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        var usage = cells
            .Where(index => index >= 0)
            .GroupBy(index => index)
            .Select(group =>
            {
                var color = palette.Colors[group.Key];
                return new UsageItem(group.Key, color.Code, color.Name, color.Hex, group.Count());
            })
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Code)
            .ToList();

        timer.Stop();
        return new QuantizeResponse(
            width,
            height,
            palette.BrandId,
            palette.Id,
            palette.Colors,
            cells,
            usage,
            usage.Count,
            usage.Sum(item => item.Count),
            timer.ElapsedMilliseconds,
            BuildAlgorithmName(dither, suppressionLevel));
    }

    /// <summary>
    /// 从整张厂家色卡中挑选本图实际需要的候选色。使用量负责保留主体色，
    /// 与已选颜色的最小 CIEDE2000 色差负责提升颜色覆盖，并阻止近似色重复占用名额。
    /// </summary>
    private static int[] SelectCandidates(
        IReadOnlyDictionary<int, int> counts,
        IReadOnlyList<LabColor> paletteLabs,
        int maxColors,
        int suppressionLevel)
    {
        var ranked = counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key)
            .ToList();
        if (ranked.Count == 0) return [];

        var limit = Math.Clamp(maxColors, 2, paletteLabs.Count);
        var minimumSeparation = suppressionLevel switch
        {
            1 => 2.2,
            2 => 3.5,
            3 => 5.0,
            _ => 0.0
        };
        var selected = new List<int>(limit) { ranked[0].Key };
        var remaining = ranked.Skip(1).ToList();

        while (selected.Count < limit && remaining.Count > 0)
        {
            var bestPosition = -1;
            var bestScore = double.MinValue;
            for (var i = 0; i < remaining.Count; i++)
            {
                var candidate = remaining[i];
                var nearestSelected = selected
                    .Select(index => ColorScience.DeltaE2000(paletteLabs[candidate.Key], paletteLabs[index]))
                    .Min();
                if (nearestSelected < minimumSeparation) continue;

                // 使用量保证主体色优先，色差奖励让少量但关键的强调色有机会进入候选集。
                var coverageWeight = 1.0 + Math.Min(nearestSelected, 20.0) / 7.0;
                var score = candidate.Value * coverageWeight;
                if (score <= bestScore) continue;
                bestScore = score;
                bestPosition = i;
            }

            if (bestPosition < 0) break;
            selected.Add(remaining[bestPosition].Key);
            remaining.RemoveAt(bestPosition);
        }

        // 单色或相近色图片允许少用颜色；但存在多个原始颜色时至少保留两个候选。
        if (selected.Count == 1 && ranked.Count > 1) selected.Add(ranked[1].Key);
        return selected.ToArray();
    }

    /// <summary>
    /// 清理空间上孤立的色点。只有邻域存在明确主色，且替换后的原图色差增量在当前档位允许范围内时才替换，
    /// 因此平坦区域会被整理，而高反差轮廓、眼睛和小装饰等有效细节会尽量保留。
    /// </summary>
    private static int[] SuppressNoise(
        int[] sourceCells,
        IReadOnlyList<SKColor> samples,
        IReadOnlyList<bool> transparent,
        int width,
        int height,
        IReadOnlyList<LabColor> paletteLabs,
        int level,
        CancellationToken cancellationToken)
    {
        var cells = sourceCells;
        var passes = level;
        var allowedDistanceIncrease = level switch
        {
            1 => 2.0,
            2 => 4.0,
            _ => 6.5
        };
        var maximumSameNeighbors = level switch
        {
            1 => 0,
            2 => 1,
            _ => 2
        };
        var requiredDominantNeighbors = level switch
        {
            1 => 5,
            2 => 4,
            _ => 4
        };
        var sampleLabs = samples
            .Select(color => ColorScience.RgbToLab(color.Red, color.Green, color.Blue))
            .ToArray();
        Span<int> neighborColors = stackalloc int[8];
        Span<int> neighborColorCounts = stackalloc int[8];

        for (var pass = 0; pass < passes; pass++)
        {
            var next = cells.ToArray();
            var changed = 0;
            for (var y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var x = 0; x < width; x++)
                {
                    var index = y * width + x;
                    var current = cells[index];
                    if (current < 0 || transparent[index]) continue;

                    var uniqueNeighborColors = 0;
                    for (var offsetY = -1; offsetY <= 1; offsetY++)
                    {
                        for (var offsetX = -1; offsetX <= 1; offsetX++)
                        {
                            if (offsetX == 0 && offsetY == 0) continue;
                            var neighborX = x + offsetX;
                            var neighborY = y + offsetY;
                            if (neighborX < 0 || neighborX >= width || neighborY < 0 || neighborY >= height) continue;
                            var neighbor = cells[neighborY * width + neighborX];
                            if (neighbor < 0) continue;

                            var colorSlot = -1;
                            for (var slot = 0; slot < uniqueNeighborColors; slot++)
                            {
                                if (neighborColors[slot] != neighbor) continue;
                                colorSlot = slot;
                                break;
                            }

                            if (colorSlot >= 0)
                            {
                                neighborColorCounts[colorSlot]++;
                            }
                            else
                            {
                                neighborColors[uniqueNeighborColors] = neighbor;
                                neighborColorCounts[uniqueNeighborColors] = 1;
                                uniqueNeighborColors++;
                            }
                        }
                    }

                    if (uniqueNeighborColors == 0) continue;
                    var sameNeighbors = 0;
                    var dominantColor = -1;
                    var dominantCount = 0;
                    for (var slot = 0; slot < uniqueNeighborColors; slot++)
                    {
                        var neighborColor = neighborColors[slot];
                        var neighborCount = neighborColorCounts[slot];
                        if (neighborColor == current) sameNeighbors = neighborCount;

                        if (neighborCount > dominantCount ||
                            (neighborCount == dominantCount && (dominantColor < 0 || neighborColor < dominantColor)))
                        {
                            dominantColor = neighborColor;
                            dominantCount = neighborCount;
                        }
                    }

                    if (sameNeighbors > maximumSameNeighbors) continue;
                    // 主色评选包含当前颜色：数量相同的时候按色卡索引取最小值，与旧版 Dictionary
                    // 排序规则完全一致，避免性能改造改变边界像素的最终结果。
                    if (dominantColor < 0 || dominantColor == current || dominantCount < requiredDominantNeighbors) continue;

                    var currentDistance = ColorScience.DeltaE2000(sampleLabs[index], paletteLabs[current]);
                    var replacementDistance = ColorScience.DeltaE2000(sampleLabs[index], paletteLabs[dominantColor]);
                    if (replacementDistance - currentDistance > allowedDistanceIncrease) continue;

                    next[index] = dominantColor;
                    changed++;
                }
            }

            cells = next;
            if (changed == 0) break;
        }

        return cells;
    }

    private static string BuildAlgorithmName(bool dither, int suppressionLevel)
    {
        var baseName = dither ? "CIEDE2000 + Floyd–Steinberg" : "CIEDE2000";
        return suppressionLevel == 0 ? baseName : $"{baseName} + 杂色抑制{suppressionLevel}";
    }

    /// <summary>
    /// 按完整图片边界以 Cover 方式铺满图纸，再在每个目标格内进行 4×4 Alpha 加权采样。
    /// 裁剪工具输出与图纸比例一致，因此其中的透明留白会原样保留并转换为无豆格；多点平均还能抑制照片噪点和摩尔纹。
    /// </summary>
    private static SKColor[] SampleToGrid(SKBitmap source, int width, int height, CancellationToken cancellationToken)
    {
        var output = new SKColor[width * height];
        var contentBounds = new SourceBounds(0, 0, source.Width, source.Height);

        // 取两个方向所需缩放倍数中的较大值，保证宽、高都不会留下空边。
        // 用目标尺寸反推采样窗口，可以直接从原图采样，避免先生成一张可能很大的中间位图。
        var coverScale = Math.Max(width / contentBounds.Width, height / contentBounds.Height);
        var cropWidth = width / coverScale;
        var cropHeight = height / coverScale;
        var cropX = contentBounds.X + (contentBounds.Width - cropWidth) / 2;
        var cropY = contentBounds.Y + (contentBounds.Height - cropHeight) / 2;

        // 目标格在源图上的采样跨度：小于 1 像素说明图纸在放大源图（常见于小尺寸像素图）。
        // 放大时用最近邻采样（保持像素锐利、不糊）；缩小/等大时用 4×4 平均（抑制照片噪声与摩尔纹）。
        var stepX = cropWidth / width;
        var stepY = cropHeight / height;
        var useNearest = stepX < 1 || stepY < 1;

        const int sampleAxis = 4;
        for (var gy = 0; gy < height; gy++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var gx = 0; gx < width; gx++)
            {
                if (useNearest)
                {
                    var u = (gx + 0.5) / width;
                    var v = (gy + 0.5) / height;
                    var px = Math.Clamp((int)(cropX + u * cropWidth), 0, source.Width - 1);
                    var py = Math.Clamp((int)(cropY + v * cropHeight), 0, source.Height - 1);
                    output[gy * width + gx] = source.GetPixel(px, py);
                    continue;
                }
                double sumR = 0, sumG = 0, sumB = 0, sumA = 0;
                for (var sy = 0; sy < sampleAxis; sy++)
                {
                    for (var sx = 0; sx < sampleAxis; sx++)
                    {
                        var u = (gx + (sx + 0.5) / sampleAxis) / width;
                        var v = (gy + (sy + 0.5) / sampleAxis) / height;
                        var px = Math.Clamp((int)(cropX + u * cropWidth), 0, source.Width - 1);
                        var py = Math.Clamp((int)(cropY + v * cropHeight), 0, source.Height - 1);
                        var color = source.GetPixel(px, py);
                        var alpha = color.Alpha / 255.0;
                        sumR += color.Red * alpha;
                        sumG += color.Green * alpha;
                        sumB += color.Blue * alpha;
                        sumA += alpha;
                    }
                }

                var count = sampleAxis * sampleAxis;
                if (sumA < 0.05)
                {
                    output[gy * width + gx] = new SKColor(0, 0, 0, 0);
                }
                else
                {
                    output[gy * width + gx] = new SKColor(
                        (byte)Math.Clamp(sumR / sumA, 0, 255),
                        (byte)Math.Clamp(sumG / sumA, 0, 255),
                        (byte)Math.Clamp(sumB / sumA, 0, 255),
                        (byte)Math.Clamp(sumA / count * 255, 0, 255));
                }
            }
        }

        return output;
    }

    private readonly record struct SourceBounds(double X, double Y, double Width, double Height);

    /// <summary>
    /// 从画面四周执行连通区域搜索，仅移除与角落平均背景色足够接近且能连接到边界的格子。
    /// 该约束可以避免把主体内部恰好同色的区域误判为透明背景。
    /// </summary>
    private static void RemoveConnectedBackground(
        IReadOnlyList<SKColor> samples,
        bool[] transparent,
        int width,
        int height,
        double threshold,
        CancellationToken cancellationToken)
    {
        var corners = new[] { samples[0], samples[width - 1], samples[(height - 1) * width], samples[^1] }
            .Where(c => c.Alpha >= 40)
            .ToArray();
        if (corners.Length == 0) return;
        var background = ColorScience.RgbToLab(
            corners.Average(c => c.Red),
            corners.Average(c => c.Green),
            corners.Average(c => c.Blue));
        var queue = new Queue<int>();
        var visited = new bool[samples.Count];
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
            var index = queue.Dequeue();
            if (visited[index]) continue;
            visited[index] = true;
            var color = samples[index];
            if (color.Alpha < 40)
            {
                transparent[index] = true;
            }
            else
            {
                var lab = ColorScience.RgbToLab(color.Red, color.Green, color.Blue);
                if (ColorScience.DeltaE2000(lab, background) > threshold) continue;
                transparent[index] = true;
            }

            var x = index % width;
            var y = index / width;
            if (x > 0) queue.Enqueue(index - 1);
            if (x < width - 1) queue.Enqueue(index + 1);
            if (y > 0) queue.Enqueue(index - width);
            if (y < height - 1) queue.Enqueue(index + width);
        }
    }

    private static int[] Remap(
        IReadOnlyList<SKColor> samples,
        IReadOnlyList<bool> transparent,
        IReadOnlyList<LabColor> paletteLabs,
        IReadOnlyList<int> candidates,
        CancellationToken cancellationToken)
    {
        var cells = new int[samples.Count];
        for (var i = 0; i < cells.Length; i++)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            cells[i] = transparent[i] ? -1 : FindNearest(samples[i], paletteLabs, candidates);
        }
        return cells;
    }

    /// <summary>
    /// 使用 Floyd–Steinberg 将当前格子的 RGB 误差分配给后续格子。
    /// 扩散只发生在非透明区域，防止背景边缘把颜色误差带入主体或相反方向。
    /// </summary>
    private static int[] Dither(
        IReadOnlyList<SKColor> samples,
        IReadOnlyList<bool> transparent,
        int width,
        int height,
        IReadOnlyList<BeadColor> colors,
        IReadOnlyList<LabColor> paletteLabs,
        IReadOnlyList<int> candidates,
        CancellationToken cancellationToken)
    {
        var cells = new int[samples.Count];
        Array.Fill(cells, -1);
        var errors = new (double R, double G, double B)[samples.Count];
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                if (transparent[index]) continue;
                var source = samples[index];
                var adjusted = new SKColor(
                    ClampByte(source.Red + errors[index].R),
                    ClampByte(source.Green + errors[index].G),
                    ClampByte(source.Blue + errors[index].B));
                var match = FindNearest(adjusted, paletteLabs, candidates);
                cells[index] = match;
                var target = colors[match].Rgb;
                var error = (adjusted.Red - target[0], adjusted.Green - target[1], adjusted.Blue - target[2]);
                Spread(errors, transparent, width, height, x + 1, y, error, 7.0 / 16);
                Spread(errors, transparent, width, height, x - 1, y + 1, error, 3.0 / 16);
                Spread(errors, transparent, width, height, x, y + 1, error, 5.0 / 16);
                Spread(errors, transparent, width, height, x + 1, y + 1, error, 1.0 / 16);
            }
        }
        return cells;
    }

    private static void Spread(
        (double R, double G, double B)[] errors,
        IReadOnlyList<bool> transparent,
        int width,
        int height,
        int x,
        int y,
        (int R, int G, int B) error,
        double factor)
    {
        if (x < 0 || x >= width || y < 0 || y >= height) return;
        var index = y * width + x;
        if (transparent[index]) return;
        var current = errors[index];
        errors[index] = (current.R + error.R * factor, current.G + error.G * factor, current.B + error.B * factor);
    }

    private static byte ClampByte(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);

    /// <summary>
    /// 使用 CIEDE2000 选择人眼感知上最接近的实体色号；传入候选集后只搜索本图筛选出的颜色，
    /// 既减少计算量，也避免未入选色号在最终映射阶段重新出现。
    /// </summary>
    private static int FindNearest(SKColor color, IReadOnlyList<LabColor> paletteLabs, IReadOnlyList<int>? candidates)
    {
        var lab = ColorScience.RgbToLab(color.Red, color.Green, color.Blue);
        var bestIndex = -1;
        var bestDistance = double.MaxValue;
        if (candidates is null)
        {
            for (var i = 0; i < paletteLabs.Count; i++)
            {
                var distance = ColorScience.DeltaE2000(lab, paletteLabs[i]);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                bestIndex = i;
            }
        }
        else
        {
            foreach (var candidate in candidates)
            {
                var distance = ColorScience.DeltaE2000(lab, paletteLabs[candidate]);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                bestIndex = candidate;
            }
        }

        return bestIndex;
    }
}
