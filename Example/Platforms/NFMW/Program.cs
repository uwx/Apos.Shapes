// LLM maintained.
//
// The runnable Apos.Shapes example, on NFMWorld.Graphics instead of MonoGame/KNI.
//
// The shape work is the same spread the game example draws - fills, borders, a dashed arc, text,
// an SVG - so this is a smoke test of the port rather than a demo of the library. What it exercises
// that the FNA/MonoGame examples do not is the software path the port exists for: a device that
// takes ShaderStageSources rather than a precompiled .mgfx, an ICommandBuffer whose uniforms are
// byte offsets into a merged block, and the RGBA32F glyph atlas.
//
// Three things about running it are worth knowing up front:
//
//  1. The window is shown, because the whole point is to look at it. Visual correctness is the
//     reader's call, not an assertion here - nothing in this file reads pixels back.
//  2. It exits on its own after a fixed number of frames, so it can be run unattended, and on a
//     close or Escape before that.
//  3. The frame is the host's. ShapeBatch.Begin/End record a batch and submit it; the present and
//     the clear are this program's, which is the contract the NFMW port documents on Warmup.
//
// The one thing that needs care is the matrix convention. System.Numerics and XNA both keep
// translations in M41/M42/M43 (row vectors multiplied on the left), and every backend of the
// abstraction transposes a 4x4 uniform on the way into the constant block, because the shaders the
// compiler emits read them column-major. So the orthographic matrix built in Begin - and by
// extension the default projection here - is an XNA-style row-major matrix, and it is not converted
// by hand. Doing so would transpose twice and the scene would collapse to a sliver rather than
// merely move.
extern alias SDL3New;

using System.Runtime.InteropServices;
// The math the shape calls take. On this tree the library aliases these to Maxine's and
// System.Numerics' types internally (see Apos.Shapes/Source/GlobalUsings.cs), but those aliases are
// per-assembly globals and do not travel with the reference - a caller has to name them itself.
using System.Numerics;
using Color = Maxine.Extensions.Mathematics.Color;
using Apos.Shapes;
using NFMWorld.Graphics;
using NFMWorld.Graphics.DesktopGL;
using NFMWorld.Platform.SDL3;
using SDL3New::SDL3;

namespace Apos.Shapes.Example.NFMW;

internal static class Program
{
    private const int Width = 1280;
    private const int Height = 720;

    /// <summary>
    /// How many frames to draw before exiting. A window that never closes itself cannot be run
    /// unattended, and a shape renderer that is only ever watched by hand is hard to regress.
    /// </summary>
    private const int Frames = 60000;

