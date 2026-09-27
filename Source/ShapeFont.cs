using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
#if !NFMW
using Microsoft.Xna.Framework;
#endif

namespace Apos.Shapes {
    /// <summary>
    /// A font a <see cref="ShapeBatch"/> draws text with. The outlines come straight out of the
    /// file and the pixel shader solves the coverage of every curve, so a glyph is exact at any
    /// size. There's no atlas behind it, so nothing has to pick a size up front and nothing goes
    /// blurry when you zoom in.
    ///
    /// A glyph is read out of the file the first time something draws it, so every code point the
    /// font has an outline for works without being asked for in advance. Reading the file is the
    /// expensive part, so load a font once and keep it. One font can back any number of batches at
    /// the same time.
    ///
    /// Only TrueType outlines work. Most .otf files describe their glyphs with cubic curves in a
    /// CFF table instead, and this solver is quadratic only, so loading one throws.
    /// <see cref="TryLoad(byte[], out ShapeFont)"/> asks instead of throwing.
    ///
    /// A font only has the code points its file has. A code point it is missing draws the
    /// font's own missing glyph, which is usually a box, so
    /// <see cref="AddFallback(ShapeFont)"/> hands over another font to try instead. The fonts do
    /// not have to agree on units per em: a glyph is rescaled into this font's em as it is
    /// drawn, so every font in the chain lays out in the same one.
    ///
    /// Metrics are in em units: multiply by the size you draw at to get world units. Everything
    /// here is safe to call from any thread.
    /// </summary>
    public sealed class ShapeFont : IDisposable {
        /// <summary>Loads a font from the bytes of a .ttf file.</summary>
        /// <param name="ttf">The whole file.</param>
        /// <exception cref="ArgumentNullException"><paramref name="ttf"/> is null.</exception>
        /// <exception cref="ArgumentException">The bytes aren't a font this can read.</exception>
        /// <exception cref="NotSupportedException">The font's outlines are cubic, which this can't draw.</exception>
        public ShapeFont(byte[] ttf) {
            ArgumentNullException.ThrowIfNull(ttf);
            _font = new GlyphFont(ttf);
            if (!_font.Quadratic) {
                _font.Dispose();
                throw new NotSupportedException(
                    "This font keeps its outlines in a CFF table, which is cubic. Only TrueType outlines can be drawn.");
            }
            float em = _font.UnitsPerEm;
            UnitsPerEm = _font.UnitsPerEm;
            Ascent = _font.Ascent / em;
            Descent = _font.Descent / em;
            LineGap = _font.LineGap / em;
            LineHeight = (_font.Ascent - _font.Descent + _font.LineGap) / em;
        }
        /// <summary>Loads a font by reading a stream to its end. The stream stays open.</summary>
        /// <param name="ttf">A stream over a whole .ttf file.</param>
        /// <exception cref="ArgumentNullException"><paramref name="ttf"/> is null.</exception>
        /// <exception cref="ArgumentException">The bytes aren't a font this can read.</exception>
        /// <exception cref="NotSupportedException">The font's outlines are cubic, which this can't draw.</exception>
        public ShapeFont(Stream ttf) : this(ReadAll(ttf)) { }

        /// <summary>
        /// Loads a font and hands back whether it worked, for when a font comes from somewhere you
        /// don't control. Nothing throws.
        /// </summary>
        /// <param name="ttf">The whole file.</param>
        /// <param name="font">The loaded font, or null when this returns false.</param>
        /// <returns>False when the bytes aren't a readable TrueType font.</returns>
        public static bool TryLoad(byte[] ttf, [NotNullWhen(true)] out ShapeFont? font) {
            font = null;
            if (ttf == null || ttf.Length == 0) return false;
            try {
                font = new ShapeFont(ttf);
                return true;
            } catch (ArgumentException) {
                return false;
            } catch (NotSupportedException) {
                return false;
            }
        }
        /// <summary>Loads a font from a stream and hands back whether it worked. See the byte[] overload.</summary>
        /// <param name="ttf">A stream over a whole .ttf file.</param>
        /// <param name="font">The loaded font, or null when this returns false.</param>
        /// <returns>False when the stream can't be read, or isn't a readable TrueType font.</returns>
        public static bool TryLoad(Stream ttf, [NotNullWhen(true)] out ShapeFont? font) {
            font = null;
            if (ttf == null) return false;
            byte[] bytes;
            try {
                bytes = ReadAll(ttf);
            } catch (IOException) {
                return false;
            }
            return TryLoad(bytes, out font);
        }

