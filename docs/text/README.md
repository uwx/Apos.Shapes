# Text
This guide will show you how to draw text with the `ShapeBatch`.

Text comes out of the font's own outlines. Each glyph is baked into a distance field on first use, and the pixel shader reads the distance to the outline out of it, so a pixel gets one signed number rather than a coverage. Outline, glow and shadow are then arithmetic on that number rather than geometry behind it, which is what makes them cheap enough to put on every frame. There's still no charset to declare. Text goes into the same batch as the shapes and never breaks it.

## Load a font

Put a `.ttf` file in your Content folder and make sure it gets copied to the output directory. In your game's `LoadContent()`, read it into a `ShapeFont`:

```csharp
protected override void LoadContent() {
    _sb = new ShapeBatch(GraphicsDevice);

    using var ttf = TitleContainer.OpenStream($"{Content.RootDirectory}/my-font.ttf");
    _font = new ShapeFont(ttf);
}

ShapeBatch _sb;
ShapeFont _font;
```

There's a `byte[]` overload too. Reading the file is the expensive part, so load a font once and keep it. One `ShapeFont` can back any number of batches at the same time, and everything on it is safe to call from any thread. It's `IDisposable`, and a font you never dispose holds onto its file bytes for the life of the process.

A font this can't draw throws at load. If the bytes come from somewhere you don't control, `ShapeFont.TryLoad` asks instead of throwing:

```csharp
if (!ShapeFont.TryLoad(bytes, out ShapeFont? font)) {
    // Not a TrueType font this can read.
}
```

## Draw text

`DrawString` goes between `Begin` and `End` like any other draw call:

```csharp
protected override void Draw(GameTime gameTime) {
    GraphicsDevice.Clear(Color.Black);

    _sb.Begin();
    _sb.FillCircle(new Vector2(120, 120), 75, new Color(96, 165, 250));
    _sb.DrawString(_font, "Hello!", new Vector2(100, 100), 24f, Color.White);
    _sb.End();

    base.Draw(gameTime);
}
```

![Text drawn on top of a circle](text.png)

Shapes and text are drawn in the order you call them, and it's still one draw call. The fill is a `Gradient`, and a `Color` converts to one, so `Color.White` there is a gradient with one color in it. There's a `ReadOnlySpan<char>` overload for text you'd rather not build a string for.

Every glyph is read out of the file the first time something draws it. You never declare a character set, and the ones you stop drawing get recycled to make room for the ones you start. A cold glyph costs a fraction of a millisecond to bake, which is free for text that dribbles in but is paid all at once for a screen of text nothing has drawn before. `Prewarm` moves that cost to a load screen or the moment a panel is built:

```csharp
_font.Prewarm("Fps: 0.00  Frame (CPU): 0.00ms");
```

The size and colors the text will be drawn at make no difference to it; a field is baked once per glyph, not per draw, and code points the font has no glyph for are skipped.

## The size is an em in world units

`size` is the em size, in the same world units the shapes use. Every size reads the same baked field, so nothing has to pick one up front:

```csharp
float x = 16f;
float baseline = 43f;
foreach (float size in new[] { 9f, 13f, 19f, 27f, 38f }) {
    _sb.DrawString(_font, "Shapes", new Vector2(x, baseline - _font.Ascent * size), size, Color.White);
    x += _font.MeasureString("Shapes", size).X + 14f;
}
```

![The word Shapes at five rising sizes sitting on one baseline](sizes.png)

Since the size is in world units, zooming the view in draws the letters bigger rather than scaling a bitmap. The field was baked at 48 texels per em, which is at or above one texel per screen pixel for every size the example above draws, so the field is sampled rather than magnified. Zoom in far past the size you drew at and the edge softens, the way any distance field does.

## Where the text lands

`position` is the top left corner of the first line, not the baseline. That's also the corner of the box `MeasureString` hands back, so you can measure a string and place it without having drawn it first:

```csharp
const string text = "The position is\nthe top left corner.";
var at = new Vector2(21f, 21f);

_sb.DrawString(_font, text, at, 20f, Color.White);
_sb.BorderRectangle(at, _font.MeasureString(text, 20f), new Color(96, 165, 250), 1f);
```

![Two lines of text with the rectangle MeasureString returns drawn around them](measure.png)

The box is the longest line's advance wide and the line count times `LineHeight` tall, so a one line string is one line tall whatever letters are in it. A `'\n'` starts a new line that far down, back at the left edge. A `'\r'` is skipped.

The metrics behind that are on the font, in em units, so multiply by the size you draw at: `Ascent` reaches above the baseline, `Descent` below it and is negative, `LineGap` is the extra room the font asks for between lines, and `LineHeight` is the three of them together. `Advance(codePoint)` and `Kerning(left, right)` give the same numbers the layout uses.

## Rotation and origin

`rotation` turns a whole line at once, around `position`. `origin` moves the point it turns around, in world units out from the top left corner, so half of `MeasureString` turns the text around its middle:

```csharp
Vector2 half = _font.MeasureString("turn", 22f) * 0.5f;
for (int i = 0; i < 8; i++) {
    _sb.DrawString(_font, "turn", new Vector2(50f + i * 66f, 50f), 22f, Color.White, MathF.Tau * i / 8f, half);
}
```

