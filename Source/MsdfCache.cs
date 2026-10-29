// A font's baked distance fields, on disk.
//
// Baking costs milliseconds per glyph and the game warms ~220 of them per font at startup, which on
// the browser is most of the launch. A field is a pure function of the outline, the font's bytes and
// the two bake constants, so it is baked once at build time and loaded instead.
//
// A cache is only valid for the exact font bytes it was baked from, so every file carries a hash of
// them: a font that changed since the build misses, and the lazy per-glyph bake the runtime already
// had covers it. Nothing here can make the game draw the wrong glyph.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Apos.Shapes {
    // Keyed by the font's own glyph index, which is what GlyphFont's field cache is keyed by.
    internal static class MsdfCache {
        // Bump whenever a bake would produce different bytes for the same outline - a change to
        // MsdfBake's algorithm or to this layout. An older file then misses and is rebaked rather
        // than trusted.
        internal const int Format = 1;

        private const uint Magic = 0x4644534Du; // "MSDF", little endian

        // magic + format + font hash + payload hash. Named because the reader has to skip exactly
        // this many bytes to reach the deflated body - getting it wrong feeds the inflater a stream
        // starting mid-header, which surfaces as "UnsupportedCompression" rather than as anything
        // about an offset.
        private const int HeaderBytes = 4 + 4 + 8 + 4;

        // The payload deflates rather than being stored: a distance field is smooth and compresses
        // several times over, and the boot archive ships its entries stored, so this is the only
        // chance to shrink it. Deflate is pure managed code, which is what makes it work on the
        // browser, where Brotli's native library has no wasm build.
        internal static byte[] Save(ulong fontHash, IReadOnlyList<MsdfGlyph> glyphs) {
            using var raw = new MemoryStream();
            using (var w = new BinaryWriter(raw, System.Text.Encoding.UTF8, leaveOpen: true)) {
                w.Write(glyphs.Count);
                foreach (MsdfGlyph g in glyphs) {
                    w.Write(g.Glyph);
                    w.Write(g.Advance);
                    w.Write(g.Bearing);
                    w.Write(g.Min.X); w.Write(g.Min.Y);
                    w.Write(g.Max.X); w.Write(g.Max.Y);
                    w.Write(g.PaddedMin.X); w.Write(g.PaddedMin.Y);
                    w.Write(g.PaddedMax.X); w.Write(g.PaddedMax.Y);
                    w.Write(g.EmPx);
                    w.Write(g.RangeTexels);
                    w.Write(g.Width);
                    w.Write(g.Height);
                    w.Write(g.Rgba.Length);
                    w.Write(g.Rgba);
                }
            }
            using var output = new MemoryStream();
            // A checksum over the payload, outside the compressed stream: raw deflate carries none,
            // so a damaged file can otherwise decode into bytes that are valid and wrong, which is
            // the one failure mode that would draw a mangled glyph instead of falling back to the
            // bake. Written uncompressed, ahead of the deflated body.
            using (var w = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true)) {
                w.Write(Magic);
                w.Write(Format);
                w.Write(fontHash);
                w.Write(Hash(raw.ToArray()));
            }
            raw.Position = 0;
            using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true)) {
                raw.CopyTo(deflate);
            }
            return output.ToArray();
        }

        // The glyphs the file holds, or null when it is missing, damaged, in another format, or was
        // baked from a different font. A miss is normal rather than exceptional: the runtime bakes
        // anything this does not cover.
        internal static List<MsdfGlyph>? Load(byte[] blob, ulong fontHash) {
            try {
                if (blob.Length < HeaderBytes) return null;
                using var header = new BinaryReader(new MemoryStream(blob, writable: false));
                if (header.ReadUInt32() != Magic) return null;
                if (header.ReadInt32() != Format) return null;
                if (header.ReadUInt64() != fontHash) return null;
                uint expected = header.ReadUInt32();

                using var input = new MemoryStream(blob, HeaderBytes, blob.Length - HeaderBytes, writable: false);
                using var deflate = new DeflateStream(input, CompressionMode.Decompress);
                using var deflateBytes = new MemoryStream();
                deflate.CopyTo(deflateBytes);
                deflateBytes.Position = 0;
                using var r = new BinaryReader(deflateBytes);

                int count = r.ReadInt32();
                var glyphs = new List<MsdfGlyph>(count);
                for (int i = 0; i < count; i++) {
                    var g = new MsdfGlyph(r.ReadInt32()) {
                        Advance = r.ReadInt32(),
                        Bearing = r.ReadInt32(),
                        Min = new Vector2(r.ReadSingle(), r.ReadSingle()),
                        Max = new Vector2(r.ReadSingle(), r.ReadSingle()),
                        PaddedMin = new Vector2(r.ReadSingle(), r.ReadSingle()),
                        PaddedMax = new Vector2(r.ReadSingle(), r.ReadSingle()),
                        EmPx = r.ReadSingle(),
                        RangeTexels = r.ReadSingle(),
                        Width = r.ReadInt32(),
                        Height = r.ReadInt32(),
                    };
                    g.Rgba = r.ReadBytes(r.ReadInt32());
                    glyphs.Add(g);
                }
                // A truncated payload can still decode a plausible-looking prefix, so the entry
                // count alone is not proof: hash every byte that came out and compare it to what
                // the header recorded. Raw deflate has no checksum of its own, which is why the
                // count and the hash are both here - either alone accepts a damaged file.
                return Hash(deflateBytes.ToArray()) == expected ? glyphs : null;
            } catch (Exception e) when (e is EndOfStreamException or IOException or InvalidDataException) {
                return null;
            }
        }

        // FNV-1a. Not cryptographic: one use notices that a font file was replaced, the other that a
        // cache file is damaged, and both run over bytes already in memory.
        private static uint Hash(byte[] bytes) {
            uint h = 2166136261u;
            foreach (byte b in bytes) {
                h ^= b;
                h *= 16777619u;
            }
            return h;
        }

        internal static ulong FontHash(byte[] bytes) {
            ulong h = 14695981039346656037UL;
            foreach (byte b in bytes) {
                h ^= b;
                h *= 1099511628211UL;
            }
            return h;
        }
    }
}
