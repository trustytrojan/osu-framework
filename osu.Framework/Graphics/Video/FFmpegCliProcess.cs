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
    public class FFmpegCliProcess
    {
        private Process? ffmpegProcess;
        private NamedPipeServerStream? audioPipe;
        private readonly Channel<byte[]> audioQueue = Channel.CreateBounded<byte[]>(1000);
        private readonly Channel<Image<Rgba32>> videoQueue = Channel.CreateBounded<Image<Rgba32>>(1000);
        private bool running;

        private readonly string outputFilePath;
        private readonly Vector2 videoSize;
        private readonly int framerate;

        private bool audioEnabled;
        private int audioSampleRate;
        private string? audioSampleFormat;
        private int audioChannels;

        public FFmpegCliProcess(string outputFilePath, Vector2 videoSize, int framerate)
        {
            this.outputFilePath = outputFilePath;
            this.videoSize = videoSize;
            this.framerate = framerate;
        }

        /// <summary>
        /// Enables audio processing using a named pipe.
        /// </summary>
        public FFmpegCliProcess EnableAudio(int sampleRate, string sampleFormat, int channels)
        {
            audioEnabled = true;
            audioSampleRate = sampleRate;
            audioSampleFormat = sampleFormat;
            audioChannels = channels;
            return this;
        }

        public void Start()
        {
            if (ffmpegProcess != null)
                throw new InvalidOperationException("Process has already started.");

            // string videoCodec = "libx264";
            string videoCodec = "h264_qsv"; // for my laptop on windows...
            string vaapiArgs = "";

            // Hardware acceleration check for Linux
            string vaapiDevice = DetectVaapiDevice();
            if (vaapiDevice.Length > 0)
            {
                vaapiArgs = $"-vaapi_device {vaapiDevice} -vf format=nv12,hwupload";
                videoCodec = "h264_vaapi";
            }

            string inputArgs = $"-f rawvideo -pix_fmt rgba -s {(int)videoSize.X}x{(int)videoSize.Y} -r {framerate} -i -";
            string audioArgs = "";
            string mappingArgs = "-map 0:v";
            string audioPipeName;

            if (audioEnabled)
            {
                audioPipeName = $"osu-framework-ffmpeg-audio-{Guid.NewGuid():N}";
                audioPipe = new NamedPipeServerStream(
                    audioPipeName,
                    PipeDirection.Out,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.None
                );

                audioArgs = $"-f {audioSampleFormat} -ar {audioSampleRate} -ac {audioChannels} -i \"{toNamedPipePath(audioPipeName)}\"";
                mappingArgs = "-map 0:v -map 1:a";
            }

            ffmpegProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-hide_banner -hwaccel auto -y {inputArgs} {audioArgs} {mappingArgs} {vaapiArgs} -c:v {videoCodec} \"{outputFilePath}\"",
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            if (OperatingSystem.IsWindows())
            {
                ffmpegProcess.StartInfo.RedirectStandardOutput = true;
                ffmpegProcess.StartInfo.RedirectStandardError = true;
                ffmpegProcess.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                        Console.WriteLine(e.Data);
                };
                ffmpegProcess.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                        Console.WriteLine(e.Data);
                };
            }

            Logger.Log($"ffmpegProcess.StartInfo.Arguments: {ffmpegProcess.StartInfo.Arguments}");

            /*
            DO NOT CHANGE THIS AT ALL!!!!!!!!!!
            THIS IS INTENDED BEHAVIOR!!!!!!
            All because it takes a while for NamedPipeServerStream to detect that it has a connection...
            */
            audioPipe?.WaitForConnectionAsync();
            ffmpegProcess.Start();

            if (OperatingSystem.IsWindows())
            {
                ffmpegProcess.BeginOutputReadLine();
                ffmpegProcess.BeginErrorReadLine();
            }

            running = true;

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
                ffmpegProcess.StandardInput.Close();
            });

            Task.Run(() =>
            {
                if (audioPipe == null)
                    throw new InvalidOperationException("audioPipe is null");
                while (running || audioQueue.Reader.Count > 0)
                {
                    if (!audioQueue.Reader.TryRead(out byte[]? audio) || audio == null)
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
            if (image.Size.Width != videoSize.X || image.Size.Height != videoSize.Y)
                throw new ArgumentException($"Image size ({image.Size}) is different from ffmpeg size ({videoSize})");
            return videoQueue.Writer.TryWrite(image);
        }

        public bool WriteAudio(ReadOnlySpan<byte> audioData)
        {
            /*
            DO NOT CHANGE THIS RETURN GUARD AT ALL!!!!!!!!!!
            THIS IS INTENDED BEHAVIOR!!!!!!
            All because it takes a while for NamedPipeServerStream to detect that it has a connection...
            */
            if (!audioEnabled || audioPipe == null || !audioPipe.IsConnected)
                return true;
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