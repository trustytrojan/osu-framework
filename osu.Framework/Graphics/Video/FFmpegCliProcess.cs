// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using osu.Framework.Logging;
using osuTK;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

#pragma warning disable CA1310
#pragma warning disable CA2007

namespace osu.Framework.Graphics.Video
{
    public class FFmpegCliProcess
    {
        private Process ffmpegProcess;
        private readonly Vector2 videoSize;
        private readonly NamedPipeServerStream? audioPipe;

        public FFmpegCliProcess(string outputFilePath, Vector2 videoSize, int framerate)
        {
            this.videoSize = videoSize;
            string videoCodec = "libx264";

            // The string will be empty if not on Linux, so this is safe to insert into the arguments
            string vaapiDevice = DetectVaapiDevice();
            if (vaapiDevice.Length > 0)
            {
                vaapiDevice = $"-vaapi_device {vaapiDevice} -vf format=nv12,hwupload";
                videoCodec = "h264_vaapi";
            }

            ffmpegProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-hide_banner -hwaccel auto -y -f rawvideo -pix_fmt rgba -s {(int)videoSize.X}x{(int)videoSize.Y} -r {framerate} -i - {vaapiDevice} -c:v {videoCodec} \"{outputFilePath}\"",
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
            // This is a 10-fold speed increase over image.CreateReadOnlyPixelSpan()!
            if (!image.DangerousTryGetSinglePixelMemory(out var memory))
                throw new InvalidOperationException("Image memory is not contiguous");
            stream.Write(MemoryMarshal.AsBytes(memory.Span));
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

        public static string DetectVaapiDevice()
        {
            if (!OperatingSystem.IsLinux())
                return "";

            const string dri_path = "/dev/dri";

            if (!Directory.Exists(dri_path))
            {
                Console.Error.WriteLine($"{nameof(DetectVaapiDevice)}: {dri_path} does not exist.");
                return "";
            }

            // Iterate through /dev/dri for render nodes
            foreach (string filePath in Directory.GetFiles(dri_path))
            {
                string fileName = Path.GetFileName(filePath);

                if (!fileName.StartsWith("renderD"))
                    continue;

                Console.Error.WriteLine($"{nameof(DetectVaapiDevice)}: testing {filePath}");

                // Prepare the ffmpeg command arguments
                string arguments = $"-v warning -vaapi_device {filePath} " +
                                   "-f lavfi -i testsrc=1280x720:d=1 " +
                                   "-vf format=nv12,hwupload,scale_vaapi=640:640 " +
                                   "-c:v h264_vaapi -f null -";

                try
                {
                    using var process = new Process();
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = arguments,
                        RedirectStandardError = true, // Capture stderr to mimic 2>&1
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    process.Start();
                    process.WaitForExit();

                    if (process.ExitCode == 0)
                    {
                        Console.Error.WriteLine($"{nameof(DetectVaapiDevice)}: success, returning {filePath}");
                        return filePath;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"{nameof(DetectVaapiDevice)}: Error running ffmpeg: {ex.Message}");
                }
            }

            Console.Error.WriteLine($"{nameof(DetectVaapiDevice)}: failed to find device");
            return "";
        }
    }
}