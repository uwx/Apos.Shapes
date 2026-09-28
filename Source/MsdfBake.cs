// Bakes a glyph outline into a multi-channel signed distance field.
//
// This is the text path's replacement for the Slug solve. Slug computes true coverage from the
// curves at draw time, which is exact but leaves no number the shader can shift for an outline or
// a glow. A distance field costs an atlas and a fixed bake resolution, and pays for that with
// outline, glow and shadow, all of which become arithmetic on the one distance.
//
// Derived from msdfgen (MIT, Viktor Chlumsky) by way of Remora.MSDFGen (MIT, Jarl Gullberg).
// See THIRD_PARTY_NOTICES.md.

using System;
#if !NFMW
using Microsoft.Xna.Framework;
#endif
// Remora's geometry is System.Numerics, which is only the same type as ours on the NFMW target;
// upstream ours comes from XNA. Every crossing into Remora converts explicitly.
using SysVec2 = System.Numerics.Vector2;

namespace Apos.Shapes {
    // A glyph baked into one RGBA8 distance field, plus what a quad and a line of text need to
    // place it. Pure CPU data: nothing here touches a graphics device, and an MsdfTable is what
    // turns it into texels at a fixed place in the atlas.
    //
    // RGB is the multi-channel field, whose median is the distance, and A is the true
    // single-channel distance. The median's reconstruction error is largest at a corner, and a
    // Gaussian is the one thing that amplifies it, so a blur wants the true distance instead.
    //
    // Distances are encoded the msdfgen way, channel = sd / range + 0.5, with sd in em units.
    internal sealed class MsdfGlyph {
        internal MsdfGlyph(int glyph) {
            Glyph = glyph;
        }

        // The font's own glyph index, which is what kerning and the registry key on.
        internal readonly int Glyph;

        // Horizontal advance and left side bearing in design units, straight off the font.
        internal int Advance;
        internal int Bearing;

        // The outline's box in em units, and the same box grown by the bake's padding. The
        // padded box is what the quad covers, so it is what the renderer places.
        internal Vector2 Min;
        internal Vector2 Max;
        internal Vector2 PaddedMin;
        internal Vector2 PaddedMax;

        // Texels per em this was baked at, and how far in texels the field can represent either
        // side of the outline.
        internal float EmPx;
        internal float RangeTexels;

        // RGBA8, Width * Height texels, row 0 at the top.
        internal byte[] Rgba = Array.Empty<byte>();
        internal int Width;
        internal int Height;

        // False for a glyph with no outline to draw: a space, a code point the font has no glyph
        // for, or an outline this baker cannot express. The advance is still set.
        internal bool HasField => Width > 0;

        internal int TexelCount => Width * Height;
    }

    internal static class MsdfBake {
        // Texels per em. The game draws text at 16 to 48 px, so this is 1 to 3x magnification and
        // the field is sampled rather than reconstructed. That is the direction a distance field
        // handles gracefully; the artefact it trades for is corners rounded by up to half the
        // range, which is why the range below wants a corner-aware bake rather than a wider one.
        internal const int EmPx = 48;

        // How far, in texels, the field can represent either side of the outline. Driven by
        // outlines rather than by text quality: a fill's own edge wants about two texels, but an
        // outline of width w needs the distance unsaturated out to w, and a glow out to its
        // reach. The game strokes text at 12 to 48 px em with an outline width of 2 world units,
        // which is 0.167 em at a 12 px font and 0.042 em at 48 - so 16 texels at 48 per em holds
        // a third of an em, and covers the worst case with the AA band on top of it. A byte
        // channel this wide still quantizes to 3.3e-4 em against a reconstruction error of
        // 1.4e-3, so the range costs no accuracy the field had.
        internal const float RangeTexels = 16f;

