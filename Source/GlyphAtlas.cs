// The two textures a GlyphTable's arenas mirror into.

using System;
#if !NFMW
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endif
#if NFMW
using System.Collections.Generic;
using NFMWorld.Graphics;

/// <summary>
/// One partial row upload that could not be made when it was produced, because no command buffer
/// was live. See <see cref="Apos.Shapes.GlyphAtlas.Sync"/>.
/// </summary>
/// <param name="Texture">The texture the rows belong to.</param>
/// <param name="Y">First row, in texels.</param>
/// <param name="Rows">How many rows.</param>
/// <param name="Data">The rows' bytes, already converted to the texture's format.</param>
internal readonly record struct GlyphUpload(ITexture Texture, int Y, int Rows, byte[] Data);
#endif

namespace Apos.Shapes {
    // Brings the band and curve textures up to date with a table's arenas. Everything above
    // this is device free, so a bake can be checked without a graphics device at all; this is
    // the only part that needs one.
    //
    // Both textures are RGBA32F. The band texture only carries two values per texel, but a
    // WebGL1 context that has OES_texture_float at all has it for RGBA, and not always for two
    // channel floats, so the pair rides in the first two channels of a four channel texel.
    //
    // KNI's GL family cannot upload a float texture through the browser at all, so there it
    // repacks to RGBA8 instead. See GlyphRepack for the encodings and apos-shapes.fx for the
    // matching decode.
    internal sealed class GlyphAtlas : IDisposable {
#if NFMW
        internal ITexture? Band;
        internal ITexture? Curve;

        /// <param name="device">The device to build the textures with.</param>
        /// <param name="cb">
        /// The frame's command buffer. Partial-row uploads go through it rather than the device
        /// because <see cref="ITexture"/> has no update entry point of its own.
        /// </param>
        /// <param name="table">The baked glyph table to mirror.</param>
        /// <param name="pending">
        /// Collects rows written while no command buffer was live, for the next frame to send.
        /// </param>
        internal void Upload(IGraphicsDevice device, ICommandBuffer? cb, GlyphTable table, List<GlyphUpload> pending) {
            Band = Sync(device, cb, Band, table.Band, pending);
            Curve = Sync(device, cb, Curve, table.Curve, pending);
        }
#else
        internal Texture2D? Band;
        internal Texture2D? Curve;

        internal void Upload(GraphicsDevice graphicsDevice, GlyphTable table) {
#if KNI
            if (Repacks(graphicsDevice)) {
                Band = SyncBand8(graphicsDevice, Band, table.Band);
                Curve = SyncCurve8(graphicsDevice, Curve, table.Curve);
                return;
            }
#endif
            Band = Sync(graphicsDevice, Band, table.Band);
            Curve = Sync(graphicsDevice, Curve, table.Curve);
        }
#endif

#if NFMW
        // Growth rebuilds the texture and refills it whole, which keeps every already seated
        // texel index valid. Otherwise only the rows written since the last upload go up.
        //
        // An arena holds its texels as floats, and the texture wants them as bytes, so the two
        // differ in width. Growths convert and send the whole thing; the incremental rows
        // convert just their own span out of the middle of the arena.
        //
        // The bytes are built here rather than pointed at, because a command buffer does not
        // keep the span it is handed. A settled working set uploads nothing, so the allocation
        // only happens on the flushes that actually move rows.
        private static ITexture? Sync(IGraphicsDevice device, ICommandBuffer? cb, ITexture? texture, TexelArena arena, List<GlyphUpload> pending) {
            if (arena.Rows == 0) return texture;
            if (texture == null || arena.Grew) {
                texture?.Dispose();
                texture = device.CreateTexture(new TextureDesc(arena.Width, arena.Rows, TextureFormat.Rgba32f));
            }
            if (arena.DirtyTo > arena.DirtyFrom) {
                // A texture takes whole rows, so a write that starts or ends mid row rounds out
                // to the rows it touched.
                int y = arena.DirtyFrom / arena.Width;
                int rows = (arena.DirtyTo + arena.Width - 1) / arena.Width - y;
                int floats = rows * arena.Width * 4;
                byte[] bytes = new byte[floats * sizeof(float)];
                Buffer.BlockCopy(arena.Data, y * arena.Width * 4 * sizeof(float), bytes, 0, floats * sizeof(float));
                if (cb != null) {
                    cb.UpdateTexture(texture, 0, y, arena.Width, rows, bytes);
                } else {
                    // Rasterized outside a frame. The rows have to be kept as a copy, because
                    // the arena goes on marking itself uploaded and will not offer them again.
                    pending.Add(new GlyphUpload(texture, y, rows, bytes));
                }
            }
            arena.Uploaded();
            return texture;
        }
#endif

#if !NFMW
        // Growth rebuilds the texture and refills it whole, which keeps every already seated
        // texel index valid. Otherwise only the rows written since the last upload go up.
        private static Texture2D? Sync(GraphicsDevice graphicsDevice, Texture2D? texture, TexelArena arena) {
            if (arena.Rows == 0) return texture;
            if (texture == null || arena.Grew) {
                texture?.Dispose();
                texture = new Texture2D(graphicsDevice, arena.Width, arena.Rows, false, SurfaceFormat.Vector4);
            }
            if (arena.DirtyTo > arena.DirtyFrom) {
                // A texture takes whole rows, so a write that starts or ends mid row rounds out
                // to the rows it touched.
                int y = arena.DirtyFrom / arena.Width;
                int rows = (arena.DirtyTo + arena.Width - 1) / arena.Width - y;
                texture.SetData(0, new Rectangle(0, y, arena.Width, rows), arena.Data,
                                y * arena.Width * 4, rows * arena.Width * 4);
            }
            arena.Uploaded();
            return texture;
        }

#if KNI
        // The DirectX backends of KNI load the standard mgfx shader and take a float texture the
        // same way the MonoGame ones do, so only the GL family repacks. This matches the pick in
        // ShapeBatch.LoadEmbeddedEffect: a custom effect compiled for a different backend than
        // the device is running would disagree with it, the same way it already disagrees about
        // which bytecode to load.
        private static bool Repacks(GraphicsDevice graphicsDevice) {
            GraphicsBackend backend = graphicsDevice.Adapter.Backend;
            return backend != GraphicsBackend.DirectX11 && backend != GraphicsBackend.DirectX12;
        }

