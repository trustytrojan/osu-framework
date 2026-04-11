// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
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
        private readonly NamedPipeServerStream? audioPipe;

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

        public FFmpegCliProcess(string outputFilePath, Vector2 videoSize, int framerate, int audioSampleRate, string audioSampleFormat, int audioChannels, string videoCodec = "libx264")
        {
            this.videoSize = videoSize;

            string audioPipeName = $"osu-framework-ffmpeg-audio-{Guid.NewGuid():N}";
            audioPipe = new NamedPipeServerStream(audioPipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            ffmpegProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-hide_banner -hwaccel auto -y -f rawvideo -pix_fmt rgba -s {(int)videoSize.X}x{(int)videoSize.Y} -r {framerate} -i - -f {audioSampleFormat} -ar {audioSampleRate} -ac {audioChannels} -i \"{toNamedPipePath(audioPipeName)}\" -map 0:v -map 1:a -c:v {videoCodec} -c:a copy -shortest \"{outputFilePath}\"",
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            Logger.Log($"ffmpegProcess.StartInfo.Arguments: {ffmpegProcess.StartInfo.Arguments}");
        }

        public async Task Start()
        {
            var connectTask = audioPipe?.WaitForConnectionAsync();

            ffmpegProcess.Exited += (_, _) =>
            {
                if (ffmpegProcess.ExitCode != 0)
                    throw new IOException($"ffmpeg exited with {ffmpegProcess.ExitCode}");
            };
            ffmpegProcess.Start();

            if (connectTask == null)
                return;

            // 3. Now wait for the connection to actually be established.
            // Set a timeout so you don't hang forever if FFmpeg fails.
            if (await Task.WhenAny(connectTask, Task.Delay(5000)) == connectTask)
            {
                // Connection successful
                await connectTask;
            }
            else
            {
                // FFmpeg likely failed to connect or crashed
                throw new TimeoutException($"FFmpeg failed to connect to the audio pipe. Exit Code: {(ffmpegProcess.HasExited ? ffmpegProcess.ExitCode : "Still running")}");
            }
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

        public void WriteAudio(ReadOnlySpan<byte> audioData)
        {
            if (audioPipe == null || !audioPipe.IsConnected)
                return;

            audioPipe.Write(audioData);
        }

        public void Dispose()
        {
            audioPipe?.Dispose();
            ffmpegProcess.Dispose();
        }

        private string toNamedPipePath(string pipeName)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return $@"\\.\pipe\{pipeName}";
            }
            else
            {
                // On Linux/Mac, .NET creates a socket file in /tmp/
                // FFmpeg needs the 'unix' protocol prefix to connect to a socket
                string socketPath = Path.Combine(Path.GetTempPath(), $"CoreFxPipe_{pipeName}");
                return $"unix://{socketPath}";
            }
        }
    }
}