        // Bakes one glyph. `outline` is the em-space contour GlyphBake retains, in contour order
        // and never mutated after the bake, so this can run outside the font's lock.
        internal static MsdfGlyph Bake(
            int glyph, int advance, int bearing, Vector2 min, Vector2 max,
            GlyphCurve[] outline, int[] contourStarts, int emPx = EmPx, float rangeTexels = RangeTexels) {

            var g = new MsdfGlyph(glyph) {
                Advance = advance,
                Bearing = bearing,
                Min = min,
                Max = max,
                EmPx = emPx,
                RangeTexels = rangeTexels,
            };
            if (outline.Length == 0 || contourStarts.Length == 0) return g;

            Remora.MSDFGen.Shape? shape = BuildShape(outline, contourStarts);
            if (shape == null) return g;

            // Half the range either side of the outline, plus a gutter texel: a quad corner
            // samples the exact rect corner, and bilinear reaches half a texel past it.
            float padTexels = MathF.Ceiling(rangeTexels * 0.5f) + 1f;
            int w = (int)MathF.Ceiling((max.X - min.X) * emPx) + (int)padTexels * 2;
            int h = (int)MathF.Ceiling((max.Y - min.Y) * emPx) + (int)padTexels * 2;
            if (w < 2 || h < 2) return g;

            g.PaddedMin = new Vector2(min.X - padTexels / emPx, min.Y - padTexels / emPx);
            g.PaddedMax = new Vector2(g.PaddedMin.X + w / (float)emPx, g.PaddedMin.Y + h / (float)emPx);

            var scale = new SysVec2(emPx, emPx);
            var translate = new SysVec2(padTexels / emPx - min.X, padTexels / emPx - min.Y);
            // Remora 2.0.0's EvaluateMSDF adds its half texel in shape units and divides the
            // translate by scale again, where msdfgen does (x + 0.5) / scale - translate. Both
            // are affine, so this adjusted translate cancels them exactly and puts the two
            // fields on one sample grid. The SDF path is correct and takes the plain translate.
            var msdfTranslate = new SysVec2(
                scale.X * translate.X + 0.5f * (scale.X - 1f),
                scale.Y * translate.Y + 0.5f * (scale.Y - 1f));
            // `range` is in shape units, not texels: the encoding is sd / range and sd is in
            // shape units because the sample point is. At the scale 1 of Remora's own tests the
            // two coincide, which is what hides it.
            double shapeRange = rangeTexels / emPx;

            Remora.MSDFGen.MSDF.EdgeColoringSimple(shape, 3.0 * Math.PI / 180.0, 0);

            var msdf = new Remora.MSDFGen.Graphics.Pixmap<Remora.MSDFGen.Graphics.Color3>(w, h);
            var sdf = new Remora.MSDFGen.Graphics.Pixmap<float>(w, h);
            Remora.MSDFGen.MSDF.GenerateMSDF(msdf, shape, shapeRange, scale, msdfTranslate);
            Remora.MSDFGen.SDF.GenerateSDF(sdf, shape, shapeRange, scale, translate);

            // Remora has no MTSDF entry point, so the pair is assembled here. CorrectErrors reads
            // RGB only, which is what lets alpha carry the untouched true distance.
            int texels = w * h;
            var packed = new Remora.MSDFGen.Graphics.Pixmap<Remora.MSDFGen.Graphics.Color4>(w, h);
            for (int i = 0; i < texels; i++) {
                Remora.MSDFGen.Graphics.Color3 c = msdf.Data[i];
                packed.Data[i] = new Remora.MSDFGen.Graphics.Color4 {
                    R = c.R, G = c.G, B = c.B, A = sdf.Data[i],
                };
            }
            // msdfgen's default of minDeviationRatio (10/9) over the range: the expected delta
            // between neighbouring encoded texels.
            float threshold = 1.11111111111111111f / rangeTexels;
            Remora.MSDFGen.MSDF.CorrectErrors(
                packed, new System.Drawing.Rectangle(0, 0, w, h), new SysVec2(threshold, threshold));

            var rgba = new byte[texels * 4];
            for (int i = 0; i < texels; i++) {
                Remora.MSDFGen.Graphics.Color4 c = packed.Data[i];
                rgba[i * 4] = Byte(c.R);
                rgba[i * 4 + 1] = Byte(c.G);
                rgba[i * 4 + 2] = Byte(c.B);
                rgba[i * 4 + 3] = Byte(c.A);
            }
            g.Rgba = rgba;
            g.Width = w;
            g.Height = h;
            return g;
        }

