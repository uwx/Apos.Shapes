// The math types the shape code is written against.
//
// Upstream these come from what a `using Microsoft.Xna.Framework;` in each file brings in: XNA
// re-exports System.Numerics' Vector2/3/4, and MonoGame.Extended puts RectangleF alongside them.
// Rendering through NFMWorld.Graphics leaves the XNA assembly behind, so the names are imported
// here instead and the call sites are untouched.
#if NFMW
global using System.Numerics;
global using Color = Maxine.Extensions.Mathematics.Color;
global using Rectangle = Maxine.Extensions.Mathematics.Rectangle;
global using RectangleF = Maxine.Extensions.Mathematics.RectangleF;
global using Matrix = System.Numerics.Matrix4x4;
// The Draw overloads take a texture, and upstream that is XNA's type. Its NFMWorld.Graphics
// counterpart is ITexture, which carries the Width/Height the same overloads read.
global using Texture2D = NFMWorld.Graphics.ITexture;
// RectangleF.Position was renamed to TopLeft along the way, and only the NFMW tree has the newer
// spelling; see the Draw overloads that place a source rectangle.
global using Matrix3x2 = System.Numerics.Matrix3x2;
#else
// MonoGame.Extended and KNI.Extended declare both of these under the same namespace, so the one
// pair of aliases serves both upstream targets. Matrix3x2 is theirs rather than System.Numerics',
// which is what the Draw overloads were written against.
global using RectangleF = MonoGame.Extended.RectangleF;
global using Matrix3x2 = MonoGame.Extended.Matrix3x2;
#endif
