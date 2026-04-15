// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Allocation;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Shaders;
using osuTK;
using osuTK.Graphics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace osu.Framework.Graphics.Visualisation
{
    /// <summary>
    /// Takes an image of a drawable.
    /// </summary>
    public partial class DrawableScreenshotter : Drawable, IBufferedDrawable
    {
        public readonly Drawable Target;

        private readonly Action<Image<Rgba32>?> onImageReceived;
        private readonly bool expireAfterCapture;
        public Action? OnExtractBegin, OnExtractEnd;

        private bool captureRequested;
        private bool didRender;
        private long captureVersion;

        public DrawableScreenshotter(Drawable target, Action<Image<Rgba32>?> onImageReceived)
            : this(target, onImageReceived, expireAfterCapture: true)
        {
        }

        public DrawableScreenshotter(Drawable target, Action<Image<Rgba32>?> onImageReceived, bool expireAfterCapture)
        {
            this.onImageReceived = onImageReceived;
            this.expireAfterCapture = expireAfterCapture;

            Target = target;

            captureRequested = expireAfterCapture;
        }

        /// <summary>
        /// Requests a capture on a future draw pass.
        /// </summary>
        public void RequestCapture()
        {
            captureVersion++;
            captureRequested = true;
        }

        public override Quad ScreenSpaceDrawQuad => Target.ScreenSpaceDrawQuad;

        private IShader textureShader = null!;

        [BackgroundDependencyLoader]
        private void load(ShaderManager shaders)
        {
            textureShader = shaders.Load(VertexShaderDescriptor.TEXTURE_2, FragmentShaderDescriptor.TEXTURE);
        }

        public override bool IsPresent => true;

        IShader ITexturedShaderDrawable.TextureShader => textureShader;
        Color4 IBufferedDrawable.BackgroundColour => new Color4(0, 0, 0, 0);
        DrawColourInfo? IBufferedDrawable.FrameBufferDrawColour => new DrawColourInfo(Color4.White);
        Vector2 IBufferedDrawable.FrameBufferScale => Vector2.One;

        public override DrawColourInfo DrawColourInfo => new DrawColourInfo(Color4.White);

        public override DrawInfo DrawInfo => Target.DrawInfo;

        private readonly BufferedDrawNodeSharedData sharedData = new BufferedDrawNodeSharedData(new[] { RenderBufferFormat.D16 }, pixelSnapping: true, clipToRootNode: true);

        [Resolved]
        private IRenderer renderer { get; set; } = null!;

        private void onRendered(IFrameBuffer frameBuffer)
        {
            if (didRender)
                return;

            didRender = true;

            // off-screen-capture: This used to be behind 2 scheduled tasks,
            // and I understand why, when this was solely being used for single screenshots.
            // But I need minimal delay between the framebuffer and the encoder.

            OnExtractBegin?.Invoke();
            var image = renderer.ExtractFrameBufferData(frameBuffer);
            OnExtractEnd?.Invoke();

            onImageReceived(image);

            captureRequested = false;
            didRender = false;

            if (expireAfterCapture)
                Expire();
        }

        internal override DrawNode? GenerateDrawNodeSubtree(ulong frame, int treeIndex, bool forceNewDrawNode)
        {
            if (!captureRequested || didRender)
                return null;

            var targetDrawNode = Target.GenerateDrawNodeSubtree(frame, treeIndex, forceNewDrawNode);

            if (targetDrawNode == null)
            {
                onImageReceived(null);

                captureRequested = false;

                if (expireAfterCapture)
                    Expire();

                return null;
            }

            // This looks a bit odd, but we essentially want a drawNode that we can safely dispose once we've rendered it.
            // This call will force the target drawable to recreate its drawNode subtree so the one we got should be completely detached.
            // off-screen-capture: This costs 3ms on my machine, and commenting it out causes no harm. Free savings!
            // Target.GenerateDrawNodeSubtree(frame, treeIndex, forceNewDrawNode: true);

            var drawNode = new DrawableScreenshotterDrawNode(this, targetDrawNode, sharedData, onRendered, captureVersion);

            drawNode.ApplyState();

            return drawNode;
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            sharedData.Dispose();
        }

        private class DrawableScreenshotterDrawNode : BufferedDrawNode
        {
            private readonly Action<IFrameBuffer> onRendered;
            private readonly long captureVersion;

            public DrawableScreenshotterDrawNode(IBufferedDrawable source, DrawNode child, BufferedDrawNodeSharedData sharedData, Action<IFrameBuffer> onRendered, long captureVersion)
                : base(source, child, sharedData)
            {
                this.onRendered = onRendered;
                this.captureVersion = captureVersion;
            }

            protected override long GetDrawVersion() => captureVersion;
            protected override void DrawContents(IRenderer renderer) => onRendered(SharedData.MainBuffer);
        }
    }
}