![The word turn at eight angles around a full circle](rotation.png)

A turned line costs one sine and one cosine for the whole string, so it's the same price as a straight one. `aaSize` is there too, the same anti-aliasing width in screen pixels that every shape takes.

## The fill is a gradient

Text takes a `Gradient` where a shape takes one. Two stops sweep across the string:

```csharp
var at = new Vector2(16, 16);
float wide = _font.MeasureString("Gradient", 40f).X;
_sb.DrawString(_font, "Gradient", at, 40f, new Gradient(
    at, new Color(96, 165, 250),
    at + new Vector2(wide, 0f), new Color(220, 38, 38)));
```

![The word Gradient sweeping from blue to red](gradient.png)

The gradient is resolved once for the whole string rather than once per glyph, so the colors run across the line instead of starting over inside every letter. Everything in the [Gradients](../gradients/README.md) guide works here: the gradient shapes, the repeat styles, the offsets, the color spaces, palettes, ramps, and color ramps.

```csharp
var sunset = new ColorRamp(
    (0f, new Color(251, 191, 36)),
    (0.45f, new Color(236, 72, 153)),
    (0.7f, new Color(147, 51, 234)),
    (0.7f, new Color(45, 212, 191)),
    (1f, new Color(8, 145, 178)));

var at = new Vector2(16, 16);
float wide = _font.MeasureString("a color ramp", 40f).X;
_sb.DrawString(_font, "a color ramp", at, 40f, new Gradient(
    at, at + new Vector2(wide, 0f), sunset));
```

![The words a color ramp running through amber, pink, purple and teal](text-ramp.png)

A local gradient reads its two points in the line's own box, the one `MeasureString` hands back, y down from `position`, and it turns with the text. A world one stays where it is while the text moves through it.

Alpha lives in the fill's colors, so translucent text is a fill whose colors are translucent. `new Color(255, 255, 255, 128)` draws a half transparent white line, and two stops with different alphas fade a line out along its length.

## Outlined text

An overload takes an outline color and a width, and strokes each glyph behind its fill:

```csharp
_sb.DrawString(_font, "Outline", at, 40f,
    new Color(255, 255, 255),   // fill
    new Color(17, 24, 39),      // outline
    4f);                        // width in world units
```

The band sits outside the glyph, so the two colors meet at the glyph's own edge with no seam and no gap. The outline grows the letter by the full width you asked for, on every side, so a measured box no longer covers the result exactly.

The band follows the glyph's own curves, because it is the field's distance shifted outward rather than a second set of geometry behind the fill. It stays smooth at any size, and it costs almost nothing: an outlined line builds in about the same time as a plain one, where stroking real geometry behind the glyph would cost many times the fill.

The outline takes a `Gradient` as well as a `Color`, and it resolves across the whole string the same way the fill does, so a gradient on the outline runs from the first letter to the last. A width of zero, or a negative one, draws no outline and the outline color goes unread.

## Glow

The same overload takes a glow after the width. It is a world space Gaussian on the same band the outline is, so it replaces the anti-aliasing width rather than adding to it, the way it does on every other shape:

```csharp
_sb.DrawString(_font, "Glow", at, 40f,
    new Color(255, 255, 255),   // fill
    new Color(96, 165, 250),    // outline
    2f,                         // width in world units
    6f);                        // glow radius
```

A glow of zero leaves the plain anti-aliased edge and draws no glow at all. Past about a third of the field's range the glow runs out of distance to read and flattens into a square edge, so a very large one should be built from the blur guide's `DrawString` alternative or a smaller radius.

## Limits

Only TrueType outlines work. Most `.otf` files describe their glyphs with cubic curves in a `CFF` table instead, and this solver is quadratic only, so loading one throws a `NotSupportedException`. `TryLoad` is how you check without a `try`.

A newline is the only layout this does. There's no wrapping, no alignment, and no ellipsis, since those are decisions and this draws what it's handed. There's no fallback font chain either. A code point the font has no glyph for draws the font's own missing glyph box, so a hole shows up instead of quietly closing. You pick the font per call, so a fallback is a loop you write.

A glyph's field is baked at 48 texels per em, so text drawn much past that size is magnified and its edge softens. Corners are where a distance field is least exact, and a stroke much thinner than the field's range - a stem a couple of texels wide at a small size - is the classic case a multi-channel field can round away. Both are handled at the sizes games draw text at; a display that magnifies letters heavily wants a larger em.

A glyph is baked once per font and shared by every batch and every draw of it, and the batch keeps a fixed size atlas that recycles the fields you stop drawing, the same way it recycles the curves. A glyph larger than the atlas on its own cannot be placed at all.

## Coming from FontStashSharp

The FontStashSharp API is gone and the dependency with it. A `FontSystem` plus `GetFont(size)` is one `ShapeFont` and a `size` argument on the call, and character spacing, line spacing, `TextStyle`, and `FontSystemEffect` have no replacement.

## Follow up

[SVG](../svg/README.md), a guide that shows how to draw SVG files with the `ShapeBatch`. A drawing is solved from its own outlines the way a glyph is.
