using System.IO;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Visualisation;
using osu.Framework.Screens;
using osu.Framework.Platform;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using osu.Framework.Logging;
using System.Diagnostics;

namespace TemplateGame.Game
{
    public partial class TemplateGameGame : TemplateGameGameBase
    {
        private const int target_frames = 30;

        private readonly Container captureLayer = new() { RelativeSizeAxes = Axes.Both };

        private ScreenStack captureStack = null;
        private DrawableScreenshotter captureScreenshotter = null;
        private GameHost host = null;
        private string outputDirectory = null;

        private int requestedFrames;
        private int completedFrames;
        private bool captureInFlight;

        [BackgroundDependencyLoader]
        private void load(GameHost host)
        {
            this.host = host;

            // Host all runtime-capture drawables.
            Child = captureLayer;

            outputDirectory = host.Storage.GetFullPath("offscreen-captures", createIfNotExisting: true);
            Directory.CreateDirectory(outputDirectory);

            captureStack = new ScreenStack
            {
                Size = DrawSize,
            };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            // Load and tick a detached stack used only for off-screen capture.
            LoadComponent(captureStack);
            captureStack.Push(new CaptureMainScreen());

            // Re-use one screenshotter and trigger captures explicitly.
            captureScreenshotter = new DrawableScreenshotter(captureStack, onImageReceived, expireAfterCapture: false);
            captureLayer.Add(captureScreenshotter);
        }

        protected override void Update()
        {
            base.Update();

            if (completedFrames >= target_frames)
                return;

            captureStack.Size = DrawSize;
            captureStack.UpdateSubTree();
            captureStack.UpdateSubTreeMasking();

            if (captureInFlight || requestedFrames >= target_frames)
                return;

            captureInFlight = true;
            requestedFrames++;
            captureScreenshotter.RequestCapture();
        }

        private void onImageReceived(Image<Rgba32> image)
        {
            if (image != null)
            {
                string path = Path.Combine(outputDirectory, $"frame-{completedFrames:0000}.png");

                var stopwatch = new Stopwatch();
                stopwatch.Start();
                using (image)
                    image.SaveAsPng(path);
                stopwatch.Stop();
                Logger.Log($"Saved image frame-{completedFrames:0000}.png in {stopwatch.Elapsed.TotalMilliseconds}ms");

                completedFrames++;
            }

            captureInFlight = false;

            if (completedFrames >= target_frames)
                host.Exit();
        }
    }
}
