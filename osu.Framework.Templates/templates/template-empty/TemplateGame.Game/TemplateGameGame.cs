using System.IO;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Visualisation;
using osu.Framework.Screens;
using osu.Framework.Platform;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Threading.Tasks;

namespace TemplateGame.Game
{
    public partial class TemplateGameGame : TemplateGameGameBase
    {
        private const int target_frames = 30;

        private readonly Container captureLayer = new() { RelativeSizeAxes = Axes.Both };

        private ScreenStack visibleStack = null;
        private ScreenStack captureStack = null;
        private DrawableScreenshotter captureScreenshotter = null;
        private GameHost host = null;
        private string outputDirectory = null;

        private int requestedFrames;
        private int completedFrames;
        private bool currentlyCapturing;

        [BackgroundDependencyLoader]
        private void load(GameHost host)
        {
            this.host = host;

            // Host all runtime-capture drawables.
            Child = captureLayer;

            outputDirectory = host.Storage.GetFullPath("offscreen-captures", createIfNotExisting: true);
            Directory.CreateDirectory(outputDirectory);

            // This contains what we see in the game window.
            // Monitor it closely to ensure it is running at a smooth framerate.
            visibleStack = new ScreenStack
            {
                Size = DrawSize
            };

            // Everything inside here will be captured (or "screenshotted") into images.
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
            LoadComponent(visibleStack);
            visibleStack.Push(new MainScreen());
            captureStack.Push(new CaptureMainScreen());

            // So we can see the pink screen in the game window.
            captureLayer.Add(visibleStack);

            // Our captureStack doesn't need to be in the game's scene graph,
            // but our DrawableScreenshotter does, otherwise it won't be triggered.

            // However, due to the fact that all drawables use their parents' clocks unless overridden,
            // our CaptureMainScreen will be updated in real time, when we actually want it to update
            // *independently* of real time, at our own explicit pace.
            // To solve this, we need to give our CaptureMainScreen (or our captureStack) a separate clock!

            // Re-use one screenshotter and trigger captures explicitly.
            captureScreenshotter = new DrawableScreenshotter(captureStack, onImageReceived, expireAfterCapture: false);
            captureLayer.Add(captureScreenshotter);
        }

        protected override void Update()
        {
            base.Update();

            if (completedFrames >= target_frames)
                return;

            if (currentlyCapturing || requestedFrames >= target_frames)
                return;

            captureStack.Size = DrawSize;
            captureStack.UpdateSubTree();
            captureStack.UpdateSubTreeMasking();

            currentlyCapturing = true;
            requestedFrames++;
            captureScreenshotter.RequestCapture();
        }

        private void onImageReceived(Image<Rgba32> image)
        {
            if (image != null)
            {
                var path = Path.Combine(outputDirectory, $"frame-{completedFrames:0000}.png");

                // This is a slow operation: offload to thread pool to not disrupt the draw thread.
                Task.Run(() => image.SaveAsPng(path));

                completedFrames++;
            }

            currentlyCapturing = false;

            if (completedFrames >= target_frames)
                host.Exit();
        }
    }
}
