using System;
using System.Collections.Generic;
#if !NFMW
using Microsoft.Xna.Framework;
#endif

namespace Apos.Shapes {
    /// <summary>
    /// The canvas style commands a <see cref="ShapePath"/> is built from. Handed back by
    /// <see cref="ShapeBatch.ShapePath(FillRule)"/>, and the same object
    /// <see cref="ShapeBatch.BeginShapePath(FillRule)"/> starts so the <c>Shape*</c> command
    /// methods on the batch can drive it.
    ///
    /// Every command returns this, so a path reads as one chain; <see cref="Build"/> finishes it.
    /// </summary>
    public sealed class ShapePathBuilder {
        private readonly SvgOutline _outline;
        private readonly FillRule _fillRule;
        private readonly float _tolerance;
        // The subpath offsets MarkHole has claimed, keyed by where each one starts in the quad
        // list so Finish dropping an empty subpath cannot shuffle a claim onto the wrong one.
        private readonly List<int> _holes = new();
        private readonly List<Vector2> _areaScratch = new();
        private bool _holeNext;
        private bool _built;

        internal ShapePathBuilder(FillRule fillRule, float tolerance) {
            _fillRule = fillRule;
            _tolerance = tolerance;
            _outline = new SvgOutline(SvgMatrix.Identity, tolerance);
        }

        /// <summary>Starts a new subpath at <paramref name="p"/>. Anything still open is left as it is.</summary>
        public ShapePathBuilder MoveTo(Vector2 p) {
            _outline.MoveTo(p);
            if (_holeNext) {
                _holeNext = false;
                ClaimHole(_outline.OpenSubpath);
            }
            return this;
        }

        /// <summary>A straight run to <paramref name="p"/>.</summary>
        public ShapePathBuilder LineTo(Vector2 p) {
            _outline.LineTo(p);
            return this;
        }

        /// <summary>A quadratic Bezier from the current point to <paramref name="p"/>, bent toward <paramref name="c"/>.</summary>
        public ShapePathBuilder QuadTo(Vector2 c, Vector2 p) {
            _outline.QuadTo(c, p);
            return this;
        }

        /// <summary>A cubic Bezier from the current point to <paramref name="p"/>, with the tangents <paramref name="c1"/> and <paramref name="c2"/>.</summary>
        public ShapePathBuilder CubicTo(Vector2 c1, Vector2 c2, Vector2 p) {
            _outline.CubicTo(c1, c2, p);
            return this;
        }

        /// <summary>
        /// An elliptical arc from the current point to <paramref name="p"/>, in the form SVG
        /// writes one in: the two radii, the ellipse's rotation in degrees, and which of the four
        /// arcs through the two points is meant. A zero radius draws a straight line.
        /// </summary>
        /// <param name="p">Where the arc ends.</param>
        /// <param name="rx">The ellipse's x radius.</param>
        /// <param name="ry">The ellipse's y radius.</param>
        /// <param name="rotation">The ellipse's rotation in degrees.</param>
        /// <param name="largeArc">True to take the arc going the long way round.</param>
        /// <param name="sweep">True to sweep the way a clock's hands go.</param>
        public ShapePathBuilder ArcTo(Vector2 p, float rx, float ry, float rotation, bool largeArc, bool sweep) {
            _outline.ArcTo(rx, ry, rotation, largeArc, sweep, p);
            return this;
        }

        /// <summary>Closes the current subpath back to where it started.</summary>
        public ShapePathBuilder Close() {
            _outline.Close();
            return this;
        }

        /// <summary>
        /// Marks the subpath about to start as a hole: it is forced to wind the other way from
        /// the subpath before it, so under the default fill rule it punches through whatever it
        /// sits inside. Call it just before the <see cref="MoveTo"/> that starts the subpath.
        ///
        /// A subpath that is already wound the other way is left as it is, so this is safe to
        /// ask for twice, which is what a caller drawing a border as an outer ring plus an inner
        /// one wants: the inner ring is already reversed and stays that way.
        /// </summary>
        public ShapePathBuilder MarkHole() {
            if (_outline.OpenSubpath < 0) {
                // Nothing to mark yet. The next MoveTo picks the claim up.
                _holeNext = true;
                return this;
            }
            ClaimHole(_outline.OpenSubpath);
            return this;
        }

        private void ClaimHole(int subpath) {
            if (subpath < 0) return;
            int offset = _outline.Starts[subpath];
            if (!_holes.Contains(offset)) _holes.Add(offset);
        }

        /// <summary>
        /// Finishes the path. The builder is spent afterwards; the path it hands back is
        /// independent and can be drawn any number of times.
        /// </summary>
        public ShapePath Build() {
            if (_built) throw new InvalidOperationException("This path has already been built.");
            _built = true;
            _outline.Finish();
            // Finish drops the subpaths that drew nothing, so a hole is found by the quad it
            // starts at rather than by the index it had while it was open.
            for (int i = 0; i < _holes.Count; i++) ReverseIfNeeded(_holes[i]);
            return new ShapePath(_outline, _fillRule);
        }

        // Winding is normalized rather than blindly flipped: a subpath that already runs the
        // other way from the one before it is already a hole under a nonzero fill, so reversing
        // it again would turn it back into a solid.
        private void ReverseIfNeeded(int offset) {
            int subpath = -1;
            for (int i = 0; i < _outline.SubpathCount; i++) {
                if (_outline.Starts[i] == offset) {
                    subpath = i;
                    break;
                }
            }
            if (subpath <= 0) return;
            if (SignedArea(offset) * SignedArea(_outline.Starts[subpath - 1]) < 0f) return;
            int start = _outline.Starts[subpath];
            int count = _outline.Counts[subpath];
            for (int i = 0; i < count / 2; i++) Reverse(start + i, start + count - 1 - i);
            if ((count & 1) != 0) Reverse(start + count / 2, start + count / 2);
        }

        // The shoelace sum of a subpath's flattened outline. Its sign is the winding direction,
        // which is all this compares; the magnitude is deliberately left unhalved.
        private float SignedArea(int offset) {
            int subpath = -1;
            for (int i = 0; i < _outline.SubpathCount; i++) {
                if (_outline.Starts[i] == offset) {
                    subpath = i;
                    break;
                }
            }
            if (subpath < 0) return 0f;
            _areaScratch.Clear();
            _outline.Flatten(subpath, _areaScratch, _tolerance);
            float sum = 0f;
            for (int i = 0; i < _areaScratch.Count; i++) {
                Vector2 a = _areaScratch[i];
                Vector2 b = _areaScratch[(i + 1) % _areaScratch.Count];
                sum += a.X * b.Y - b.X * a.Y;
            }
            return sum;
        }

        private void Reverse(int a, int b) {
            SvgQuad x = _outline.Quads[a];
            SvgQuad y = _outline.Quads[b];
            _outline.Quads[a] = new SvgQuad { P1 = y.P3, P2 = y.P2, P3 = y.P1 };
            _outline.Quads[b] = new SvgQuad { P1 = x.P3, P2 = x.P2, P3 = x.P1 };
        }
    }
}
