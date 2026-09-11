// 文件：CatalogModels.cs
// 用途：定义厂商、色卡、底板、量化结果和导出请求等 API 数据契约。
// 核心职责：统一前后端字段结构，保持目录查询、图纸生成与文件导出的类型一致性。
// 版权：@董志伟-联系方式-makabak1204
// 最后修改：2026-08-21

namespace Pindou.Api.Models;

public sealed record BeadColor(
    string Id,
    string Brand,
    string Code,
    string Name,
    string Hex,
    int[] Rgb,
    double[] Lab,
    string Source,
    string License);

public sealed record PaletteSummary(
    string Id,
    string Name,
    int ColorCount,
    double[] BeadSizes,
    bool Verified,
    string Version,
    string Note);

public sealed record BrandSummary(
    string Id,
    string Name,
    string Country,
    string Kind,
    int HeatRank,
    IReadOnlyList<PaletteSummary> Palettes);

public sealed record PaletteDetail(
    string BrandId,
    string BrandName,
    string PaletteId,
    string PaletteName,
    int ColorCount,
    bool Verified,
    string Version,
    string Note,
    IReadOnlyList<BeadColor> Colors);

public sealed record BoardPreset(
    string Id,
    string Name,
    int Columns,
    int Rows,
    double BeadSize,
    string Category,
    string Note);

public sealed record UsageItem(int ColorIndex, string Code, string Name, string Hex, int Count);

public sealed record QuantizeResponse(
    int Width,
    int Height,
    string BrandId,
    string PaletteId,
    IReadOnlyList<BeadColor> Colors,
    int[] Cells,
    IReadOnlyList<UsageItem> Usage,
    int UsedColorCount,
    int BeadCount,
    long ProcessingMs,
    string Algorithm);

public sealed record ExportColor(string Code, string Name, string Hex);

public sealed record PatternExportRequest(
    string? Title,
    int Width,
    int Height,
    double BeadSize,
    int BoardColumns,
    int BoardRows,
    string BrandName,
    string PaletteName,
    IReadOnlyList<ExportColor> Colors,
    IReadOnlyList<int> Cells);