        // Scratch for the rows going up. The interop hands WebGL a view over the whole managed
        // array and ignores both the start index and the element count it was given, so the array
        // has to hold exactly the rectangle's bytes and nothing else. It is kept between uploads
        // and only replaced when the size changes, which for a working set that has settled is
        // never, since a run with no new glyphs uploads nothing at all.
        private byte[] _bandBytes = Array.Empty<byte>();
        private byte[] _curveBytes = Array.Empty<byte>();

        private static byte[] Fit(ref byte[] scratch, int bytes) {
            if (scratch.Length != bytes) scratch = new byte[bytes];
            return scratch;
        }

        private Texture2D? SyncBand8(GraphicsDevice graphicsDevice, Texture2D? texture, TexelArena arena) {
            if (arena.Rows == 0) return texture;
            if (texture == null || arena.Grew) {
                texture?.Dispose();
                texture = new Texture2D(graphicsDevice, arena.Width, arena.Rows, false, SurfaceFormat.Color);
            }
            if (arena.DirtyTo > arena.DirtyFrom) {
                int y = arena.DirtyFrom / arena.Width;
                int rows = (arena.DirtyTo + arena.Width - 1) / arena.Width - y;
                byte[] bytes = Fit(ref _bandBytes, rows * arena.Width * 4);
                GlyphRepack.EncodeBandRows(arena.Data, arena.Width, y, rows, bytes);
                texture.SetData(0, new Rectangle(0, y, arena.Width, rows), bytes, 0, bytes.Length);
            }
            arena.Uploaded();
            return texture;
        }

        // Twice the rows of the arena: the low bytes of a logical texel's four values sit in row
        // 2y and the high bytes in row 2y + 1.
        private Texture2D? SyncCurve8(GraphicsDevice graphicsDevice, Texture2D? texture, TexelArena arena) {
            if (arena.Rows == 0) return texture;
            if (texture == null || arena.Grew) {
                texture?.Dispose();
                texture = new Texture2D(graphicsDevice, arena.Width, arena.Rows * 2, false, SurfaceFormat.Color);
            }
            if (arena.DirtyTo > arena.DirtyFrom) {
                int y = arena.DirtyFrom / arena.Width;
                int rows = (arena.DirtyTo + arena.Width - 1) / arena.Width - y;
                byte[] bytes = Fit(ref _curveBytes, rows * arena.Width * 8);
                GlyphRepack.EncodeCurveRows(arena.Data, arena.Width, y, rows, bytes);
                texture.SetData(0, new Rectangle(0, y * 2, arena.Width, rows * 2), bytes, 0, bytes.Length);
            }
            arena.Uploaded();
            return texture;
        }
#endif
#endif

        public void Dispose() {
            Band?.Dispose();
            Curve?.Dispose();
            Band = null;
            Curve = null;
        }
    }
}
