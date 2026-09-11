// 文件：PaletteCatalog.cs
// 用途：加载并组织 MARD 等厂商的公开色卡与版本信息。
// 核心职责：建立品牌、色卡、豆径之间的索引，为查询和量化服务提供只读目录。
// 版权：@董志伟-联系方式-makabak1204
// 最后修改：2026-08-24

using System.Text.Json;
using Pindou.Api.Models;

namespace Pindou.Api.Services;

public sealed class PaletteCatalog
{
    private readonly Dictionary<string, PaletteDefinition> _palettes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<BrandDefinition> _brands;
    private readonly IReadOnlyList<BrandSummary> _brandSummaries;

    public PaletteCatalog(IWebHostEnvironment environment)
    {
        var root = Path.Combine(environment.ContentRootPath, "Data", "Palettes");
        var mard = Load(root, "mard.json");

        Add("mard-221", "mard", "MARD 221 标准色", "2026-community-221",
            mard.Where(c => IsMardStandard(c.Code)).ToList(), [2.6, 5.0], true,
            "A–H、M基础系列；屏幕色值为近似值，量产前请用实体色卡校准。");
        Add("mard-291", "mard", "MARD 291 全色", "2026-community-291",
            mard, [2.6, 5.0], true,
            "221基础色加P/Q/R/T/Y/ZG扩展色；屏幕色值为近似值。");
        Add("coco-291", "coco", "COCO 291 对照色", "community-291",
            Load(root, "coco.json"), [2.6], false,
            "店铺色号体系存在264/291/303/329等版本，本库采用291色公开对照版。");
        Add("manman-290", "manman", "漫漫 290 对照色", "community-290",
            Load(root, "manman.json"), [2.6, 5.0], false,
            "公开对照库版本；购买前请与商家实体色卡核对。");
        Add("panpan-291", "panpan", "盼盼 291 对照色", "community-291",
            Load(root, "panpan.json"), [2.6], false,
            "纯数字编号公开对照版；不同批次与店铺版本可能存在差异。");
        Add("mixiaowo-291", "mixiaowo", "咪小窝 291 对照色", "community-291",
            Load(root, "mixiaowo.json"), [2.6], false,
            "纯数字编号公开对照版；建议以实际库存为准。");
        Add("artkal-s-core", "artkal-s", "ARTKAL S 5mm 核心色", "core-35",
            Load(root, "artkal-s.json"), [5.0], false,
            "当前内置35个高频色；正式版可继续导入官方S-5mm完整色卡。");
        Add("perler-core", "perler", "Perler 5mm 核心色", "core-70",
            Load(root, "perler.json"), [5.0], false,
            "当前内置70个常用色，编号为工具对照编号。");
        Add("hama-core", "hama", "Hama 核心色", "core-50",
            Load(root, "hama.json"), [2.5, 5.0, 10.0], false,
            "当前内置50个常用色；Mini/Midi/Maxi并非每个色号都实际供货。");

        _brands =
        [
            new("mard", "MARD / 马尔德", "中国", "主流品牌色卡", 1),
            new("coco", "COCO / 可可", "中国", "店铺色号体系", 2),
            new("manman", "漫漫家 / MM拼豆", "中国", "店铺色号体系", 3),
            new("panpan", "盼盼拼豆", "中国", "店铺色号体系", 4),
            new("mixiaowo", "咪小窝", "中国", "店铺色号体系", 5),
            new("artkal-s", "ARTKAL / 艾特卡", "中国", "国际品牌色卡", 6),
            new("perler", "Perler / 帕勒", "美国", "国际品牌色卡", 7),
            new("hama", "Hama / 哈马", "丹麦", "国际品牌色卡", 8)
        ];

        // 目录在进程生命周期内只读，启动时生成一次摘要，避免每个概览请求重复排序、筛选和分配列表。
        _brandSummaries = _brands
            .OrderBy(brand => brand.HeatRank)
            .Select(brand => new BrandSummary(
                brand.Id,
                brand.Name,
                brand.Country,
                brand.Kind,
                brand.HeatRank,
                _palettes.Values
                    .Where(palette => palette.BrandId.Equals(brand.Id, StringComparison.OrdinalIgnoreCase))
                    .Select(palette => palette.ToSummary())
                    .ToArray()))
            .ToArray();
    }

    public IReadOnlyList<BrandSummary> GetBrands() => _brandSummaries;

    public PaletteDetail? GetPalette(string brandId, string paletteId)
    {
        if (!_palettes.TryGetValue(paletteId, out var palette) ||
            !palette.BrandId.Equals(brandId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var brand = _brands.First(b => b.Id.Equals(brandId, StringComparison.OrdinalIgnoreCase));
        return new PaletteDetail(
            brand.Id,
            brand.Name,
            palette.Id,
            palette.Name,
            palette.Colors.Count,
            palette.Verified,
            palette.Version,
            palette.Note,
            palette.Colors);
    }

    public PaletteDefinition? Find(string paletteId) =>
        _palettes.GetValueOrDefault(paletteId);

    private void Add(
        string id,
        string brandId,
        string name,
        string version,
        IReadOnlyList<BeadColor> colors,
        double[] beadSizes,
        bool verified,
        string note)
    {
        _palettes[id] = new PaletteDefinition(id, brandId, name, version, colors, beadSizes, verified, note);
    }

    private static List<BeadColor> Load(string root, string fileName)
    {
        var path = Path.Combine(root, fileName);
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<List<BeadColor>>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? [];
    }

    private static bool IsMardStandard(string code)
    {
        if (code.StartsWith("ZG", StringComparison.OrdinalIgnoreCase)) return false;
        return code.Length > 0 && "ABCDEFGHM".Contains(char.ToUpperInvariant(code[0]));
    }

    private sealed record BrandDefinition(string Id, string Name, string Country, string Kind, int HeatRank);
}

public sealed record PaletteDefinition(
    string Id,
    string BrandId,
    string Name,
    string Version,
    IReadOnlyList<BeadColor> Colors,
    double[] BeadSizes,
    bool Verified,
    string Note)
{
    public PaletteSummary ToSummary() =>
        new(Id, Name, Colors.Count, BeadSizes, Verified, Version, Note);
}
