// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using osu.Framework.Extensions.ImageExtensions;
using osu.Framework.Logging;
using osuTK;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace osu.Framework.Graphics.Video
{
    public class FFmpegCliProcess
    {
        private Process ffmpegProcess;
        private readonly Vector2 videoSize;

        public FFmpegCliProcess(string outputFilePath, Vector2 videoSize, int framerate, string videoCodec = "libx264")
        {
            this.videoSize = videoSize;
            ffmpegProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-hide_banner -hwaccel auto -y -f rawvideo -pix_fmt rgba -s {(int)videoSize.X}x{(int)videoSize.Y} -r {framerate} -i - -c:v {videoCodec} \"{outputFilePath}\"",
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            Logger.Log($"ffmpegProcess.StartInfo.Arguments: {ffmpegProcess.StartInfo.Arguments}");
            ffmpegProcess.Start();
        }

        public FFmpegCliProcess(string outputFilePath, Vector2 videoSize, int framerate, string audioFilePath, string videoCodec = "libx264")
        {
            this.videoSize = videoSize;
            ffmpegProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-hide_banner -hwaccel auto -y -f rawvideo -pix_fmt rgba -s {(int)videoSize.X}x{(int)videoSize.Y} -r {framerate} -i - -i \"{audioFilePath}\" -map 0 -map 1:a -c:v {videoCodec} -c:a copy -shortest \"{outputFilePath}\"",
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            Logger.Log($"ffmpegProcess.StartInfo.Arguments: {ffmpegProcess.StartInfo.Arguments}");
            ffmpegProcess.Start();
        }

        public void WriteFrame(Image<Rgba32> image)
        {
            if (image.Size.Width != videoSize.X || image.Size.Height != videoSize.Y)
                throw new ArgumentException($"Image size ({image.Size}) is different from ffmpeg size ({videoSize})");
            var stream = ffmpegProcess.StandardInput.BaseStream;
            if (!stream.CanWrite)
                return;
            using var pixelMemory = image.CreateReadOnlyPixelMemory();
            var rgbaBytes = MemoryMarshal.AsBytes(pixelMemory.Span);
            stream.Write(rgbaBytes);
        }

        public void Close()
        {
            ffmpegProcess.Close();
        }
    }
}