// The one texture an MsdfTable's shelf mirrors into.

using System;
#if !NFMW
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endif
#if NFMW
using System.Collections.Generic;
using NFMWorld.Graphics;

/// <summary>
/// One row run of a distance field atlas that could not be uploaded when it was produced, because
/// no command buffer was live. See <see cref="Apos.Shapes.MsdfAtlas.Sync"/>.
/// </summary>
/// <param name="Texture">The texture the rows belong to.</param>
/// <param name="Y">First row, in texels.</param>
/// <param name="Rows">How many rows.</param>
/// <param name="Data">The rows' bytes.</param>
internal readonly record struct MsdfUpload(ITexture Texture, int Y, int Rows, byte[] Data);
#endif

namespace Apos.Shapes {
    // Brings the distance field texture up to date with a table's shelf. Everything above this is
    // device free, so a bake can be checked without a graphics device at all; this is the only
    // part that needs one.
    //
    // Unlike the band and curve textures this is RGBA8, which is the same width as the field
    // itself and needs no repack: KNI's GL family cannot upload a float texture at all, and a
    // byte atlas is the shape every backend already takes. It is also half the memory of the
    // float pair for the same glyph set.
    internal sealed class MsdfAtlas : IDisposable {
#if NFMW
        internal ITexture? Texture;

        /// <param name="device">The device to build the texture with.</param>
        /// <param name="cb">
        /// The frame's command buffer. Partial-row uploads go through it rather than the device
        /// because <see cref="ITexture"/> has no update entry point of its own.
        /// </param>
        /// <param name="table">The baked field table to mirror.</param>
        /// <param name="pending">
        /// Collects rows written while no command buffer was live, for the next frame to send.
        /// </param>
        internal void Upload(IGraphicsDevice device, ICommandBuffer? cb, MsdfTable table, List<MsdfUpload> pending) {
            Texture = Sync(device, cb, Texture, table.Shelves, pending);
        }

        // The shelf never grows, so the texture is only ever built once and the incremental rows
        // are all that ever move. The bytes are built here rather than pointed at, because a
        // command buffer does not keep the span it is handed.
        private static ITexture? Sync(IGraphicsDevice device, ICommandBuffer? cb, ITexture? texture, MsdfShelf shelf, List<MsdfUpload> pending) {
            if (shelf.DirtyTo <= shelf.DirtyFrom) {
                shelf.Uploaded();
                return texture;
            }
            texture ??= device.CreateTexture(new TextureDesc(shelf.Width, shelf.MaxRows, TextureFormat.Rgba8));

            int y = shelf.DirtyFrom;
            int rows = shelf.DirtyTo - y;
            byte[] bytes = new byte[rows * shelf.Width * 4];
            Buffer.BlockCopy(shelf.Data, y * shelf.Width * 4, bytes, 0, bytes.Length);
            if (cb != null) {
                cb.UpdateTexture(texture, 0, y, shelf.Width, rows, bytes);
            } else {
                // Rasterized outside a frame. The rows have to be kept as a copy, because the
                // shelf goes on marking itself uploaded and will not offer them again.
                pending.Add(new MsdfUpload(texture, y, rows, bytes));
            }
            shelf.Uploaded();
            return texture;
        }
#else
        internal Texture2D? Texture;

        internal void Upload(GraphicsDevice graphicsDevice, MsdfTable table) {
            Texture = Sync(graphicsDevice, Texture, table.Shelves);
        }

        private static Texture2D? Sync(GraphicsDevice graphicsDevice, Texture2D? texture, MsdfShelf shelf) {
            if (shelf.DirtyTo <= shelf.DirtyFrom) {
                shelf.Uploaded();
                return texture;
            }
            // Colour is the one byte RGBA format every backend maps to an uploadable texture, and
            // it is the format the field is packed in.
            texture ??= new Texture2D(graphicsDevice, shelf.Width, shelf.MaxRows, false, SurfaceFormat.Color);

            int y = shelf.DirtyFrom;
            int rows = shelf.DirtyTo - y;
            texture.SetData(0, new Rectangle(0, y, shelf.Width, rows), shelf.Data,
                            y * shelf.Width * 4, rows * shelf.Width * 4);
            shelf.Uploaded();
            return texture;
        }
#endif

        public void Dispose() {
            Texture?.Dispose();
            Texture = null;
        }
    }
}
