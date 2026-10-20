using System.Drawing.Imaging;

namespace LidAwake;

// Screen pictures as JPEG files, for pages that can't play video and for /screen.jpg
// (FrameStream.swift on the Mac). A JPEG starts with FF D8, which is how the page tells it from
// a video frame on the socket.
public static class JpegFrames
{
    static readonly ImageCodecInfo? Jpeg =
        ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

    // `quality` 0 to 100 (the Mac's 0.7 is 70).
    public static byte[] Encode(ScreenImage image, int quality)
    {
        if (Jpeg == null) throw new InvalidOperationException("No JPEG encoder.");
        using var bitmap = new Bitmap(image.Width, image.Height, image.Stride, PixelFormat.Format32bppRgb, image.Pixels);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(quality, 1, 100));
        using var stream = new MemoryStream();
        bitmap.Save(stream, Jpeg, parameters);
        return stream.ToArray();
    }
}