    private static int Main()
    {
        using var host = new GlHost("Apos.Shapes on NFMWorld.Graphics", Width, Height);
        var device = host.Device;

        Console.WriteLine($"DEVICE: {host.Renderer}");
        Console.WriteLine($"DEVICE: swapchain {device.Swapchain.Width}x{device.Swapchain.Height}");
        Console.WriteLine();

        // The SVG is a document, not a file: reading one is a parse, and the parse is cheap next to
        // the baking, so it happens once and the result is drawn every frame.
        var svg = new ShapeSvg(IconSvg);

        using var font = new ShapeFont(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "source-code-pro-medium.ttf")));

        using var shapes = new ShapeBatch(device);

        for (var frame = 0; frame < Frames && !host.ShouldQuit; frame++)
        {
            host.PumpEvents();

            // One command buffer for the whole frame, the way the game this library is for records
            // one: the clear, the shapes and the present all belong to the same buffer, and the
            // batch is told to draw onto it rather than acquiring one of its own.
            var cb = device.AcquireCommandBuffer();
            // Null targets the swapchain, so this is written even though nothing was bound before:
            // it re-establishes the default framebuffer after the previous frame's present, and it
            // resets the command buffer's idea of the target height, which every viewport and
            // scissor flip is measured against.
            cb.SetRenderTarget(null);
            cb.SetViewport(new Viewport(0, 0, device.Swapchain.Width, device.Swapchain.Height));
            cb.Clear(ClearOptions.Color, new ColorRgba(0.06f, 0.09f, 0.16f));

            var t = frame / 60f;
            shapes.Begin(cb);
            DrawScene(shapes, svg, font, t);
            shapes.End();

            device.Submit(cb);
            device.Swapchain.Present();
        }

        Console.WriteLine("Done.");
        return 0;
    }

    private static void DrawScene(ShapeBatch sb, ShapeSvg svg, ShapeFont font, float t)
    {
        // Shapes, the way the game example lays them out: each family in its own row, with the
        // fill, the border and the dashed variant side by side.
        sb.FillCircle(new Vector2(120f, 110f), 60f, Color.SkyBlue);
        sb.BorderCircle(new Vector2(280f, 110f), 60f, Color.Orange, thickness: 6f);

        sb.FillRectangle(new Vector2(400f, 50f), new Vector2(120f, 120f), Color.MediumSeaGreen,
            cornerRadii: new CornerRadii(20f));
        sb.BorderRectangle(new Vector2(560f, 50f), new Vector2(120f, 120f), Color.Crimson, thickness: 6f,
            cornerRadii: new CornerRadii(20f));

        sb.FillHexagon(new Vector2(760f, 110f), 60f, Color.MediumPurple, rounded: 8f);
        sb.FillEquilateralTriangle(new Vector2(920f, 110f), 60f, Color.Gold, rounded: 6f);

        // A gradient whose two ends are not on the shape, so it runs across it rather than
        // restarting inside it.
        sb.FillEllipse(new Vector2(1080f, 110f), 70f, 45f, new Gradient(
            new Vector2(1010f, 110f), Color.DeepPink,
            new Vector2(1150f, 110f), Color.RoyalBlue), rotation: t);

        // A dashed arc, which is the shape that reads the ellipse-arc lookup table - the RGBA8
        // texture whose partial-row upload path is one of the things this port had to add.
        sb.BorderArc(new Vector2(120f, 300f), -0.4f + t * 0.2f, 2.2f + t * 0.2f, 40f, 70f,
            Color.White, thickness: 8f, dash: new DashStyle(14f, 8f, offset: t * 2f, cap: DashCap.Round));

        var points = new Vector2[]
        {
            new(240f, 340f), new(300f, 260f), new(360f, 350f), new(420f, 270f), new(480f, 340f),
        };
        sb.DrawPath(points, 12f, Color.Transparent, Color.LightGoldenrodYellow, thickness: 6f,
            join: PathJoin.Round, cap: PathCap.Round);

        // Text: the glyphs are solved in the pixel shader from outlines in the RGBA32F atlas, so
        // this is the path that exercises the float texture end to end. Each line is one string,
        // and a string resolves its own gradient frame across the whole of itself.
        sb.DrawString(font, "Apos.Shapes on NFMWorld.Graphics", new Vector2(560f, 250f), 26f,
            new Gradient(new Vector2(560f, 250f), Color.White,
                         new Vector2(1180f, 250f), Color.SkyBlue));
        sb.DrawString(font, "glyphs, gradients and dashes", new Vector2(560f, 296f), 22f, Color.LightGray);
        sb.DrawString(font, $"frame {t * 60f:0}", new Vector2(560f, 336f), 22f, Color.CadetBlue);

        // An SVG document, drawn from its own colors (the overload that takes a fill would
        // override them). This is the third pixel path: the document is tessellated to paths and
        // then rasterized by the same shader as everything else.
        sb.DrawSvg(svg, new Vector2(160f, 430f), 220f, rotation: MathF.Sin(t) * 0.15f);
        sb.DrawSvg(svg, new Vector2(560f, 430f), 220f, new Gradient(
            new Vector2(560f, 430f), Color.OrangeRed,
            new Vector2(780f, 650f), Color.MediumSpringGreen), rotation: -MathF.Sin(t) * 0.15f);
    }

    // A document with a few element kinds in it - a rounded rect with a stroke, a circle and a
    // path - drawn from a 64x64 viewBox so one "em" is the whole drawing's height.
    private const string IconSvg = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64">
          <rect x="5" y="9" width="54" height="46" rx="8" fill="#0f172a" stroke="#38bdf8" stroke-width="4"/>
          <circle cx="21" cy="24" r="6" fill="#fbbf24"/>
          <path d="M9 51 L26 30 L38 44 L45 36 L57 51 Z" fill="#34d399"/>
        </svg>
        """;
}

/// <summary>
/// A shown SDL window with a desktop GL 3.3 core context, and the device on it.
///
/// Shaped like the DesktopGL smoke test's host, with the one difference that decides everything
/// else: the window is visible, because this program exists to be looked at. That also means it
/// owns the event pump, which the smoke test has no use for.
///
/// The context setup mirrors <c>WorldGame.CreateGlContext</c> - same profile, same version, same
/// stencil request - so this example renders on the driver configuration the game does.
///
/// Disposal is the reverse of construction and both halves are needed: the device goes first
/// because every GL object it created belongs to the context, and the context before the window it
/// was made from.
/// </summary>
internal sealed class GlHost : IDisposable
{
    /// <summary><c>SDL_GL_CONTEXT_PROFILE_CORE</c>, from <c>SDL_video.h</c>.</summary>
    private const int CoreProfile = 0x0001;

    private readonly SdlWindow _window;
    private readonly IntPtr _context;
    private readonly GlGraphicsDevice _device;

    public GlGraphicsDevice Device => _device;

    public bool ShouldQuit => _window.ShouldQuit;

    /// <summary>The GL renderer string, so the run says which driver it measured.</summary>
    public string Renderer { get; }

    public GlHost(string title, int width, int height)
    {
        _window = SdlWindow.Create(title, width, height,
            extraFlagsRaw: (ulong)(SDL.SDL_WindowFlags.SDL_WINDOW_OPENGL |
                                   SDL.SDL_WindowFlags.SDL_WINDOW_RESIZABLE));

        SDL.SDL_GL_SetAttribute(SDL.SDL_GLAttr.SDL_GL_CONTEXT_MAJOR_VERSION, 3);
        SDL.SDL_GL_SetAttribute(SDL.SDL_GLAttr.SDL_GL_CONTEXT_MINOR_VERSION, 3);
        SDL.SDL_GL_SetAttribute(SDL.SDL_GLAttr.SDL_GL_CONTEXT_PROFILE_MASK, CoreProfile);

        // The stencil buffer the game asks for and for the same reason: NanoVG's stencil pipelines
        // and the clip rectangles share one, and a context without one fails those calls silently
        // rather than loudly.
        SDL.SDL_GL_SetAttribute(SDL.SDL_GLAttr.SDL_GL_STENCIL_SIZE, 8);

        if (!SDL.SDL_GL_LoadLibrary(null))
            throw new InvalidOperationException($"SDL_GL_LoadLibrary failed: {SDL.SDL_GetError()}");

        _context = SDL.SDL_GL_CreateContext(_window.Handle);
        if (_context == IntPtr.Zero)
            throw new InvalidOperationException($"SDL_GL_CreateContext failed: {SDL.SDL_GetError()}");

        // VSync, which is what the game does too - without it a 600-frame run spins the GPU for no
        // reason and the frame counter becomes a measure of nothing.
        SDL.SDL_GL_SetSwapInterval(1);

        _device = GlGraphicsDevice.Create(
            SDL.SDL_GL_GetProcAddress, width, height,
            () => SDL.SDL_GL_SwapWindow(_window.Handle));

        Renderer = ReadRenderer();
    }

    public void PumpEvents() => _window.PumpEvents();

    /// <summary>
    /// The GL_RENDERER string, read through the loader rather than the device: the device exposes
    /// its Silk.NET GL object, but not for callers of a different backend, and this is the one fact
    /// worth printing either way.
    /// </summary>
    private static unsafe string ReadRenderer()
    {
        const int glRenderer = 0x1F01;
        var getString = SDL.SDL_GL_GetProcAddress("glGetString");
        if (getString == IntPtr.Zero) return "(glGetString unavailable)";

        var function = (delegate* unmanaged[Cdecl]<int, byte*>)getString;
        var value = function(glRenderer);
        // GL's strings are only valid until the next GL call on the context, so they are marshalled
        // here rather than handed out as pointers.
        return value is null ? "(null)" : Marshal.PtrToStringUTF8((nint)value) ?? "(unreadable)";
    }

    public void Dispose()
    {
        // The device first: every GL object it made belongs to the context below it.
        _device.Dispose();
        SDL.SDL_GL_DestroyContext(_context);
        _window.Dispose();
    }
}