        /// <summary>Design units per em, straight off the font. Only useful next to a font's own numbers.</summary>
        public int UnitsPerEm { get; }
        /// <summary>How far the font reaches above the baseline, in em units. Positive.</summary>
        public float Ascent { get; }
        /// <summary>How far the font reaches below the baseline, in em units. Negative.</summary>
        public float Descent { get; }
        /// <summary>Extra room the font asks for between two lines, in em units.</summary>
        public float LineGap { get; }
        /// <summary>
        /// Baseline to baseline distance in em units, which is <see cref="Ascent"/> minus
        /// <see cref="Descent"/> plus <see cref="LineGap"/>. A newline moves the pen down by this
        /// times the size drawn at.
        /// </summary>
        public float LineHeight { get; }

        /// <summary>
        /// How far the pen moves after drawing a code point, in em units. A code point the font has
        /// no glyph for measures the missing glyph, which is what draws in its place.
        /// </summary>
        /// <param name="codePoint">A Unicode code point, not a UTF-16 char.</param>
        public float Advance(int codePoint) {
            return Resolve(codePoint, out float scale, out _).Advance * scale / (float)UnitsPerEm;
        }

        /// <summary>
        /// Extra advance the font asks for between two code points, in em units, on top of the left
        /// one's own. Negative when the pair tightens up, which is the usual direction. Zero for
        /// most pairs, and always zero for a font with no kerning table.
        /// </summary>
        /// <param name="left">The code point on the left.</param>
        /// <param name="right">The code point on the right.</param>
        public float Kerning(int left, int right) {
            // Kerning is a table of glyph index pairs, and an index only means anything inside
            // the font it came from. A pair with a fallback in it is therefore left unkerned:
            // the primary's table would be read at an index that is a different glyph there.
            BakedGlyph l = Resolve(left, out _, out GlyphFont? lf);
            BakedGlyph r = Resolve(right, out _, out GlyphFont? rf);
            if (!ReferenceEquals(lf, _font) || !ReferenceEquals(rf, _font)) return 0f;
            return _font.Kerning(l.Glyph, r.Glyph) / (float)UnitsPerEm;
        }

        // The glyph a code point draws as, and which font it comes from. The primary is asked
        // first, then each fallback in turn, and the first font with a glyph wins. When nothing
        // has one the primary's missing glyph is the answer, so the text still gets the advance
        // and the box a font that has never heard of the code point would give it.
        //
        // The scale is the fallback's design units per primary unit, so a glyph baked out of
        // another font lands in the same em the text is being laid out in.
        internal BakedGlyph Resolve(int codePoint, out float scale, out GlyphFont? owner) {
            if (_font.LookupOrNull(codePoint) is { } primary) {
                scale = 1f;
                owner = _font;
                return primary;
            }
            for (int i = 0; i < _fallbacks.Count; i++) {
                GlyphFont font = _fallbacks[i]._font;
                if (font.LookupOrNull(codePoint) is { } glyph) {
                    scale = (float)UnitsPerEm / font.UnitsPerEm;
                    owner = font;
                    return glyph;
                }
            }
            scale = 1f;
            owner = _font;
            return _font.Glyph(0);
        }

