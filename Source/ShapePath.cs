using System;
using System.Collections.Generic;
#if !NFMW
using Microsoft.Xna.Framework;
#endif

namespace Apos.Shapes {
    /// <summary>
    /// How a filled shape decides which side of an outline is inside.
    /// </summary>
    public enum FillRule {
        /// <summary>A point is inside when a ray from it crosses the outline more times one way than the other. Overlapping subpaths of the same winding fill solid.</summary>
        NonZero = 0,
        /// <summary>A point is inside when a ray from it crosses the outline an odd number of times. Every other overlap is punched out, which is what a hole looks like.</summary>
        EvenOdd = 1,
    }

    /// <summary>
    /// A path built from canvas style commands — move, line, quadratic and cubic Beziers and
    /// elliptical arcs — for a <see cref="ShapeBatch"/> to fill or stroke.
    ///
    /// Build one through the batch (<c>sb.ShapePath()</c>, or <c>BeginShapePath()</c> and the
    /// <c>Shape*</c> command methods), draw it as many times as you like, and hand it to
    /// <see cref="ShapeBatch.FillShape(ShapePath, Gradient, float)"/>,
    /// <see cref="ShapeBatch.StrokeShape(ShapePath, Gradient, float, PathJoin, PathCap, float, DashStyle, float)"/>
    /// or <see cref="ShapeBatch.DrawShape(ShapePath, Vector2, Gradient, Gradient, float, float, Vector2, float, float)"/>.
    ///
    /// Coordinates are y down, the same way a document and the world are, so a path a hundred
    /// units tall grows down from its first point. The batch places that first point where you
    /// ask it to.
    ///
    /// A path is immutable once built and safe to draw from more than one batch.
    /// </summary>
    public sealed class ShapePath {
        // The outline is in path coordinates: the identity transform means the points go in as
        // they were written. It is finished the moment a batch builds it, so no command ever runs
        // after the geometry is measured.
        private readonly SvgOutline _outline;
        private readonly FillRule _fillRule;

        // The bake and the em scale it was measured in, made on first draw. One bake serves every
        // size, which is the same tradeoff a drawing makes.
        private bool _baked;
        private BakedGlyph? _glyph;
        private float _em = 1f;

        internal ShapePath(SvgOutline outline, FillRule fillRule) {
            _outline = outline;
            _fillRule = fillRule;
        }

        /// <summary>The rule this path's fill is solved with.</summary>
        public FillRule FillRule => _fillRule;

        /// <summary>The geometry, for the batch's draw path.</summary>
        internal SvgOutline Outline => _outline;

        /// <summary>Whether the path has any segment to draw. A path of loose move-tos has none.</summary>
        public bool IsEmpty => _outline.SubpathCount == 0;

        /// <summary>
        /// The box the path covers, in path units, or null for an empty path.
        /// </summary>
        public RectangleF? Bounds {
            get {
                if (_outline.SubpathCount == 0) return null;
                _outline.DocBox(out Vector2 min, out Vector2 max);
                return new RectangleF(min.X, min.Y, max.X - min.X, max.Y - min.Y);
            }
        }

        // The bake is measured so the whole path fits the design grid: the coordinate furthest
        // from the origin is one em, so no point can trip the baker's reach test and be dropped.
        // Anchoring to the path's own origin rather than to its bounding box is what keeps a
        // drawn `position` meaning where local (0,0) goes.
        internal BakedGlyph? Bake() {
            if (_baked) return _glyph;
            _baked = true;
            if (_outline.SubpathCount == 0) return null;

            _outline.DocBox(out Vector2 min, out Vector2 max);
            _em = MathF.Max(MathF.Max(MathF.Abs(min.X), MathF.Abs(min.Y)),
                            MathF.Max(MathF.Abs(max.X), MathF.Abs(max.Y)));
            if (!(_em > 1e-6f)) _em = 1e-6f;
            _glyph = SvgBake.Bake(_outline, 0, _em, Vector2.Zero, out _);
            return _glyph;
        }

        // The em scale the bake was measured in. Only meaningful once Bake has run.
        internal float Em => _em;

        // Whether each subpath was closed, which decides whether its stroke takes caps.
        internal IReadOnlyList<bool> Closed => _outline.Closed;
    }
}
