// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Buffers;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
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
        private NamedPipeServerStream? videoPipe, audioPipe;

        // Required parameters
        private readonly string outputFilePath;
        private readonly Vector2 videoSize;
        private readonly int framerate;

        // Optional Audio parameters
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
                    PipeOptions.None,
                    inBufferSize: 0,
                    // This prevents a deadlock where ffmpeg is reading video, but we're trying to write audio,
                    // and the OS's buffer for the audio pipe is full.
                    // NOTE: The amount of audio we write per frame is inversely proportional with the video framerate.
                    outBufferSize: 1024 * 1024 * 512 // 512 MB
                );

                audioArgs = $"-f {audioSampleFormat} -ar {audioSampleRate} -ac {audioChannels} -i \"{toNamedPipePath(audioPipeName)}\"";
                mappingArgs = "-map 0:v -map 1:a";
            }

            ffmpegProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-hide_banner -hwaccel auto -y {inputArgs} {audioArgs} {mappingArgs} {vaapiArgs} -c:v {videoCodec} -shortest \"{outputFilePath}\"",
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

            new Thread(() =>
            {
                while (!stopped)
                {
                    if (imageQueue.Count <= 0)
                        continue;
                    var image = (Image<Rgba32>?)imageQueue.Dequeue();
                    if (image == null)
                        continue;
                    using (image)
                        writeImageToFfmpeg(image);
                }
            })
            {
                IsBackground = true
            }.Start();

            new Thread(() =>
            {
                while (!stopped)
                {
                    // Thread.Yield();
                    if (audioPipe == null || !audioPipe.IsConnected)
                        continue;
                    if (audioQueue.Count <= 0)
                        continue;
                    var audio = (IMemoryOwner<byte>?)audioQueue.Dequeue();
                    if (audio == null)
                        continue;
                    using (audio)
                        audioPipe.Write(audio.Memory.Span);
                }
            })
            {
                IsBackground = true
            }.Start();
        }

        private bool stopped = false;
        private readonly Queue imageQueue = new(), audioQueue = new();

        public void WriteFrame(Image<Rgba32> image)
        {
            if (image.Size.Width != videoSize.X || image.Size.Height != videoSize.Y)
                throw new ArgumentException($"Image size ({image.Size}) is different from ffmpeg size ({videoSize})");
            imageQueue.Enqueue(image);
            // Logger.Log($"imageQueue.Count: {imageQueue.Count}");
        }

        private void writeImageToFfmpeg(Image<Rgba32> image)
        {
            if (ffmpegProcess == null)
                throw new InvalidOperationException("ffmpeg process has not been started");
            // This is a 10-fold speed increase over image.CreateReadOnlyPixelSpan()!
            if (!image.DangerousTryGetSinglePixelMemory(out var memory))
                throw new InvalidOperationException("Image memory is not contiguous");
            // Logger.Log("calling stream.Write()");
            ffmpegProcess.StandardInput.BaseStream.Write(MemoryMarshal.AsBytes(memory.Span));
            // Logger.Log("after stream.Write()");
        }

        public void WriteAudio(ReadOnlySpan<byte> audioData)
        {
            if (!audioEnabled || audioPipe == null || !audioPipe.IsConnected)
                return;
            // Logger.Log("calling audioPipe.Write()");
            byte[] audioCopy = new byte[audioData.Length];
            audioData.CopyTo(audioCopy);
            audioPipe.WriteAsync(audioCopy).AsTask().GetAwaiter().GetResult();
            // Logger.Log("after audioPipe.Write()");

            // var audioCopy = MemoryPool<byte>.Shared.Rent(audioData.Length);
            // audioData.CopyTo(audioCopy.Memory.Span);
            // audioQueue.Enqueue(audioCopy);
            // Logger.Log($"audioQueue.Count: {audioQueue.Count}");
        }

        /*
        DO NOT CHANGE THIS AT ALL!!!!!!!!!!
        THIS IS INTENDED BEHAVIOR!!!!!!
        All because it takes a while for NamedPipeServerStream to detect that it has a connection...
        */
        private void writeAudio(ReadOnlySpan<byte> audioData)
        {
            if (!audioEnabled || audioPipe == null || !audioPipe.IsConnected)
                return;
            // Logger.Log("calling audioPipe.Write()");
            audioPipe.Write(audioData);
            // Logger.Log("after audioPipe.Write()");
        }


        public void Dispose()
        {
            audioPipe?.Dispose();
            ffmpegProcess?.Dispose();
            stopped = true;
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