        /// <summary>
        /// The box <see cref="ShapeBatch.DrawString(ShapeFont, string, Vector2, float, Gradient, float, Vector2, float)"/>
        /// fills, in world units, with its top left corner at the position the text is drawn at.
        /// The width is the longest line's advance and the height is the line count times
        /// <see cref="LineHeight"/>, so a one line string is one line tall whatever letters are in
        /// it. Empty text measures zero.
        /// </summary>
        /// <param name="text">The text to measure, with newlines splitting lines the same way.</param>
        /// <param name="size">Em size in world units, the same one the text is drawn at.</param>
        public Vector2 MeasureString(ReadOnlySpan<char> text, float size) {
            if (text.IsEmpty) return Vector2.Zero;

            float scale = size / UnitsPerEm;
            float widest = 0f;
            float x = 0f;
            int lines = 1;
            int prev = -1;
            GlyphFont? prevOwner = null;
            for (int i = 0; i < text.Length;) {
                int cp = CodePointAt(text, i, out int step);
                i += step;
                if (cp == '\r') continue;
                if (cp == '\n') {
                    if (x > widest) widest = x;
                    x = 0f;
                    lines++;
                    prev = -1;
                    prevOwner = null;
                    continue;
                }
                BakedGlyph g = Resolve(cp, out float glyphScale, out GlyphFont? owner);
                // Kerning only ever applies between two glyphs of the same font, for the reason
                // Kerning gives: an index outside the font it came from names a different glyph.
                if (prev >= 0 && ReferenceEquals(owner, prevOwner)) {
                    x += owner!.Kerning(prev, g.Glyph) * scale * glyphScale;
                }
                x += g.Advance * glyphScale * scale;
                prev = g.Glyph;
                prevOwner = owner;
            }
            if (x > widest) widest = x;
            return new Vector2(widest, lines * LineHeight * size);
        }
        /// <summary>Measures a string. See the span overload.</summary>
        /// <param name="text">The text to measure, with newlines splitting lines the same way.</param>
        /// <param name="size">Em size in world units, the same one the text is drawn at.</param>
        public Vector2 MeasureString(string text, float size) {
            return MeasureString(text.AsSpan(), size);
        }

        /// <summary>
        /// Adds a font to try after this one, for the code points this font has no glyph for.
        /// Fallbacks are tried in the order they were added, and one is only consulted once
        /// every font before it has been asked and come back with nothing.
        ///
        /// The fallback is not owned: this does not dispose it, so the same font can stand
        /// behind several others and has to outlive them.
        /// </summary>
        /// <param name="fallback">The font to try next.</param>
        /// <returns>This font, so calls can be chained.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="fallback"/> is null.</exception>
        public ShapeFont AddFallback(ShapeFont fallback) {
            ArgumentNullException.ThrowIfNull(fallback);
            // A font that cannot draw an outline would take the code point out of the hands of
            // one that can, so it is left out. A font that has no glyph for anything is the
            // same shape of mistake, and neither is worth throwing over.
            if (!fallback._font.Quadratic) return this;
            if (!ReferenceEquals(fallback, this)) _fallbacks.Add(fallback);
            return this;
        }

        /// <summary>Releases the font file this holds. Text drawn before this still draws.</summary>
        public void Dispose() {
            _font.Dispose();
        }

        // The font behind this, which is what the batch's glyph path draws from.
        internal GlyphFont Font => _font;

        // One code point out of UTF-16, and how many chars it took. A high surrogate with a low
        // one after it is a single code point; a lone surrogate is left as its own value, which no
        // font has a glyph for, so a broken pair draws one missing glyph rather than two.
        internal static int CodePointAt(ReadOnlySpan<char> text, int i, out int size) {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) {
                size = 2;
                return char.ConvertToUtf32(c, text[i + 1]);
            }
            size = 1;
            return c;
        }

        private static byte[] ReadAll(Stream ttf) {
            ArgumentNullException.ThrowIfNull(ttf);
            if (ttf is MemoryStream ms) return ms.ToArray();
            using var copy = new MemoryStream();
            ttf.CopyTo(copy);
            return copy.ToArray();
        }

        private readonly GlyphFont _font;
        // The fonts tried after this one, in order, when a code point has no glyph here. Held
        // rather than owned: a fallback is a font in its own right that someone else loads and
        // disposes, and the usual arrangement is one of them standing behind several primaries.
        private readonly List<ShapeFont> _fallbacks = new();
    }
}
