// One place that turns an outline into the texels the Slug pixel loops read, shared by the SVG
// loader and the hand built path API so both bake the same way.

using System;
using System.Collections.Generic;
#if !NFMW
using Microsoft.Xna.Framework;
#endif

namespace Apos.Shapes {
    // An outline in document units goes to a pseudo design unit grid: divided by the em scale so
    // one em is that scale, y negated so it lands in the y up frame a glyph's does, moved by the
    // origin and multiplied by the grid.
    internal static class SvgBake {
        // Design units the baker is fed. Any grid works, since the baker divides by the same
        // number to get em units back; 2048 is what a TrueType font uses and keeps the integer
        // band box from quantizing anything visible.
        internal const int Grid = 2048;

        // How far out of the em square an outline's own control points may reach. The curve
        // texture holds them as fixed point over [-2, 2] on the KNI targets and the pad curve
        // every short band list is filled out with sits at -1.5, so an outline wider than this
        // is dropped rather than drawn wrong.
        internal const float EmReach = 1.2f;

        // Bakes an outline against the em scale it was measured in. Origin is where the design
        // frame's zero sits, in em units out from the outline's own coordinates: an SVG element
        // is centered on itself, a hand built path is not moved at all. Oversized comes back true
        // when the outline reaches past EmReach and nothing was baked, which the caller reports
        // as a dropped element rather than an empty one.
        internal static BakedGlyph? Bake(SvgOutline o, int index, float u, Vector2 originEm, out bool oversized) {
            oversized = false;
            float k = Grid / u;
            float ox = originEm.X * Grid;
            float oy = originEm.Y * Grid;
            Vector2 Design(Vector2 q) => new(q.X * k - ox, oy - q.Y * k);

            var curves = new List<GlyphCurve>();
            float lo = float.MaxValue, hi = float.MinValue, lox = float.MaxValue, hix = float.MinValue;
            void Grow(Vector2 d) {
                if (d.X < lox) lox = d.X;
                if (d.X > hix) hix = d.X;
                if (d.Y < lo) lo = d.Y;
                if (d.Y > hi) hi = d.Y;
            }

            for (int s = 0; s < o.SubpathCount; s++) {
                int start = o.Starts[s];
                int count = o.Counts[s];
                if (count == 0) continue;
                Vector2 first = default, last = default;
                for (int i = 0; i < count; i++) {
                    SvgQuad q = o.Quads[start + i];
                    Vector2 p1 = Design(q.P1);
                    Vector2 p2 = Design(q.P2);
                    Vector2 p3 = Design(q.P3);
                    if (i == 0) first = p1;
                    last = p3;
                    Grow(p1);
                    Grow(p2);
                    Grow(p3);
                    curves.Add(new GlyphCurve { P1 = p1, P2 = p2, P3 = p3 });
                }
                // Every subpath is closed for filling, whether or not a Z said so.
                if (last != first) {
                    curves.Add(new GlyphCurve { P1 = last, P2 = (last + first) * 0.5f, P3 = first });
                }
            }
            if (curves.Count == 0) return null;

            float reach = Grid * EmReach;
            if (!(lox >= -reach) || !(hix <= reach) || !(lo >= -reach) || !(hi <= reach)) {
                oversized = true;
                return null;
            }

            int x1 = (int)MathF.Floor(lox);
            int y1 = (int)MathF.Floor(lo);
            int x2 = (int)MathF.Ceiling(hix);
            int y2 = (int)MathF.Ceiling(hi);
            return GlyphBake.Bake(curves, index, 0, 0, x1, y1, x2, y2, Grid, GlyphBake.MaxCurves);
        }
    }
}
