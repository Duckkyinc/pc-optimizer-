using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using WinForms = System.Windows.Forms;
using Gdi = System.Drawing;

namespace Overdrive.Core;

/// <summary>
/// Built-in 3D benchmark. Opens a full-screen DirectX 11 window and renders four original procedural scenes,
/// each stressing a different part of the PC, while timing every frame. Nothing here touches games.
/// </summary>
public static class Benchmark
{
    public record StageInfo(string Name, string Tests, string Description);

    public static readonly StageInfo[] Stages =
    {
        new("Neon City", "Geometry", "260,000 lit buildings. Tests geometry power."),
        new("Fractal Core", "Shaders", "A 3D fractal drawn per pixel. Tests shader power."),
        new("Galaxy Storm", "Fill rate", "1.2 million glowing particles. Tests fill rate."),
        new("Draw Call Rush", "CPU + driver", "12,000 draw calls a frame. Tests CPU and driver."),
    };

    public const int TestVersion = 2;
    public static bool Running { get; private set; }

    /// <summary>Runs the benchmark on its own thread. Returns null if cancelled or failed (error in <paramref name="error"/>).</summary>
    public static Task<(SimRun Run, string Error)> RunAsync(bool quick)
    {
        var tcs = new TaskCompletionSource<(SimRun, string)>();
        var t = new Thread(() =>
        {
            Running = true;
            var proc = Process.GetCurrentProcess();
            var oldPri = proc.PriorityClass;
            try
            {
                proc.PriorityClass = ProcessPriorityClass.Normal; // we lower ourselves while gaming; undo that for a fair test
                using var r = new Renderer(quick);
                tcs.SetResult((r.Run(), r.Cancelled ? null : r.Error));
            }
            catch (Exception ex) { Store.Log("Benchmark failed: " + ex); tcs.TrySetResult((null, ex.Message)); }
            finally { try { proc.PriorityClass = oldPri; } catch { } Running = false; }
        }) { IsBackground = true, Name = "Benchmark" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    // ------------------------------------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    struct FrameCB
    {
        public Matrix4x4 ViewProj;
        public Vector4 Cam;     // xyz pos, w time
        public Vector4 Params;  // x aspect, y width, z height, w tan(fov/2)
        public Vector4 Right, Up, Fwd;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ObjCB { public Vector4 Pos, Color; }

    [StructLayout(LayoutKind.Sequential)]
    struct HudCB { public Vector4 Rect, Uv, Color, Flags; }

    sealed class Renderer : IDisposable
    {
        readonly bool _quick;
        readonly double _warm, _len;
        public bool Cancelled;
        public string Error;

        WinForms.Form _form;
        ID3D11Device _dev;
        ID3D11DeviceContext _ctx;
        IDXGISwapChain1 _swap;
        ID3D11RenderTargetView _rtv;
        ID3D11DepthStencilView _dsv;
        ID3D11Texture2D _depth;
        bool _tearing;
        int _w, _h;

        ID3D11Buffer _frameCb, _objCb, _hudCb;
        ID3D11VertexShader _vsCity, _vsGround, _vsFull, _vsParticle, _vsObj, _vsHud;
        ID3D11PixelShader _psCity, _psGround, _psFractal, _psParticle, _psHud;
        ID3D11BlendState _opaque, _additive, _premul;
        ID3D11DepthStencilState _depthOn, _depthOff;
        ID3D11RasterizerState _raster;
        ID3D11SamplerState _sampler;
        readonly List<(ID3D11Texture2D Tex, ID3D11ShaderResourceView Srv, int W, int H)> _labels = new();
        (ID3D11Texture2D Tex, ID3D11ShaderResourceView Srv, int W, int H) _digits, _esc, _fpsLbl;

        public Renderer(bool quick)
        {
            _quick = quick;
            _warm = quick ? 1.5 : 2.5;
            _len = quick ? 5 : 12;
        }

        public SimRun Run()
        {
            _form = new WinForms.Form
            {
                Text = "PC Optimizer Benchmark", FormBorderStyle = WinForms.FormBorderStyle.None, StartPosition = WinForms.FormStartPosition.Manual,
                Bounds = WinForms.Screen.PrimaryScreen.Bounds, BackColor = Gdi.Color.Black, TopMost = true, KeyPreview = true, ShowInTaskbar = true
            };
            try { _form.Icon = Gdi.Icon.ExtractAssociatedIcon(Environment.ProcessPath); } catch { }
            _form.KeyDown += (_, e) => { if (e.KeyCode == WinForms.Keys.Escape) Cancelled = true; };
            _form.FormClosing += (_, _) => Cancelled = true;
            _form.Show();
            WinForms.Cursor.Hide();
            _form.Activate(); _form.BringToFront(); _form.Focus();
            try
            {
                Init();
                return Loop();
            }
            catch (Exception ex) { Store.Log("Benchmark error: " + ex); Error = ex.Message; return null; }
            finally
            {
                WinForms.Cursor.Show();
                try { _form.Close(); } catch { }
            }
        }

        void Init()
        {
            _w = Math.Max(1, _form.ClientSize.Width); _h = Math.Max(1, _form.ClientSize.Height);
            var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
            D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out _dev, out _ctx).CheckError();

            try { using var dxgiDev = _dev.QueryInterface<IDXGIDevice1>(); dxgiDev.MaximumFrameLatency = 1; } catch { }
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();
            try { using var f5 = factory.QueryInterfaceOrNull<IDXGIFactory5>(); _tearing = f5 != null && f5.PresentAllowTearing; } catch { _tearing = false; }
            var desc = new SwapChainDescription1
            {
                Width = (uint)_w, Height = (uint)_h, Format = Format.B8G8R8A8_UNorm, BufferCount = 2,
                BufferUsage = Usage.RenderTargetOutput, SampleDescription = new SampleDescription(1, 0),
                SwapEffect = SwapEffect.FlipDiscard, Scaling = Scaling.Stretch, AlphaMode = AlphaMode.Ignore,
                Flags = _tearing ? SwapChainFlags.AllowTearing : SwapChainFlags.None
            };
            _swap = factory.CreateSwapChainForHwnd(_dev, _form.Handle, desc);
            factory.MakeWindowAssociation(_form.Handle, WindowAssociationFlags.IgnoreAltEnter);

            using (var back = _swap.GetBuffer<ID3D11Texture2D>(0)) _rtv = _dev.CreateRenderTargetView(back);
            _depth = _dev.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_w, Height = (uint)_h, MipLevels = 1, ArraySize = 1, Format = Format.D32_Float,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.DepthStencil
            });
            _dsv = _dev.CreateDepthStencilView(_depth);

            _frameCb = Cb(Marshal.SizeOf<FrameCB>());
            _objCb = Cb(Marshal.SizeOf<ObjCB>());
            _hudCb = Cb(Marshal.SizeOf<HudCB>());

            _vsCity = Vs("VSCity"); _vsGround = Vs("VSGround"); _vsFull = Vs("VSFull"); _vsParticle = Vs("VSParticle"); _vsObj = Vs("VSObj"); _vsHud = Vs("VSHud");
            _psCity = Ps("PSCity"); _psGround = Ps("PSGround"); _psFractal = Ps("PSFractal"); _psParticle = Ps("PSParticle"); _psHud = Ps("PSHud");

            _opaque = _dev.CreateBlendState(BlendDescription.Opaque);
            _additive = _dev.CreateBlendState(BlendDescription.Additive);
            _premul = _dev.CreateBlendState(BlendDescription.AlphaBlend);
            _depthOn = _dev.CreateDepthStencilState(DepthStencilDescription.Default);
            _depthOff = _dev.CreateDepthStencilState(DepthStencilDescription.None);
            _raster = _dev.CreateRasterizerState(RasterizerDescription.CullNone);
            _sampler = _dev.CreateSamplerState(SamplerDescription.LinearClamp);

            for (int i = 0; i < Stages.Length; i++) _labels.Add(StageLabel(i));
            _digits = MakeTexture(DrawDigits());
            _esc = MakeTexture(DrawText("ESC  to cancel", 26, Gdi.Color.FromArgb(150, 255, 255, 255), false));
            _fpsLbl = MakeTexture(DrawText("FPS", 26, Gdi.Color.FromArgb(170, 141, 150, 171), true));
        }

        ID3D11Buffer Cb(int size) => _dev.CreateBuffer(new BufferDescription((uint)((size + 15) / 16 * 16), BindFlags.ConstantBuffer, ResourceUsage.Default));

        static ReadOnlyMemory<byte> Compile(string entry, string profile)
        {
            var code = Compiler.Compile(Shaders.Source, entry, "bench.hlsl", profile, ShaderFlags.OptimizationLevel3);
            return code;
        }
        ID3D11VertexShader Vs(string e) => _dev.CreateVertexShader(Compile(e, "vs_5_0").Span);
        ID3D11PixelShader Ps(string e) => _dev.CreatePixelShader(Compile(e, "ps_5_0").Span);

        // ---------------- main loop ----------------
        SimRun Loop()
        {
            var run = new SimRun
            {
                At = DateTime.Now, TestVersion = Benchmark.TestVersion, Mode = _quick ? "Quick" : "Full", Resolution = $"{_w} x {_h}",
                Gpu = SysInfo.PrimaryGpu?.Name ?? "GPU"
            };
            var clock = Stopwatch.StartNew();
            double fpsShown = 0, fpsAcc = 0; int fpsN = 0; double fpsT = 0;

            for (int s = 0; s < Stages.Length && !Cancelled; s++)
            {
                var times = new List<double>(20000);
                double start = clock.Elapsed.TotalSeconds, last = start;
                while (!Cancelled)
                {
                    WinForms.Application.DoEvents();
                    double now = clock.Elapsed.TotalSeconds;
                    double dt = now - last; last = now;
                    double local = now - start;
                    if (local >= _warm + _len) break;
                    if (local > _warm && dt > 0) times.Add(dt * 1000.0);

                    if (dt > 0) { fpsAcc += dt; fpsN++; }
                    if (now - fpsT > 0.25 && fpsN > 0) { fpsShown = fpsN / fpsAcc; fpsAcc = 0; fpsN = 0; fpsT = now; }

                    RenderStage(s, (float)local);
                    double overall = (s + Math.Min(1, local / (_warm + _len))) / Stages.Length;
                    DrawHud(s, local < _warm, fpsShown, overall);
                    _swap.Present(0, _tearing ? PresentFlags.AllowTearing : PresentFlags.None);
                }
                if (Cancelled) break;
                run.Stages.Add(Summarize(Stages[s].Name, times));
            }
            if (Cancelled || run.Stages.Count != Stages.Length) return null;

            double Geo(IEnumerable<double> v) { var a = v.Where(x => x > 0).ToArray(); return a.Length == 0 ? 0 : Math.Exp(a.Average(Math.Log)); }
            // Score: geometric mean of FPS weighted 70/30 toward 1% lows so smoothness counts, x10 for a nicer number.
            run.GpuScore = Math.Round(Geo(run.Stages.Take(3).Select(x => 0.7 * x.Avg + 0.3 * x.Low1)) * 10);
            run.CpuScore = Math.Round((0.7 * run.Stages[3].Avg + 0.3 * run.Stages[3].Low1) * 10);
            run.Score = Math.Round(Geo(run.Stages.Select(x => 0.7 * x.Avg + 0.3 * x.Low1)) * 10);
            run.CappedAt = DetectCap(run);
            if (run.CappedAt > 0) Store.Log($"Benchmark: FPS looks capped at ~{run.CappedAt:0}");
            return run;
        }

        /// <summary>Four very different loads giving the same FPS means something (V-Sync, G-Sync limit, FPS cap) is limiting it.</summary>
        static double DetectCap(SimRun run)
        {
            var avgs = run.Stages.Select(x => x.Avg).Where(x => x > 0).ToArray();
            if (avgs.Length < 3) return 0;
            if (avgs.Max() / avgs.Min() < 1.06) return Math.Round(avgs.Average());
            int hz = 0; try { hz = Display.Cached?.CurrentHz ?? 0; } catch { }
            if (hz > 0 && avgs.Count(a => Math.Abs(a - hz) / hz < 0.03) >= 2) return hz;
            return 0;
        }

        static SimStage Summarize(string name, List<double> ms)
        {
            if (ms.Count == 0) return new SimStage { Name = name };
            var sorted = ms.OrderByDescending(x => x).ToArray(); // worst first
            double total = ms.Sum();
            double Low(double pct)
            {
                int n = Math.Max(1, (int)Math.Ceiling(sorted.Length * pct));
                return 1000.0 / sorted.Take(n).Average();
            }
            return new SimStage
            {
                Name = name, Frames = ms.Count, Avg = Math.Round(ms.Count / (total / 1000.0), 1),
                Low1 = Math.Round(Low(0.01), 1), Low01 = Math.Round(Low(0.001), 1), FrameMs = Math.Round(total / ms.Count, 2)
            };
        }

        // ---------------- scenes ----------------
        void SetCamera(Vector3 pos, Vector3 target, float t, float fovDeg = 60)
        {
            float aspect = (float)_w / _h, fov = fovDeg * MathF.PI / 180f;
            var view = Matrix4x4.CreateLookAt(pos, target, Vector3.UnitY);
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(fov, aspect, 0.1f, 600f);
            var fwd = Vector3.Normalize(target - pos);
            var right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitY));
            var up = Vector3.Cross(right, fwd);
            var cb = new FrameCB
            {
                ViewProj = view * proj, Cam = new Vector4(pos, t), Params = new Vector4(aspect, _w, _h, MathF.Tan(fov / 2)),
                Right = new Vector4(right, 0), Up = new Vector4(up, 0), Fwd = new Vector4(fwd, 0)
            };
            _ctx.UpdateSubresource(in cb, _frameCb);
        }

        void BeginScene(Color4 clear)
        {
            _ctx.OMSetRenderTargets(_rtv, _dsv);
            _ctx.RSSetViewport(new Viewport(0, 0, _w, _h));
            _ctx.RSSetState(_raster);
            _ctx.ClearRenderTargetView(_rtv, clear);
            _ctx.ClearDepthStencilView(_dsv, DepthStencilClearFlags.Depth, 1f, 0);
            _ctx.IASetInputLayout(null);
            _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _ctx.VSSetConstantBuffer(0, _frameCb); _ctx.PSSetConstantBuffer(0, _frameCb);
            _ctx.VSSetConstantBuffer(1, _objCb); _ctx.PSSetConstantBuffer(1, _objCb);
        }

        void Ground()
        {
            _ctx.OMSetBlendState(_opaque); _ctx.OMSetDepthStencilState(_depthOn);
            _ctx.VSSetShader(_vsGround); _ctx.PSSetShader(_psGround);
            _ctx.Draw(6, 0);
        }

        void RenderStage(int s, float t)
        {
            switch (s)
            {
                case 0:
                {
                    float a = t * 0.12f;
                    SetCamera(new Vector3(MathF.Cos(a) * 95, 26 + MathF.Sin(t * 0.4f) * 6, MathF.Sin(a) * 95),
                              new Vector3(MathF.Cos(a + 0.9f) * 30, 4, MathF.Sin(a + 0.9f) * 30), t);
                    BeginScene(new Color4(0.02f, 0.02f, 0.05f, 1));
                    Ground();
                    _ctx.VSSetShader(_vsCity); _ctx.PSSetShader(_psCity);
                    _ctx.DrawInstanced(36, 512 * 512, 0, 0);
                    break;
                }
                case 1:
                {
                    float a = t * 0.25f;
                    var pos = new Vector3(MathF.Sin(a) * 2.35f, MathF.Sin(t * 0.17f) * 0.9f, MathF.Cos(a) * 2.35f);
                    SetCamera(pos, Vector3.Zero, t, 55);
                    BeginScene(new Color4(0, 0, 0, 1));
                    _ctx.OMSetBlendState(_opaque); _ctx.OMSetDepthStencilState(_depthOff);
                    _ctx.VSSetShader(_vsFull); _ctx.PSSetShader(_psFractal);
                    _ctx.Draw(3, 0);
                    break;
                }
                case 2:
                {
                    float a = t * 0.18f;
                    SetCamera(new Vector3(MathF.Cos(a) * 62, 26 + MathF.Sin(t * 0.3f) * 10, MathF.Sin(a) * 62), new Vector3(0, -2, 0), t);
                    BeginScene(new Color4(0.005f, 0.005f, 0.015f, 1));
                    _ctx.OMSetBlendState(_additive); _ctx.OMSetDepthStencilState(_depthOff);
                    _ctx.VSSetShader(_vsParticle); _ctx.PSSetShader(_psParticle);
                    _ctx.DrawInstanced(6, 1_200_000, 0, 0);
                    break;
                }
                default:
                {
                    float a = t * 0.2f;
                    SetCamera(new Vector3(MathF.Cos(a) * 70, 30, MathF.Sin(a) * 70), new Vector3(0, 12, 0), t);
                    BeginScene(new Color4(0.02f, 0.015f, 0.04f, 1));
                    Ground();
                    _ctx.VSSetShader(_vsObj); _ctx.PSSetShader(_psCity);
                    const int n = 12000;
                    for (int i = 0; i < n; i++)
                    {
                        float f = i / (float)n;
                        float ang = f * 60f + t * (0.6f + f);
                        float rad = 8 + f * 40;
                        var o = new ObjCB
                        {
                            Pos = new Vector4(MathF.Cos(ang) * rad, 2 + f * 40 + MathF.Sin(t * 2 + i) * 1.5f, MathF.Sin(ang) * rad, 0.9f + (i % 7) * 0.12f),
                            Color = new Vector4(0.35f + 0.65f * f, 0.4f + 0.5f * (1 - f), 1f, 1)
                        };
                        _ctx.UpdateSubresource(in o, _objCb);
                        _ctx.Draw(36, 0);
                    }
                    break;
                }
            }
        }

        // ---------------- HUD ----------------
        void DrawHud(int stage, bool warming, double fps, double progress)
        {
            float k = _h / 1080f;
            _ctx.OMSetRenderTargets(_rtv, (ID3D11DepthStencilView)null);
            _ctx.OMSetBlendState(_premul); _ctx.OMSetDepthStencilState(_depthOff);
            _ctx.VSSetShader(_vsHud); _ctx.PSSetShader(_psHud);
            _ctx.VSSetConstantBuffer(2, _hudCb); _ctx.PSSetConstantBuffer(2, _hudCb);
            _ctx.PSSetSampler(0, _sampler);
            _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);

            // stage panel (top-left)
            var lab = _labels[stage];
            float lw = lab.W * k * 0.5f, lh = lab.H * k * 0.5f;
            Rect(28 * k, 28 * k, lw + 28 * k, lh + 20 * k, new Vector4(0.02f, 0.025f, 0.04f, 0.72f));
            Rect(28 * k, 28 * k, 4 * k, lh + 20 * k, new Vector4(0, 0.9f, 1f, 1));
            Tex(lab, 46 * k, 38 * k, lw, lh, new Vector4(1, 1, 1, warming ? 0.6f : 1));

            // live fps (top-right)
            string s = fps <= 0 ? "--" : Math.Min(99999, (int)Math.Round(fps)).ToString();
            float dh = 64 * k, cw = _digits.W / 10f, dw = dh * cw / _digits.H;
            float totalW = dw * s.Length;
            float x0 = _w - 40 * k - totalW;
            Rect(x0 - 22 * k, 28 * k, totalW + 44 * k, dh + 46 * k, new Vector4(0.02f, 0.025f, 0.04f, 0.72f));
            for (int i = 0; i < s.Length; i++)
            {
                int d = s[i] == '-' ? -1 : s[i] - '0';
                if (d < 0) { Rect(x0 + i * dw + dw * 0.2f, 28 * k + 14 * k + dh * 0.5f, dw * 0.6f, 5 * k, new Vector4(0.8f, 0.85f, 0.9f, 1)); continue; }
                Tex(_digits, x0 + i * dw, 28 * k + 10 * k, dw, dh, new Vector4(1, 1, 1, 1), new Vector4(d / 10f, 0, (d + 1) / 10f, 1));
            }
            float fl = _fpsLbl.H * k * 0.5f;
            Tex(_fpsLbl, x0 + totalW - _fpsLbl.W * k * 0.5f, 28 * k + 12 * k + dh, _fpsLbl.W * k * 0.5f, fl, new Vector4(1, 1, 1, 1));

            // progress (bottom)
            float barY = _h - 46 * k, barH = 8 * k, barX = 40 * k, barW = _w - 80 * k;
            float seg = barW / Stages.Length;
            for (int i = 0; i < Stages.Length; i++)
                Rect(barX + i * seg + (i > 0 ? 3 * k : 0), barY, seg - (i > 0 ? 3 * k : 0), barH, new Vector4(1, 1, 1, 0.12f));
            Rect(barX, barY, (float)(barW * Math.Clamp(progress, 0, 1)), barH, new Vector4(0, 0.9f, 1f, 0.95f), gradient: true);
            Tex(_esc, _w - 40 * k - _esc.W * k * 0.5f, barY - 34 * k, _esc.W * k * 0.5f, _esc.H * k * 0.5f, new Vector4(1, 1, 1, 1));
        }

        void Rect(float x, float y, float w, float h, Vector4 color, bool gradient = false) =>
            Quad(x, y, w, h, new Vector4(0, 0, 1, 1), color, new Vector4(0, gradient ? 1 : 0, 0, 0), null);

        void Tex((ID3D11Texture2D Tex, ID3D11ShaderResourceView Srv, int W, int H) t, float x, float y, float w, float h, Vector4 color, Vector4? uv = null) =>
            Quad(x, y, w, h, uv ?? new Vector4(0, 0, 1, 1), color, new Vector4(1, 0, 0, 0), t.Srv);

        void Quad(float x, float y, float w, float h, Vector4 uv, Vector4 color, Vector4 flags, ID3D11ShaderResourceView srv)
        {
            var cb = new HudCB
            {
                Rect = new Vector4(x / _w * 2 - 1, 1 - y / _h * 2, (x + w) / _w * 2 - 1, 1 - (y + h) / _h * 2),
                Uv = uv, Color = color, Flags = flags
            };
            _ctx.UpdateSubresource(in cb, _hudCb);
            if (srv != null) _ctx.PSSetShaderResource(0, srv);
            _ctx.Draw(4, 0);
        }

        // ---------------- text textures (drawn once with GDI+) ----------------
        (ID3D11Texture2D, ID3D11ShaderResourceView, int, int) StageLabel(int i)
        {
            var st = Stages[i];
            var bmp = new Gdi.Bitmap(1100, 150, Gdi.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Gdi.Graphics.FromImage(bmp))
            {
                g.Clear(Gdi.Color.Transparent);
                g.TextRenderingHint = Gdi.Text.TextRenderingHint.AntiAliasGridFit;
                using var small = new Gdi.Font("Segoe UI", 22, Gdi.FontStyle.Bold, Gdi.GraphicsUnit.Pixel);
                using var big = new Gdi.Font("Segoe UI", 56, Gdi.FontStyle.Bold, Gdi.GraphicsUnit.Pixel);
                using var sub = new Gdi.Font("Segoe UI", 26, Gdi.FontStyle.Regular, Gdi.GraphicsUnit.Pixel);
                using var cyan = new Gdi.SolidBrush(Gdi.Color.FromArgb(255, 0, 229, 255));
                using var white = new Gdi.SolidBrush(Gdi.Color.White);
                using var gray = new Gdi.SolidBrush(Gdi.Color.FromArgb(255, 141, 150, 171));
                g.DrawString($"TEST {i + 1} OF {Stages.Length}  ·  {st.Tests.ToUpperInvariant()}", small, cyan, 0, 0);
                g.DrawString(st.Name, big, white, -3, 26);
                g.DrawString(Shorten(st.Description), sub, gray, 0, 104);
            }
            var r = MakeTexture(Crop(bmp));
            return (r.Tex, r.Srv, r.W, r.H);
        }

        static string Shorten(string s) { int i = s.IndexOf('.'); return i > 0 ? s[..i] : s; }

        static Gdi.Bitmap DrawText(string text, int px, Gdi.Color color, bool bold)
        {
            var bmp = new Gdi.Bitmap(px * text.Length + 20, (int)(px * 1.5), Gdi.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Gdi.Graphics.FromImage(bmp))
            {
                g.Clear(Gdi.Color.Transparent);
                g.TextRenderingHint = Gdi.Text.TextRenderingHint.AntiAliasGridFit;
                using var f = new Gdi.Font("Segoe UI", px, bold ? Gdi.FontStyle.Bold : Gdi.FontStyle.Regular, Gdi.GraphicsUnit.Pixel);
                using var b = new Gdi.SolidBrush(color);
                g.DrawString(text, f, b, 0, 0);
            }
            return Crop(bmp);
        }

        static Gdi.Bitmap DrawDigits()
        {
            const int cw = 80, ch = 128;
            var bmp = new Gdi.Bitmap(cw * 10, ch, Gdi.Imaging.PixelFormat.Format32bppPArgb);
            using var g = Gdi.Graphics.FromImage(bmp);
            g.Clear(Gdi.Color.Transparent);
            g.TextRenderingHint = Gdi.Text.TextRenderingHint.AntiAliasGridFit;
            using var f = new Gdi.Font("Segoe UI", 118, Gdi.FontStyle.Bold, Gdi.GraphicsUnit.Pixel);
            using var b = new Gdi.SolidBrush(Gdi.Color.White);
            using var fmt = new Gdi.StringFormat { Alignment = Gdi.StringAlignment.Center, LineAlignment = Gdi.StringAlignment.Center };
            for (int d = 0; d < 10; d++) g.DrawString(d.ToString(), f, b, new Gdi.RectangleF(d * cw, -6, cw, ch + 10), fmt);
            return bmp;
        }

        /// <summary>Trims transparent space on the right/bottom so quads size to the text.</summary>
        static Gdi.Bitmap Crop(Gdi.Bitmap bmp)
        {
            int maxX = 1, maxY = 1;
            var data = bmp.LockBits(new Gdi.Rectangle(0, 0, bmp.Width, bmp.Height), Gdi.Imaging.ImageLockMode.ReadOnly, Gdi.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                var row = new int[bmp.Width];
                for (int y = 0; y < bmp.Height; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, bmp.Width);
                    for (int x = 0; x < bmp.Width; x++)
                        if ((row[x] >>> 24) > 8) { if (x > maxX) maxX = x; if (y > maxY) maxY = y; }
                }
            }
            finally { bmp.UnlockBits(data); }
            var c = bmp.Clone(new Gdi.Rectangle(0, 0, Math.Min(bmp.Width, maxX + 6), Math.Min(bmp.Height, maxY + 6)), Gdi.Imaging.PixelFormat.Format32bppPArgb);
            bmp.Dispose();
            return c;
        }

        (ID3D11Texture2D Tex, ID3D11ShaderResourceView Srv, int W, int H) MakeTexture(Gdi.Bitmap bmp)
        {
            var data = bmp.LockBits(new Gdi.Rectangle(0, 0, bmp.Width, bmp.Height), Gdi.Imaging.ImageLockMode.ReadOnly, Gdi.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                var tex = _dev.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)bmp.Width, Height = (uint)bmp.Height, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource
                }, new[] { new SubresourceData(data.Scan0, (uint)data.Stride) });
                var srv = _dev.CreateShaderResourceView(tex);
                _labelsAll.Add(tex); _labelsAll.Add(srv);
                return (tex, srv, bmp.Width, bmp.Height);
            }
            finally { bmp.UnlockBits(data); bmp.Dispose(); }
        }
        readonly List<IDisposable> _labelsAll = new();

        public void Dispose()
        {
            try { _ctx?.ClearState(); _ctx?.Flush(); } catch { }
            foreach (var d in _labelsAll) try { d.Dispose(); } catch { }
            IDisposable[] all = { _vsCity, _vsGround, _vsFull, _vsParticle, _vsObj, _vsHud, _psCity, _psGround, _psFractal, _psParticle, _psHud,
                _opaque, _additive, _premul, _depthOn, _depthOff, _raster, _sampler, _frameCb, _objCb, _hudCb, _dsv, _depth, _rtv, _swap, _ctx, _dev };
            foreach (var d in all) try { d?.Dispose(); } catch { }
            try { _form?.Dispose(); } catch { }
        }
    }

    // ------------------------------------------------------------------------------------------------
    static class Shaders
    {
        public const string Source = @"
cbuffer Frame : register(b0)
{
    row_major float4x4 ViewProj;
    float4 Cam;
    float4 Params;
    float4 Right;
    float4 Up;
    float4 Fwd;
};
cbuffer Obj : register(b1) { float4 ObjPos; float4 ObjColor; };
cbuffer Hud : register(b2) { float4 HRect; float4 HUv; float4 HColor; float4 HFlags; };
Texture2D Tex0 : register(t0);
SamplerState Samp : register(s0);

float hash11(float n) { return frac(sin(n * 12.9898) * 43758.5453); }
float uhash(uint x) { x ^= x >> 16; x *= 0x7feb352du; x ^= x >> 15; x *= 0x846ca68bu; x ^= x >> 16; return x * (1.0 / 4294967296.0); }
float hash21(float2 p) { p = frac(p * float2(123.34, 456.21)); p += dot(p, p + 45.32); return frac(p.x * p.y); }

static const uint Q[6] = { 0, 1, 2, 2, 1, 3 };
float3 CubeVert(uint vid, out float3 n)
{
    uint f = vid / 6; uint q = Q[vid % 6];
    float2 uv = float2(q & 1, q >> 1) * 2 - 1;
    float s = (f & 1) ? 1 : -1; uint ax = f >> 1;
    float3 p;
    if (ax == 0) { p = float3(s, uv.x, uv.y); n = float3(s, 0, 0); }
    else if (ax == 1) { p = float3(uv.x, s, uv.y); n = float3(0, s, 0); }
    else { p = float3(uv.x, uv.y, s); n = float3(0, 0, s); }
    return p * 0.5;
}

struct CityV { float4 pos : SV_Position; float3 wp : WP; float3 n : NORMAL; float3 col : COLOR; float seed : SEED; };

CityV VSCity(uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    const uint N = 512;
    float gx = (float)(iid % N), gz = (float)(iid / N);
    float h = hash21(float2(gx, gz));
    float d = length(float2(gx, gz) - N * 0.5) / (N * 0.5);
    float height = 1.0 + pow(h, 3.0) * 22.0 * (1.2 - d);
    float3 n; float3 p = CubeVert(vid, n);
    float3 scale = float3(1.35, height, 1.35);
    float3 wp = p * scale + float3((gx - N * 0.5) * 2.0, height * 0.5, (gz - N * 0.5) * 2.0);
    CityV o;
    o.pos = mul(float4(wp, 1), ViewProj);
    o.wp = wp; o.n = n; o.seed = h;
    o.col = lerp(float3(0.10, 0.12, 0.20), float3(0.16, 0.12, 0.26), hash11(gx * 7 + gz));
    return o;
}

CityV VSObj(uint vid : SV_VertexID)
{
    float3 n; float3 p = CubeVert(vid, n);
    float3 wp = p * ObjPos.w * float3(1, 2.2, 1) + ObjPos.xyz;
    CityV o;
    o.pos = mul(float4(wp, 1), ViewProj);
    o.wp = wp; o.n = n; o.seed = frac(ObjPos.x * 0.37 + ObjPos.z * 0.11);
    o.col = ObjColor.rgb * 0.35;
    return o;
}

float3 Fog(float3 c, float3 wp)
{
    float dist = length(wp - Cam.xyz);
    float f = 1 - exp(-dist * 0.0085);
    float3 fogc = lerp(float3(0.05, 0.03, 0.12), float3(0.02, 0.10, 0.16), saturate(wp.y * 0.02));
    return lerp(c, fogc, saturate(f));
}

float4 PSCity(CityV i) : SV_Target
{
    float3 L = normalize(float3(0.4, 0.8, 0.3));
    float diff = saturate(dot(i.n, L)) * 0.7 + 0.3;
    float3 c = i.col * diff;
    if (abs(i.n.y) < 0.5)
    {
        float along = (abs(i.n.x) > 0.5) ? i.wp.z : i.wp.x;
        float2 cell = float2(floor(along * 3.0), floor(i.wp.y * 2.0));
        float2 fr = float2(frac(along * 3.0), frac(i.wp.y * 2.0));
        float win = step(0.25, fr.x) * step(fr.x, 0.75) * step(0.3, fr.y) * step(fr.y, 0.8);
        float lit = step(0.55, hash21(cell + i.seed * 91.0));
        float3 neon = lerp(float3(0.0, 0.9, 1.0), float3(0.55, 0.36, 0.96), step(0.5, hash21(cell * 1.7 + i.seed)));
        c += win * lit * neon * (0.8 + 0.4 * sin(Cam.w * 2 + i.seed * 30));
    }
    else c += float3(0.02, 0.05, 0.08);
    [loop] for (int k = 0; k < 12; k++)
    {
        float a = Cam.w * (0.3 + k * 0.05) + k * 0.5236;
        float3 lp = float3(cos(a) * (20 + k * 6), 3.0 + (k % 3) * 4.0, sin(a) * (20 + k * 6));
        float3 d = lp - i.wp; float dist2 = dot(d, d);
        float3 lc = (k & 1) ? float3(0.0, 0.9, 1.0) : float3(0.6, 0.35, 1.0);
        c += lc * saturate(dot(i.n, d * rsqrt(dist2))) * (30.0 / (dist2 + 1.0));
    }
    return float4(Fog(c, i.wp), 1);
}

struct GV { float4 pos : SV_Position; float3 wp : WP; };
GV VSGround(uint vid : SV_VertexID)
{
    float2 q = float2((Q[vid] & 1), (Q[vid] >> 1)) * 2 - 1;
    float3 wp = float3(q.x * 700, 0, q.y * 700);
    GV o; o.pos = mul(float4(wp, 1), ViewProj); o.wp = wp; return o;
}
float4 PSGround(GV i) : SV_Target
{
    float2 g = abs(frac(i.wp.xz * 0.5) - 0.5) / fwidth(i.wp.xz * 0.5);
    float line_ = 1 - saturate(min(g.x, g.y));
    float3 c = float3(0.015, 0.02, 0.04) + line_ * float3(0.0, 0.35, 0.45);
    return float4(Fog(c, i.wp), 1);
}

struct FV { float4 pos : SV_Position; float2 uv : UV; };
FV VSFull(uint vid : SV_VertexID)
{
    float2 uv = float2((vid << 1) & 2, vid & 2);
    FV o; o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1); o.uv = float2(uv.x * 2 - 1, 1 - uv.y * 2); return o;
}

