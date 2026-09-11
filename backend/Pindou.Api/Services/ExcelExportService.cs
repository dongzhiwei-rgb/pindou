// 文件：ExcelExportService.cs
// 用途：将当前拼豆图纸转换为可打印、可配货的 Excel 工作簿。
// 核心职责：生成图纸网格、材料清单、色号统计和制作说明，并控制大图导出的内存占用。
// 版权：@董志伟-联系方式-makabak1204
// 最后修改：2026-08-25

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Pindou.Api.Models;

namespace Pindou.Api.Services;

public sealed class ExcelExportService
{
    public byte[] Create(PatternExportRequest request, CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
            var usedColorIndexes = request.Cells.Where(i => i >= 0).Distinct().OrderBy(i => i).ToArray();
            var styleMap = CreateStyles(stylesPart, request, usedColorIndexes, cancellationToken);
            var sheets = workbookPart.Workbook.AppendChild(new Sheets());

            AddPatternSheet(workbookPart, sheets, request, styleMap, cancellationToken);
            AddUsageSheet(workbookPart, sheets, request, cancellationToken);
            AddReadmeSheet(workbookPart, sheets, request, cancellationToken);
            workbookPart.Workbook.Save();
        }
        return stream.ToArray();
    }

    private static Dictionary<int, uint> CreateStyles(
        WorkbookStylesPart stylesPart,
        PatternExportRequest request,
        IReadOnlyList<int> usedColorIndexes,
        CancellationToken cancellationToken)
    {
        var fonts = new Fonts(
            new Font(new FontSize { Val = 9 }, new FontName { Val = "Microsoft YaHei" }),
            new Font(new Bold(), new Color { Rgb = "FFFFFFFF" }, new FontSize { Val = 10 }, new FontName { Val = "Microsoft YaHei" }),
            new Font(new Bold(), new FontSize { Val = 10 }, new FontName { Val = "Microsoft YaHei" }));
        var fills = new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
            SolidFill("14543D"),
            SolidFill("FFF7EC"));
        foreach (var index in usedColorIndexes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fills.Append(SolidFill(request.Colors[index].Hex.TrimStart('#').ToUpperInvariant()));
        }

        var thin = new Border(
            new LeftBorder(new Color { Rgb = "FFD6D1C6" }) { Style = BorderStyleValues.Thin },
            new RightBorder(new Color { Rgb = "FFD6D1C6" }) { Style = BorderStyleValues.Thin },
            new TopBorder(new Color { Rgb = "FFD6D1C6" }) { Style = BorderStyleValues.Thin },
            new BottomBorder(new Color { Rgb = "FFD6D1C6" }) { Style = BorderStyleValues.Thin },
            new DiagonalBorder());
        var borders = new Borders(new Border(), thin);
        var cellFormats = new CellFormats(
            new CellFormat(),
            new CellFormat
            {
                FontId = 1,
                FillId = 2,
                BorderId = 1,
                ApplyFill = true,
                ApplyBorder = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Center, Vertical = VerticalAlignmentValues.Center }
            },
            new CellFormat
            {
                FontId = 2,
                FillId = 3,
                BorderId = 1,
                ApplyFill = true,
                ApplyBorder = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Center, Vertical = VerticalAlignmentValues.Center }
            });

        var map = new Dictionary<int, uint>();
        uint nextStyle = 3;
        for (var i = 0; i < usedColorIndexes.Count; i++)
        {
            var colorIndex = usedColorIndexes[i];
            var color = request.Colors[colorIndex];
            var rgb = ParseHex(color.Hex);
            var luminance = 0.2126 * rgb.R + 0.7152 * rgb.G + 0.0722 * rgb.B;
            var fontId = luminance < 115 ? 1U : 0U;
            cellFormats.Append(new CellFormat
            {
                FontId = fontId,
                FillId = (uint)(4 + i),
                BorderId = 1,
                ApplyFill = true,
                ApplyBorder = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Center, Vertical = VerticalAlignmentValues.Center, ShrinkToFit = true }
            });
            map[colorIndex] = nextStyle++;
        }

        stylesPart.Stylesheet = new Stylesheet(fonts, fills, borders, cellFormats);
        stylesPart.Stylesheet.Save();
        return map;
    }

    private static void AddPatternSheet(
        WorkbookPart workbookPart,
        Sheets sheets,
        PatternExportRequest request,
        IReadOnlyDictionary<int, uint> styleMap,
        CancellationToken cancellationToken)
    {
        var part = workbookPart.AddNewPart<WorksheetPart>();
        var sheetData = new SheetData();
        var sheetViews = new SheetViews(new SheetView(
            new Pane
            {
                HorizontalSplit = 1,
                VerticalSplit = 1,
                TopLeftCell = "B2",
                ActivePane = PaneValues.BottomRight,
                State = PaneStateValues.Frozen
            }) { WorkbookViewId = 0U });
        var columns = new Columns(
            new Column { Min = 1, Max = 1, Width = 4.2, CustomWidth = true },
            new Column { Min = 2, Max = (uint)request.Width + 1, Width = 4.6, CustomWidth = true });
        part.Worksheet = new Worksheet(sheetViews, columns, sheetData);

        var header = new Row { Height = 21, CustomHeight = true };
        header.Append(TextCell("↘", 1));
        for (var x = 1; x <= request.Width; x++) header.Append(NumberCell(x, 1));
        sheetData.Append(header);

        for (var y = 0; y < request.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = new Row { Height = 21, CustomHeight = true };
            row.Append(NumberCell(y + 1, 1));
            for (var x = 0; x < request.Width; x++)
            {
                var index = request.Cells[y * request.Width + x];
                row.Append(index < 0
                    ? TextCell(string.Empty, 2)
                    : TextCell(request.Colors[index].Code, styleMap[index]));
            }
            sheetData.Append(row);
        }

        part.Worksheet.Append(new PageMargins { Left = 0.2, Right = 0.2, Top = 0.4, Bottom = 0.4, Header = 0.1, Footer = 0.1 });
        part.Worksheet.Append(new PageSetup { Orientation = OrientationValues.Landscape, FitToWidth = 1, FitToHeight = 0 });
        part.Worksheet.Save();
        sheets.Append(new Sheet { Id = workbookPart.GetIdOfPart(part), SheetId = 1, Name = "图纸" });
    }

    private static void AddUsageSheet(WorkbookPart workbookPart, Sheets sheets, PatternExportRequest request, CancellationToken cancellationToken)
    {
        var part = workbookPart.AddNewPart<WorksheetPart>();
        var data = new SheetData();
        part.Worksheet = new Worksheet(
            new Columns(
                new Column { Min = 1, Max = 1, Width = 14, CustomWidth = true },
                new Column { Min = 2, Max = 2, Width = 18, CustomWidth = true },
                new Column { Min = 3, Max = 3, Width = 14, CustomWidth = true },
                new Column { Min = 4, Max = 6, Width = 13, CustomWidth = true }),
            data);
        var header = new Row();
        foreach (var title in new[] { "色号", "颜色名称", "HEX", "数量", "1000颗/包", "占比" }) header.Append(TextCell(title, 1));
        data.Append(header);
        var usage = request.Cells.Where(i => i >= 0).GroupBy(i => i).OrderByDescending(g => g.Count()).ToArray();
        var total = usage.Sum(g => g.Count());
        foreach (var group in usage)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var color = request.Colors[group.Key];
            var row = new Row();
            row.Append(TextCell(color.Code, 0));
            row.Append(TextCell(color.Name, 0));
            row.Append(TextCell(color.Hex, 0));
            row.Append(NumberCell(group.Count(), 0));
            row.Append(NumberCell((int)Math.Ceiling(group.Count() / 1000.0), 0));
            row.Append(TextCell($"{group.Count() / (double)total:P1}", 0));
            data.Append(row);
        }
        part.Worksheet.Save();
        sheets.Append(new Sheet { Id = workbookPart.GetIdOfPart(part), SheetId = 2, Name = "材料清单" });
    }

    private static void AddReadmeSheet(WorkbookPart workbookPart, Sheets sheets, PatternExportRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var part = workbookPart.AddNewPart<WorksheetPart>();
        var data = new SheetData();
        part.Worksheet = new Worksheet(new Columns(new Column { Min = 1, Max = 1, Width = 24, CustomWidth = true }, new Column { Min = 2, Max = 2, Width = 64, CustomWidth = true }), data);
        var title = string.IsNullOrWhiteSpace(request.Title) ? "未命名拼豆图纸" : request.Title.Trim();
        var boardCount = (int)Math.Ceiling(request.Width / (double)request.BoardColumns) * (int)Math.Ceiling(request.Height / (double)request.BoardRows);
        var rows = new[]
        {
            ("项目名称", title),
            ("图纸规格", $"{request.Width} × {request.Height} 颗"),
            ("成品尺寸", $"约 {request.Width * request.BeadSize / 10:F1} × {request.Height * request.BeadSize / 10:F1} cm"),
            ("豆子规格", $"{request.BeadSize:0.0} mm"),
            ("品牌色卡", $"{request.BrandName} · {request.PaletteName}"),
            ("建议底板", $"{request.BoardColumns} × {request.BoardRows}，约需 {boardCount} 块"),
            ("总豆数", request.Cells.Count(i => i >= 0).ToString()),
            ("制作提示", "图纸中的屏幕色值仅供预览，采购与批量制作前请使用对应品牌实体色卡复核。")
        };
        foreach (var (key, value) in rows)
        {
            var row = new Row();
            row.Append(TextCell(key, 1));
            row.Append(TextCell(value, 0));
            data.Append(row);
        }
        part.Worksheet.Save();
        sheets.Append(new Sheet { Id = workbookPart.GetIdOfPart(part), SheetId = 3, Name = "说明" });
    }

    private static Fill SolidFill(string rgb) => new(new PatternFill(new ForegroundColor { Rgb = "FF" + rgb }) { PatternType = PatternValues.Solid });

    private static Cell TextCell(string value, uint style) => new()
    {
        DataType = CellValues.InlineString,
        StyleIndex = style,
        InlineString = new InlineString(new Text(value) { Space = SpaceProcessingModeValues.Preserve })
    };

    private static Cell NumberCell(int value, uint style) => new()
    {
        DataType = CellValues.Number,
        StyleIndex = style,
        CellValue = new CellValue(value)
    };

    private static (int R, int G, int B) ParseHex(string hex)
    {
        var value = hex.TrimStart('#');
        return (
            Convert.ToInt32(value[..2], 16),
            Convert.ToInt32(value.Substring(2, 2), 16),
            Convert.ToInt32(value.Substring(4, 2), 16));
    }
}
