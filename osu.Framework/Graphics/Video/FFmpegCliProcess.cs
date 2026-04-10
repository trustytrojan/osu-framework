// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Diagnostics;
using System.Runtime.InteropServices;
using osu.Framework.Extensions.ImageExtensions;
using osuTK;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace osu.Framework.Graphics.Video
{
    public class FFmpegCliProcess
    {
        private Process ffmpegProcess;

        // We can add audio later on.
        public FFmpegCliProcess(string outputFilePath, Vector2 videoSize, int framerate, string videoCodec = "libx264")
        {
            ffmpegProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-hide_banner -hwaccel auto -y -f rawvideo -pix_fmt rgba -s {videoSize.X}x{videoSize.Y} -r {framerate} -i - -c:v {videoCodec} out.mp4",
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            ffmpegProcess.Start();
        }

        public void WriteFrame(Image<Rgba32> image)
        {
            using var pixelMemory = image.CreateReadOnlyPixelMemory();
            var rgbaBytes = MemoryMarshal.AsBytes(pixelMemory.Span);
            ffmpegProcess.StandardInput.BaseStream.Write(rgbaBytes);
        }
    }
}