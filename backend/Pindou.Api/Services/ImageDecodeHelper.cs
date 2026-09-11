// 文件：ImageDecodeHelper.cs
// 用途：统一校验并解码用户上传的图像流。
// 核心职责：在分配完整像素位图前限制图片尺寸，并复用单个 Skia codec 完成解码，降低请求内存峰值。
// 版权：@董志伟-联系方式-makabak1204
// 最后修改：2026-08-24

using SkiaSharp;

namespace Pindou.Api.Services;

internal static class ImageDecodeHelper
{
    private const int MaxDimension = 8000;
    private const long MaxPixelCount = 16_000_000L;

    /// <summary>
    /// 读取一次图片头并直接解码到目标位图。调用方负责释放返回的位图。
    /// </summary>
    public static SKBitmap DecodeValidated(Stream imageStream)
    {
        using var codec = SKCodec.Create(imageStream)
            ?? throw new InvalidDataException("无法识别图片格式。");

        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0)
            throw new InvalidDataException("图片尺寸无效。");
        if (info.Width > MaxDimension || info.Height > MaxDimension)
            throw new InvalidDataException("图片尺寸不能超过8000×8000像素。");
        if ((long)info.Width * info.Height > MaxPixelCount)
            throw new InvalidDataException("图片总像素不能超过1600万（宽×高）。");

        var bitmap = new SKBitmap(info);
        try
        {
            var result = codec.GetPixels(bitmap.Info, bitmap.GetPixels());
            if (result != SKCodecResult.Success)
                throw new InvalidDataException("图片数据不完整或无法解码。");
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }
}