float DE(float3 pos, out float trap)
{
    float3 z = pos; float dr = 1.0; float r = 0.0; trap = 1e9;
    float power = 8.0 + sin(Cam.w * 0.15) * 1.5;
    [loop] for (int i = 0; i < 9; i++)
    {
        r = max(length(z), 1e-6);
        if (r > 2.0) break;
        float theta = acos(clamp(z.y / r, -1, 1));
        float phi = atan2(z.z, z.x);
        dr = pow(r, power - 1.0) * power * dr + 1.0;
        float zr = pow(r, power);
        theta *= power; phi *= power;
        z = zr * float3(sin(theta) * cos(phi), cos(theta), sin(phi) * sin(theta)) + pos;
        trap = min(trap, r);
    }
    return 0.5 * log(r) * r / dr;
}

float4 PSFractal(FV i) : SV_Target
{
    float3 ro = Cam.xyz;
    float3 rd = normalize(Fwd.xyz + Right.xyz * i.uv.x * Params.w * Params.x + Up.xyz * i.uv.y * Params.w);
    float t = 0.0, trap = 0, minD = 1e9; int steps = 0;
    bool hit = false;
    [loop] for (int s = 0; s < 160; s++)
    {
        float d = DE(ro + rd * t, trap);
        minD = min(minD, d);
        if (d < 0.0006 * t) { hit = true; break; }
        t += d;
        steps = s;
        if (t > 8.0) break;
    }
    float3 bg = lerp(float3(0.01, 0.01, 0.03), float3(0.04, 0.02, 0.09), i.uv.y * 0.5 + 0.5);
    bg += float3(0.0, 0.5, 0.7) * exp(-minD * 18.0) * 0.35;
    if (!hit) return float4(bg, 1);
    float3 p = ro + rd * t; float tr;
    float e = 0.0005 * t;
    float3 n = normalize(float3(
        DE(p + float3(e, 0, 0), tr) - DE(p - float3(e, 0, 0), tr),
        DE(p + float3(0, e, 0), tr) - DE(p - float3(0, e, 0), tr),
        DE(p + float3(0, 0, e), tr) - DE(p - float3(0, 0, e), tr)));
    float3 L = normalize(float3(0.6, 0.7, -0.4));
    float diff = saturate(dot(n, L));
    float ao = 1.0 - steps / 160.0;
    float3 base = lerp(float3(0.0, 0.85, 1.0), float3(0.55, 0.30, 0.95), saturate(trap * 1.6 - 0.5));
    float spec = pow(saturate(dot(reflect(rd, n), L)), 24.0);
    float3 c = base * (0.15 + diff * 0.85) * ao + spec * 0.6;
    return float4(c, 1);
}

