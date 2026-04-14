// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using osu.Framework.Logging;
using osuTK;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

#pragma warning disable CA1310

namespace osu.Framework.Graphics.Video
{
    public sealed class FFmpegCliProcess : IDisposable
    {
        private Process? ffmpegProcess;
        private NamedPipeServerStream? audioPipe;
        private readonly Channel<byte[]> audioQueue = Channel.CreateBounded<byte[]>(1000);
        private readonly Channel<Image<Rgba32>> videoQueue = Channel.CreateBounded<Image<Rgba32>>(1000);
        private bool running;

        public required string OutputFilePath { get; init; }
        public required Vector2 VideoSize { get; init; }
        public required int Framerate { get; init; }
        public required int Samplerate { get; init; }
        public required string SampleFormat { get; init; }
        public required int Channels { get; init; }

        public void Start()
        {
            if (ffmpegProcess != null)
                throw new InvalidOperationException("Process has already started.");

            string videoCodec = "libx264";
            string extraArgs = "";

            // Try to use hardware acceleration codecs
            if (TestH264Qsv())
            {
                videoCodec = "h264_qsv";
            }

            if (OperatingSystem.IsLinux())
            {
                // VA-API is only on Linux
                string vaapiDevice = DetectVaapiDevice();
                if (vaapiDevice.Length > 0)
                {
                    extraArgs += $"-vaapi_device {vaapiDevice} -vf format=nv12,hwupload";
                    videoCodec = "h264_vaapi";
                }
            }

            string videoArgs = $"-f rawvideo -pix_fmt rgba -s {(int)VideoSize.X}x{(int)VideoSize.Y} -r {Framerate} -i -";

            string audioPipeName = $"osu-framework-ffmpeg-audio-{Guid.NewGuid():N}";
            audioPipe = new NamedPipeServerStream(audioPipeName, PipeDirection.Out);

            string audioArgs = $"-f {SampleFormat} -ar {Samplerate} -ac {Channels} -i {toNamedPipePath(audioPipeName)}";
            string mappingArgs = "-map 0:v -map 1:a";

            ffmpegProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-hide_banner -hwaccel auto -y {videoArgs} {audioArgs} {mappingArgs} {extraArgs} -c:v {videoCodec} \"{OutputFilePath}\"",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            Logger.Log($"ffmpeg args: {ffmpegProcess.StartInfo.Arguments}");

            // Better to log everything just in case
            ffmpegProcess.OutputDataReceived += (sender, e) => Logger.Log($"ffmpeg out: {e.Data}");
            ffmpegProcess.ErrorDataReceived += (sender, e) => Logger.Log($"ffmpeg err: {e.Data}");

            // ffmpeg will delay opening/connecting to the audio pipe because it reads a video frame first.
            // We will have to wait for that to happen in the audio consumer task below.
            audioPipe.WaitForConnectionAsync();

            ffmpegProcess.Start();

            ffmpegProcess.BeginOutputReadLine();
            ffmpegProcess.BeginErrorReadLine();

            running = true;

            // Video consumer thread
            Task.Run(() =>
            {
                if (ffmpegProcess == null)
                    throw new InvalidOperationException("ffmpegProcess is null");
                // This will throw if StandardInput isn't open
                var ffmpegStdin = ffmpegProcess.StandardInput.BaseStream;
                while (running || videoQueue.Reader.Count > 0)
                {
                    if (!videoQueue.Reader.TryRead(out Image<Rgba32>? _image) || _image == null)
                    {
                        Thread.Yield();
                        continue;
                    }
                    using var image = _image;
                    if (!image.DangerousTryGetSinglePixelMemory(out var memory))
                        throw new InvalidOperationException("Image memory is not contiguous");
                    ffmpegStdin.Write(MemoryMarshal.AsBytes(memory.Span));
                }
                // Close just the standard input, not the whole process.
                // This lets ffmpeg detect that both of its inputs have closed and will gracefully finish muxing.
                ffmpegProcess.StandardInput.Close();
            });

            // Audio consumer thread
            Task.Run(() =>
            {
                if (audioPipe == null)
                    throw new InvalidOperationException("audioPipe is null");
                while (running || audioQueue.Reader.Count > 0)
                {
                    // ffmpeg will delay opening/connecting to the audio pipe because it reads a video frame first.
                    // We will have to wait for that to happen.
                    if (!audioPipe.IsConnected || !audioQueue.Reader.TryRead(out byte[]? audio) || audio == null)
                    {
                        Thread.Yield();
                        continue;
                    }
                    audioPipe.Write(audio);
                }
                audioPipe.Close();
            });
        }

        public bool WriteFrame(Image<Rgba32> image)
        {
            if (ffmpegProcess == null)
                throw new InvalidOperationException("ffmpeg has not been started yet");
            if (ffmpegProcess.HasExited)
                throw new InvalidOperationException("ffmpeg has exited");
            if (image.Size.Width != VideoSize.X || image.Size.Height != VideoSize.Y)
                throw new ArgumentException($"Image size ({image.Size}) is different from ffmpeg size ({VideoSize})");
            return videoQueue.Writer.TryWrite(image);
        }

        public bool WriteAudio(ReadOnlySpan<byte> audioData)
        {
            if (ffmpegProcess == null || audioPipe == null)
                throw new InvalidOperationException("ffmpeg has not been started yet");
            if (ffmpegProcess.HasExited)
                throw new InvalidOperationException("ffmpeg has exited");
            byte[] audioCopy = new byte[audioData.Length];
            audioData.CopyTo(audioCopy);
            return audioQueue.Writer.TryWrite(audioCopy);
        }

        public void Dispose()
        {
            // We just set the flag.
            // The tasks in Start() will close their respective pipes
            // only when they have emptied their queues.
            running = false;
        }

        public static bool TestFfmpegArguments(string arguments)
        {
            using var process = new Process()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            process.WaitForExit();

            return process.ExitCode == 0;
        }

        private string toNamedPipePath(string pipeName)
        {
            if (OperatingSystem.IsWindows())
                return $@"\\.\pipe\{pipeName}";
            else
            {
                // On Linux/Mac, NamedPipeServerStream creates a socket file in /tmp/
                // FFmpeg needs the 'unix' protocol prefix to connect to a socket
                string socketPath = Path.Combine(Path.GetTempPath(), $"CoreFxPipe_{pipeName}");
                return $"unix://{socketPath}";
            }
        }

        public static bool TestH264Qsv()
        {
            return TestFfmpegArguments("-v warning -f lavfi -i testsrc=1280x720:d=1 -c:v h264_qsv -f null -");
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
                                   "-vf format=nv12,hwupload " +
                                   "-c:v h264_vaapi -f null -";

                try
                {
                    if (TestFfmpegArguments(arguments))
                        return filePath;
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