        // The outline as a Remora shape, in em units and y up. Null when there is no closed loop
        // with edges to solve against.
        private static Remora.MSDFGen.Shape? BuildShape(GlyphCurve[] outline, int[] contourStarts) {
            var shape = new Remora.MSDFGen.Shape();
            for (int c = 0; c < contourStarts.Length; c++) {
                int start = contourStarts[c];
                int end = c + 1 < contourStarts.Length ? contourStarts[c + 1] : outline.Length;
                if (end <= start) continue;
                var contour = new Remora.MSDFGen.Contour();
                for (int i = start; i < end; i++) {
                    GlyphCurve gc = outline[i];
                    // GlyphBake has already pulled a control point that sat on either end out to
                    // the midpoint, so the quadratic is well conditioned before it reaches here.
                    contour.Edges.Add(new Remora.MSDFGen.QuadraticSegment(
                        new SysVec2(gc.P1.X, gc.P1.Y),
                        new SysVec2(gc.P2.X, gc.P2.Y),
                        new SysVec2(gc.P3.X, gc.P3.Y),
                        Remora.MSDFGen.EdgeColor.White));
                }
                if (contour.Edges.Count > 0) shape.Contours.Add(contour);
            }
            if (shape.Contours.Count == 0) return null;
            OrientContours(shape);
            shape.InverseYAxis = false;
            // Remora's Normalize only splits single-edge contours. The orientation above is the
            // part msdfgen's Shape::normalize would have done for the winding rule.
            shape.Normalize();
            return shape;
        }

        // A counter is punched out only when it runs the opposite way to the loop enclosing it,
        // and Remora's Contour.Winding is read only, so the direction is chosen here. Alternating
        // by how many loops contain this one is what gets an outline inside a hole filled again.
        private static void OrientContours(Remora.MSDFGen.Shape shape) {
            int count = shape.Contours.Count;
            var depth = new int[count];
            for (int i = 0; i < count; i++) {
                SysVec2 probe = shape.Contours[i].Edges[0].GetPoint(0.37);
                for (int j = 0; j < count; j++) {
                    if (i != j && Contains(shape.Contours[j], probe)) depth[i]++;
                }
            }
            for (int i = 0; i < count; i++) {
                int want = (depth[i] & 1) == 0 ? 1 : -1;
                if (shape.Contours[i].Winding != want) shape.Contours[i].Edges.Reverse();
            }
        }

        private static bool Contains(Remora.MSDFGen.Contour contour, SysVec2 p) {
            int crossings = 0;
            const int steps = 24;
            foreach (Remora.MSDFGen.EdgeSegment edge in contour.Edges) {
                SysVec2 prev = edge.GetPoint(0);
                for (int s = 1; s <= steps; s++) {
                    SysVec2 cur = edge.GetPoint(s / (double)steps);
                    if ((prev.Y <= p.Y) != (cur.Y <= p.Y)) {
                        double xc = prev.X + (p.Y - prev.Y) / (cur.Y - prev.Y) * (cur.X - prev.X);
                        if (xc > p.X) crossings++;
                    }
                    prev = cur;
                }
            }
            return (crossings & 1) == 1;
        }

        private static byte Byte(float v) {
            int i = (int)(v * 255f + 0.5f);
            return (byte)(i < 0 ? 0 : i > 255 ? 255 : i);
        }
    }
}
