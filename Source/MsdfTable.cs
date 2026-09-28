// Where baked distance fields live inside the MSDF texture, and what recycles the room when
// they stop being drawn.

using System;
using System.Collections.Generic;

namespace Apos.Shapes {
    // Shelves of packed rectangles over one fixed-width byte atlas. A shelf takes rectangles until
    // its row is used up, then the next shelf is started; a rectangle never crosses a shelf's
    // right edge, which the linear-index arena cannot promise and a bilinear fetch requires, since
    // the four texels a sample lands between have to be the rectangle's own.
    //
    // Unlike the Slug arenas there is no growth: a distance field's texels are never rebuilt in
    // place, so once every shelf is packed the table evicts instead.
    internal sealed class MsdfShelf {
        internal MsdfShelf(int width, int maxRows) {
            Width = width;
            MaxRows = maxRows;
            Data = new byte[width * maxRows * 4];
        }

        internal readonly int Width;
        internal readonly int MaxRows;
        internal readonly byte[] Data;

        // Rows written since the last upload, as a half open range. Empty when To is not past
        // From.
        internal int DirtyFrom = int.MaxValue;
        internal int DirtyTo;

        // A shelf is opened at this many rows, rounded up from the rectangle that opened it rather
        // than that rectangle's own height. Glyph heights differ by a row or two all through a
        // font, so a shelf sized to its first occupant would be closed to almost every other
        // glyph and a font's worth of near-identical heights would stack up one wasted shelf per
        // height. Rounding puts a group of similar heights on one shelf together.
        private const int ShelfRows = 32;

        // The shelves opened so far, in the order they were opened, each with the row it starts
        // on, how tall it is and how far along it the packer has got.
        private readonly List<(int Y, int Height, int X)> _shelves = new();

        // Rectangles given back, as (x, y, w, h). A freed run is handed out again only for an
        // exact size match, which is all a glyph cache needs and keeps the shelf layout simple.
        private readonly List<(int X, int Y, int W, int H)> _free = new();

        internal int Capacity => Width * MaxRows;

        // Room for a w by h rectangle, or (-1, -1) when there is none left. No padding is added
        // here: the padding is baked into the field's own texels, so the rect as allocated is
        // what the quad samples.
        internal (int X, int Y) Allocate(int w, int h) {
            if (w > Width || h > MaxRows) return (-1, -1);
            for (int i = 0; i < _free.Count; i++) {
                (int X, int Y, int W, int H) f = _free[i];
                if (f.W == w && f.H == h) {
                    _free.RemoveAt(i);
                    return (f.X, f.Y);
                }
            }
            // The first shelf this fits on, rather than the shelf being filled: a glyph wider than
            // the room left on the newest shelf belongs on an older one with a shorter run of
            // neighbours, and a glyph shorter than the newest shelf goes above it. Only opening a
            // shelf when nothing anywhere fits is what keeps a taller glyph from writing off the
            // horizontal room of every shelf before it.
            for (int i = 0; i < _shelves.Count; i++) {
                (int Y, int Height, int X) shelf = _shelves[i];
                if (h <= shelf.Height && shelf.X + w <= Width) {
                    _shelves[i] = (shelf.Y, shelf.Height, shelf.X + w);
                    return (shelf.X, shelf.Y);
                }
            }
            int height = Math.Min(MaxRows, (h + ShelfRows - 1) / ShelfRows * ShelfRows);
            int y = _shelves.Count == 0 ? 0 : _shelves[_shelves.Count - 1].Y + _shelves[_shelves.Count - 1].Height;
            if (y + height > MaxRows) return (-1, -1);
            _shelves.Add((y, height, w));
            return (0, y);
        }

        // Gives a rectangle back. Only an exact size match is reused, so a smaller glyph does not
        // fragment a larger hole into something the shelf layout cannot describe.
        internal void Free(int x, int y, int w, int h) {
            _free.Add((x, y, w, h));
        }

        internal void Mark(int y, int rows) {
            if (y < DirtyFrom) DirtyFrom = y;
            if (y + rows > DirtyTo) DirtyTo = y + rows;
        }

        internal void Uploaded() {
            DirtyFrom = int.MaxValue;
            DirtyTo = 0;
        }
    }

    // A batch's record of which distance fields are resident in its MSDF texture. Each ShapeBatch
    // owns its own, so batches never contend for room; the batch mirrors the shelf into its
    // texture at flush time.
    internal sealed class MsdfTable {
        internal MsdfTable() { }

