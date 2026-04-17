// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using osu.Framework.Allocation;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Screens;
using osuTK;
using osuTK.Graphics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace osu.Framework.Graphics.Visualisation
{
    public partial class ScreenStackScreenshotter : Drawable, IBufferedDrawable
    {
        private static readonly MethodInfo target_method = AccessTools.Method(typeof(CompositeDrawable), "GenerateDrawNodeSubtree")
            ?? throw new InvalidOperationException("CompositeDrawable.GenerateDrawNodeSubtree not found");

        public required ScreenStack Target;

        public Action? OnExtractBegin = null, OnExtractEnd = null;
        public Action<Image<Rgba32>?>? OnImageReceived = null;

        private readonly BufferedDrawNodeSharedData sharedData = new([RenderBufferFormat.D16], pixelSnapping: true, clipToRootNode: true);
        private IShader textureShader = null!;

        private bool captureRequested;
        private long captureVersion;
        private readonly Harmony harmony;

        public ScreenStackScreenshotter()
        {
            harmony = new Harmony($"{this}#{GetHashCode()}");
            harmony.Patch(
                original: target_method,
                postfix: typeof(GenerateDrawNodeSubtreeInjector).GetMethod("Postfix")
            );
        }

        /// <summary>
        /// Requests a framebuffer capture on the next draw pass.
        /// </summary>
        public void RequestCapture()
        {
            captureRequested = true;
            GenerateDrawNodeSubtreeInjector.Screenshotter = this;
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

        // Proxied properties
        public override Quad ScreenSpaceDrawQuad => Target.ScreenSpaceDrawQuad;

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
            GenerateDrawNodeSubtreeInjector.Screenshotter = null;
        }

        // Instead of us redrawing the entirety of Target,
        // let's use Lib.Harmony to inject **our** draw node into the Target's GenerateDrawNodeSubtree!
        [HarmonyPatch(typeof(CompositeDrawable), "GenerateDrawNodeSubtree")]
        private class GenerateDrawNodeSubtreeInjector
        {
            public static ScreenStackScreenshotter? Screenshotter = null;

            public static void Postfix(ref DrawNode __result, CompositeDrawable __instance)
            {
                if (Screenshotter == null || !Screenshotter.captureRequested || __instance != Screenshotter.Target)
                    return;
                __result = new CaptureDrawNode(Screenshotter, __result, Screenshotter.sharedData, Screenshotter.onFrameBufferReady, Screenshotter.captureVersion);
                __result.ApplyState();
            }
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);
            sharedData.Dispose();
            harmony.Unpatch(target_method, HarmonyPatchType.Prefix);
        }

        private class CaptureDrawNode(
            IBufferedDrawable source,
            DrawNode child,
            BufferedDrawNodeSharedData sharedData,
            Action<IFrameBuffer> onFrameBufferReady,
            long captureVersion)
            : BufferedDrawNode(source, child, sharedData)
        {
            private readonly Action<IFrameBuffer> onFrameBufferReady = onFrameBufferReady;
            private long captureVersion = captureVersion;

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