struct PV { float4 pos : SV_Position; float2 q : Q; float3 col : COLOR; };
PV VSParticle(uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    float h1 = uhash(iid * 4u), h2 = uhash(iid * 4u + 1u), h3 = uhash(iid * 4u + 2u), h4 = uhash(iid * 4u + 3u);
    float r = pow(h1, 0.6) * 48.0 + 0.5;
    float arm = floor(h2 * 3.0) * 2.0944;
    float spin = Cam.w * 0.35 / (1.0 + r * 0.06);
    float a = r * 0.16 + arm + spin + (h3 - 0.5) * 0.9;
    float y = (h4 - 0.5) * 3.0 * (1.0 - r / 50.0) + sin(Cam.w + r * 0.2) * 0.4;
    float3 c = float3(cos(a) * r, y, sin(a) * r);
    float2 q = float2((Q[vid] & 1), (Q[vid] >> 1)) * 2 - 1;
    float size = 0.25 + h3 * 0.45;
    float3 wp = c + (Right.xyz * q.x + Up.xyz * q.y) * size;
    PV o; o.pos = mul(float4(wp, 1), ViewProj); o.q = q;
    float k = saturate(r / 40.0);
    o.col = lerp(float3(1.0, 0.85, 0.6), lerp(float3(0.0, 0.8, 1.0), float3(0.6, 0.3, 1.0), h2), k) * 0.07;
    return o;
}
float4 PSParticle(PV i) : SV_Target
{
    float d = dot(i.q, i.q);
    float a = saturate(1.0 - d); a *= a;
    return float4(i.col * a, 1);
}

struct HV { float4 pos : SV_Position; float2 uv : UV; float2 lq : LQ; };
HV VSHud(uint vid : SV_VertexID)
{
    float2 q = float2(vid & 1, vid >> 1);
    HV o;
    o.pos = float4(lerp(HRect.x, HRect.z, q.x), lerp(HRect.y, HRect.w, q.y), 0, 1);
    o.uv = lerp(HUv.xy, HUv.zw, q);
    o.lq = q;
    return o;
}
float4 PSHud(HV i) : SV_Target
{
    float4 c = HColor;
    if (HFlags.y > 0.5) c.rgb = lerp(float3(0.0, 0.9, 1.0), float3(0.55, 0.36, 0.96), i.lq.x);
    if (HFlags.x > 0.5) { float4 t = Tex0.Sample(Samp, i.uv); return t * c; }
    return float4(c.rgb * c.a, c.a);
}
";
    }
}