        // 2048 is the widest a texture is guaranteed to be able to be everywhere the library
        // runs, so the atlas grows in rows rather than columns. A glyph's field is its own box
        // plus the range on every side, which at the bake resolution is 80 to 120 rows for an
        // ordinary letter and taller for a CJK one - so a few hundred rows is a couple of Latin
        // alphabets' worth once shelf rounding has grouped the heights, and the table evicts
        // rather than growing past it. 2048x2048 RGBA8 is 16 MB against the 3.2 MB the same
        // glyph set costs as band and curve floats, which is the price of the atlas.
        internal const int Width = 2048;
        internal const int MaxRows = 2048;

        internal readonly MsdfShelf Shelves = new(Width, MaxRows);

        // Where a seated glyph's field sits in the atlas, in texel units, plus how recently it
        // was asked for. W and H zero mean the glyph has no field to place.
        private sealed class Seat {
            internal int X, Y, W, H;
            internal long Stamp;
        }

        private readonly List<MsdfGlyph?> _glyphs = new();
        private readonly List<Seat> _seats = new();
        private readonly Dictionary<MsdfGlyph, int> _byGlyph = new();
        private readonly List<int> _vacant = new();
        private long _clock;
        private long _flushClock;

        internal int Count => _byGlyph.Count;

        // Everything packed so far is drawn, so every entry is free to recycle.
        internal void Flushed() => _flushClock = _clock;

        // The rect a seated glyph occupies, which is what a quad carries to sample it.
        internal (int X, int Y, int W, int H) Rect(int index) {
            Seat s = _seats[index];
            return (s.X, s.Y, s.W, s.H);
        }

        // The entry seating this glyph, stamped as about to pack; seats and recycles as needed.
        // Returns -1 when every resident glyph is pinned, which the batch's pre-pin turns into a
        // flush.
        internal int Resolve(MsdfGlyph glyph) {
            if (_byGlyph.TryGetValue(glyph, out int found)) {
                _seats[found].Stamp = ++_clock;
                return found;
            }
            int i = Add(glyph);
            if (i >= 0) _seats[i].Stamp = ++_clock;
            return i;
        }

        private int Add(MsdfGlyph glyph) {
            var seat = new Seat { W = glyph.Width, H = glyph.Height };
            if (glyph.HasField) {
                (int X, int Y) at = Shelves.Allocate(glyph.Width, glyph.Height);
                while (at.X < 0) {
                    if (!Evict()) return -1;
                    at = Shelves.Allocate(glyph.Width, glyph.Height);
                }
                seat.X = at.X;
                seat.Y = at.Y;
                CopyInto(glyph, seat);
            }

            int index;
            if (_vacant.Count > 0) {
                index = _vacant[_vacant.Count - 1];
                _vacant.RemoveAt(_vacant.Count - 1);
                _glyphs[index] = glyph;
                _seats[index] = seat;
            } else {
                index = _glyphs.Count;
                _glyphs.Add(glyph);
                _seats.Add(seat);
            }
            _byGlyph[glyph] = index;
            return index;
        }

        // Copies a field's bytes into the shelf at the place it just took.
        private void CopyInto(MsdfGlyph glyph, Seat seat) {
            byte[] dst = Shelves.Data;
            for (int row = 0; row < glyph.Height; row++) {
                int d = ((seat.Y + row) * Width + seat.X) * 4;
                Array.Copy(glyph.Rgba, row * glyph.Width * 4, dst, d, glyph.Width * 4);
            }
            Shelves.Mark(seat.Y, glyph.Height);
        }

        // Drops the resident glyph that has gone longest undrawn, giving its rect back. A pinned
        // glyph is one an undrawn quad still points at, so it is never a candidate.
        private bool Evict() {
            int victim = -1;
            long oldest = long.MaxValue;
            for (int i = 0; i < _glyphs.Count; i++) {
                if (_glyphs[i] == null) continue;
                if (_seats[i].Stamp <= _flushClock && _seats[i].Stamp < oldest) {
                    oldest = _seats[i].Stamp;
                    victim = i;
                }
            }
            if (victim < 0) return false;
            Seat s = _seats[victim];
            if (s.W > 0 && s.H > 0) Shelves.Free(s.X, s.Y, s.W, s.H);
            _byGlyph.Remove(_glyphs[victim]!);
            _glyphs[victim] = null;
            _vacant.Add(victim);
            return true;
        }
    }
}
