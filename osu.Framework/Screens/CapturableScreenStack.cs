// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Shaders;
using osuTK;
using osuTK.Graphics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace osu.Framework.Screens
{
    public partial class CapturableScreenStack : ScreenStack, IBufferedDrawable
    {
        public Action? OnExtractBegin = null, OnExtractEnd = null;
        public Action<Image<Rgba32>?>? OnImageReceived = null;
        private readonly BufferedDrawNodeSharedData sharedData = new([RenderBufferFormat.D16], pixelSnapping: true, clipToRootNode: true);
        private IShader textureShader = null!;

        private bool captureRequested;
        private long captureVersion;

        /// <summary>
        /// Requests a framebuffer capture on the next draw pass.
        /// </summary>
        public void RequestCapture()
        {
            captureRequested = true;
            ++captureVersion;
        }

        [BackgroundDependencyLoader]
        private void load(ShaderManager shaders)
        {
            textureShader = shaders.Load(VertexShaderDescriptor.TEXTURE_2, FragmentShaderDescriptor.TEXTURE);
        }

        // IBufferedDrawable implementation
        IShader ITexturedShaderDrawable.TextureShader => textureShader;
        Color4 IBufferedDrawable.BackgroundColour => Color4.Transparent;
        DrawColourInfo? IBufferedDrawable.FrameBufferDrawColour => new(Color4.White);
        Vector2 IBufferedDrawable.FrameBufferScale => Vector2.One;

        [Resolved]
        private IRenderer renderer { get; set; } = null!;

        private void onFrameBufferReady(IFrameBuffer frameBuffer)
        {
            if (!captureRequested)
                return;
            OnExtractBegin?.Invoke();
            var image = renderer.ExtractFrameBufferData(frameBuffer);
            OnExtractEnd?.Invoke();
            OnImageReceived?.Invoke(image);
            captureRequested = false;
        }

        internal override DrawNode GenerateDrawNodeSubtree(ulong frame, int treeIndex, bool forceNewDrawNode)
        {
            var child = base.GenerateDrawNodeSubtree(frame, treeIndex, forceNewDrawNode);

            if (!captureRequested)
                return child;

            var node = new CaptureDrawNode(this, child, sharedData, onFrameBufferReady, captureVersion);
            node.ApplyState();
            return node;
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);
            sharedData.Dispose();
        }

        private class CaptureDrawNode : BufferedDrawNode
        {
            private readonly Action<IFrameBuffer> onFrameBufferReady;
            private long captureVersion;

            public CaptureDrawNode(
                IBufferedDrawable source,
                DrawNode child,
                BufferedDrawNodeSharedData sharedData,
                Action<IFrameBuffer> onFrameBufferReady,
                long captureVersion)
                : base(source, child, sharedData)
            {
                this.onFrameBufferReady = onFrameBufferReady;
                this.captureVersion = captureVersion;
            }

            protected override long GetDrawVersion() => captureVersion;

            protected override void DrawContents(IRenderer renderer)
            {
                // First notify that framebuffer is ready for capture
                onFrameBufferReady?.Invoke(SharedData.MainBuffer);

                // Then draw framebuffer to screen (normal behavior)
                base.DrawContents(renderer);
            }
        }
    }
}