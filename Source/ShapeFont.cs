using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
#if !NFMW
using Microsoft.Xna.Framework;
#endif

namespace Apos.Shapes {
    /// <summary>
    /// A font a <see cref="ShapeBatch"/> draws text with. Each glyph is baked into a distance field
    /// at a fixed resolution and placed in an atlas, so a pixel has one signed distance to the
    /// glyph's outline. Outline, glow and shadow are arithmetic on that one number, which is what
    /// makes them cheap; the trade is that the field is sampled rather than solved, so text is
    /// near-exact at the sizes games draw it and softens if magnified far past the bake resolution.
    /// <see cref="Prewarm(string)"/> bakes ahead so nothing pays for a cold string on the frame it
    /// first appears.
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

        // The distance field a code point draws from, resolved by the same rule as Resolve: the
        // primary is asked first, then each fallback, and only a code point no font has a glyph
        // for comes back null. A fallback's field is baked at its own em and folded in by the
        // same scale, so the metrics and the field always come from one font.
        internal MsdfGlyph? ResolveField(int codePoint, out float scale, out GlyphFont? owner) {
            if (_font.FieldLookupOrNull(codePoint) is { } primary) {
                scale = 1f;
                owner = _font;
                return primary;
            }
            for (int i = 0; i < _fallbacks.Count; i++) {
                GlyphFont font = _fallbacks[i]._font;
                if (font.FieldLookupOrNull(codePoint) is { } field) {
                    scale = (float)UnitsPerEm / font.UnitsPerEm;
                    owner = font;
                    return field;
                }
            }
            scale = 1f;
            owner = _font;
            return null;
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
        /// Bakes the distance field of every glyph in a string, so drawing it later doesn't.
        /// </summary>
        /// <remarks>
        /// A glyph's field is read in the first time something draws it, which for a cold glyph
        /// costs a fraction of a millisecond. That is free for text that dribbles in, but a whole
        /// screen of text nothing has drawn yet pays it all at once, on the frame it appears. Call
        /// this at a load screen, or when a panel is first built, to move the cost off that frame.
        ///
        /// The size and the colors the text will be drawn at make no difference: a field is baked
        /// once per glyph, not per draw. Code points the font has no glyph for are skipped.
        /// </remarks>
        /// <param name="text">The text to bake, with newlines treated as in the draw.</param>
        public void Prewarm(ReadOnlySpan<char> text) {
            for (int i = 0; i < text.Length;) {
                int codePoint = CodePointAt(text, i, out int step);
                i += step;
                _ = ResolveField(codePoint, out _, out _);
            }
        }
        /// <summary>Bakes the distance fields of every glyph in a string. See the span overload.</summary>
        /// <param name="text">The text to bake.</param>
        public void Prewarm(string text) {
            Prewarm(text.AsSpan());
        }

        /// <summary>
        /// Loads distance fields that were baked ahead of time, instead of baking them here.
        /// </summary>
        /// <remarks>
        /// Baking a glyph costs milliseconds, and warming a font's Latin-1 range is a couple of
        /// seconds on the browser, so the build bakes the fields once and ships them. Pass the file
        /// this font's cache was written from; it is ignored, whole, when it was baked from
        /// different font bytes, so a replaced font falls back to baking rather than drawing the
        /// previous version's glyphs.
        ///
        /// Anything the file does not cover still bakes on first draw exactly as before, so this is
        /// an accelerator and never a requirement. Call it before the font draws anything - the
        /// fields in it are only taken where none has been baked yet.
        /// </remarks>
        /// <param name="cache">The bytes of the cache file, or null when there is none.</param>
        /// <exception cref="ArgumentNullException"><paramref name="cache"/> is null.</exception>
        public void LoadPrewarmed(byte[] cache) {
            ArgumentNullException.ThrowIfNull(cache);
            // Matched on the font's own bytes rather than on the caller's word for which file it
            // is, so a cache can never be applied to a font it was not baked from.
            if (MsdfCache.Load(cache, _font.BytesHash) is not { } fields) return;
            _font.LoadFields(fields);
        }

        /// <summary>
        /// Writes this font's prewarmed fields as a cache file, for <see cref="LoadPrewarmed"/>.
        /// </summary>
        /// <remarks>
        /// Written by the build tool rather than by the game: which range a cache holds is a build
        /// decision, and a game that rewrote its own cache at runtime would be baking once per
        /// launch instead of once per build.
        /// </remarks>
        /// <returns>The file's bytes.</returns>
        public byte[] SavePrewarmed() => MsdfCache.Save(_font.BytesHash, _font.Fields);

        /// <summary>How many distance fields this font has baked or loaded so far.</summary>
        public int PrewarmedFieldCount => _font.Fields.Count;

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
