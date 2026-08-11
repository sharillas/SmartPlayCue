using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace StagePlayout.App.Video;

/// <summary>
/// Compositor GPU próprio (D3D11 via Vortice).
/// Desenha N "slots" (cada um com um FFDecoder como fonte) num swapchain,
/// com opacidade animada no render loop (vsync) — crossfade real entre cues.
/// Preparado para geometry por slot (layers com alpha na fase 2b).
/// </summary>
public sealed unsafe class D3DCompositor : IDisposable
{
    public const int SlotProg1 = 0;
    public const int SlotProg2 = 1;
    public const int SlotLayer1 = 2;
    public const int SlotLayer2 = 3;
    public const int SlotCount = 4;

    private readonly IntPtr _hwnd;
    private readonly object _apiLock = new();

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _ctx;
    private IDXGISwapChain1? _swap;
    private ID3D11RenderTargetView? _rtv;
    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _ps;
    private ID3D11Buffer? _cb;
    private ID3D11SamplerState? _sampler;
    private ID3D11BlendState? _blend;
    private int _width, _height;

    private readonly Slot[] _slots = { new(), new(), new(), new() };
    private int[] _drawOrder = { 0, 1, 2, 3 }; // layers (2,3) sempre por cima

    private Thread? _thread;
    private volatile bool _quit;
    private readonly Stopwatch _frameClock = new();

    public event Action<int>? FadeCompleted;

    private sealed class Slot
    {
        public FFDecoder? Source;
        public double Opacity, Target, Rate; // Rate: unidades por segundo
        public double BaseVolume = 1.0;      // volume master do slot
        public double LastAppliedVolume = -1;
        public float X, Y = 0, W = 1, H = 1;
        public float Z;
        public long Gen;
        public ID3D11Texture2D? Tex;
        public ID3D11ShaderResourceView? Srv;
        public int TexW, TexH;
    }

    public D3DCompositor(IntPtr hwnd)
    {
        _hwnd = hwnd;
        VideoLog.Write($"Compositor: init hwnd={hwnd}");
        InitDevice();
        VideoLog.Write($"Compositor: device OK {_width}x{_height}");
    }

    // ===== API pública (thread-safe) =====

    public void SetSource(int slot, FFDecoder? dec)
    {
        lock (_apiLock) _slots[slot].Source = dec;
    }

    public void SetOpacity(int slot, double target, double seconds)
    {
        lock (_apiLock)
        {
            var s = _slots[slot];
            s.Target = target;
            if (seconds <= 0.001)
            {
                var changed = s.Opacity != target;
                s.Opacity = target;
                s.Rate = 0;
                if (changed) FadeCompleted?.Invoke(slot);
            }
            else
            {
                s.Rate = Math.Abs(target - s.Opacity) / seconds;
            }
        }
    }

    /// <summary>Volume base do slot — o volume efetivo = BaseVolume × opacidade (audio fade = video fade).</summary>
    public void SetBaseVolume(int slot, double vol)
    {
        lock (_apiLock) _slots[slot].BaseVolume = vol;
    }

    public void SetGeometry(int slot, float x, float y, float w, float h)
    {
        lock (_apiLock)
        {
            var s = _slots[slot];
            s.X = x; s.Y = y; s.W = w; s.H = h;
        }
    }

    /// <summary>Z-order dos slots de programa (layers têm prioridade fixa no topo).</summary>
    public void SetZ(int slot, float z)
    {
        lock (_apiLock)
        {
            _slots[slot].Z = z;
            var progFirst = _slots[1].Z < _slots[0].Z ? 1 : 0;
            _drawOrder = new[] { progFirst, 1 - progFirst, 2, 3 };
        }
    }

    // ===== D3D11 =====

    private void InitDevice()
    {
        GetClientRect(_hwnd, out var rc);
        _width = Math.Max(8, rc.Right - rc.Left);
        _height = Math.Max(8, rc.Bottom - rc.Top);

        D3D11.D3D11CreateDevice(
            null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0 },
            out _device, out _, out _ctx).CheckError();

        using var dxgiDev = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDev.GetParent<IDXGIAdapter>();
        using var factory = adapter.GetParent<IDXGIFactory2>();

        var desc = new SwapChainDescription1
        {
            Width = (uint)_width,
            Height = (uint)_height,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Ignore,
        };
        _swap = factory.CreateSwapChainForHwnd(_device, _hwnd, desc);

        CreateRtv();
        InitPipeline();
    }

    private void CreateRtv()
    {
        _rtv?.Dispose();
        using var back = _swap!.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _device!.CreateRenderTargetView(back);
    }

    private const string VsSrc = @"
cbuffer CB : register(b0) { float4 rect; float opacity; float3 pad; }
struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD; };
VSOut main(uint id : SV_VertexID)
{
    // quad em triangle list (6 verts): (0,0)(1,0)(0,1) + (1,0)(1,1)(0,1)
    float2 uv = float2((id == 1 || id == 3 || id == 4) ? 1.0 : 0.0,
                       (id >= 2 && id != 3) ? 1.0 : 0.0);
    VSOut o;
    float2 p01 = uv * rect.zw + rect.xy;          // 0..1 (origem topo-esquerda)
    o.pos = float4(p01 * float2(2.0, -2.0) + float2(-1.0, 1.0), 0, 1);
    o.uv = uv;
    return o;
}";

    private const string PsSrc = @"
cbuffer CB : register(b0) { float4 rect; float opacity; float3 pad; }
Texture2D tex : register(t0);
SamplerState smp : register(s0);
struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD; };
float4 main(VSOut i) : SV_TARGET
{
    float4 c = tex.Sample(smp, i.uv);
    return float4(c.rgb, c.a * opacity);
}";

    private void InitPipeline()
    {
        var vsBlob = Compiler.Compile(VsSrc, "main", "vs.hlsl", "vs_4_0");
        var psBlob = Compiler.Compile(PsSrc, "main", "ps.hlsl", "ps_4_0");
        _vs = _device!.CreateVertexShader(vsBlob.Span);
        _ps = _device.CreatePixelShader(psBlob.Span);

        _cb = _device.CreateBuffer(new BufferDescription(
            32, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));

        _sampler = _device.CreateSamplerState(new SamplerDescription(
            Filter.MinMagMipLinear, TextureAddressMode.Clamp));

        _blend = _device.CreateBlendState(BlendDescription.NonPremultiplied);
    }

    public void Start()
    {
        _quit = false;
        _thread = new Thread(RenderLoop) { IsBackground = true, Name = "Compositor" };
        _thread.Start();
    }

    private void RenderLoop()
    {
        _frameClock.Restart();
        var resizeGuard = 0;
        var errorCount = 0;

        VideoLog.Write("Compositor: render loop start");

        while (!_quit)
        {
            try
            {
                var dt = _frameClock.Elapsed.TotalSeconds;
                _frameClock.Restart();

                if (++resizeGuard >= 30)
                {
                    resizeGuard = 0;
                    CheckResize();
                }

                Render((float)dt);
                _swap!.Present(1, PresentFlags.None); // vsync
                errorCount = 0;
            }
            catch (Exception ex)
            {
                if (++errorCount <= 5)
                    VideoLog.Write($"Compositor render ERRO: {ex.Message}");
                Thread.Sleep(50); // nunca matar o render loop
            }
        }
        VideoLog.Write("Compositor: render loop fim");
    }

    private void CheckResize()
    {
        GetClientRect(_hwnd, out var rc);
        var w = rc.Right - rc.Left;
        var h = rc.Bottom - rc.Top;
        if (w < 8 || h < 8 || (w == _width && h == _height)) return;

        _width = w; _height = h;
        _rtv?.Dispose(); _rtv = null;
        _swap!.ResizeBuffers(2, (uint)w, (uint)h, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
        CreateRtv();
    }

    private void Render(float dt)
    {
        _ctx!.OMSetRenderTargets(_rtv!);
        _ctx.RSSetViewport(0, 0, _width, _height);
        _ctx.ClearRenderTargetView(_rtv!, new Color4(0f, 0f, 0f, 1f));

        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _ctx.VSSetShader(_vs!);
        _ctx.PSSetShader(_ps!);
        _ctx.VSSetConstantBuffer(0, _cb!);
        _ctx.PSSetConstantBuffer(0, _cb!);
        _ctx.PSSetSampler(0, _sampler!);
        _ctx.OMSetBlendState(_blend);

        int[] order;
        lock (_apiLock) order = _drawOrder;

        foreach (var i in order)
            DrawSlot(i, dt);
    }

    private void DrawSlot(int index, float dt)
    {
        Slot s;
        FFDecoder? dec;
        lock (_apiLock)
        {
            s = _slots[index];
            // progresso da opacidade (dentro do lock para consistência)
            if (s.Opacity != s.Target)
            {
                var step = s.Rate * dt;
                var diff = s.Target - s.Opacity;
                s.Opacity = Math.Abs(diff) <= step ? s.Target : s.Opacity + Math.Sign(diff) * step;
                if (s.Opacity == s.Target)
                    FadeCompleted?.Invoke(index);
            }
            dec = s.Source;

            // áudio segue o fade de vídeo: volume efetivo = base × opacidade
            if (dec != null)
            {
                var v = s.Opacity * s.BaseVolume;
                if (Math.Abs(v - s.LastAppliedVolume) > 0.004)
                {
                    dec.Volume = v;
                    s.LastAppliedVolume = v;
                }
            }
        }

        if (dec is null || s.Opacity <= 0.001) return;

        // upload do frame mais recente
        byte[]? data;
        int w, h, stride;
        long gen;
        lock (dec.FrameLock)
        {
            data = dec.FrameData;
            w = dec.FrameWidth; h = dec.FrameHeight;
            stride = dec.FrameStride; gen = dec.FrameGen;
        }

        if (data is not null && gen != s.Gen)
        {
            EnsureTexture(s, w, h);
            if (s.Tex is not null)
            {
                fixed (byte* p = data)
                    _ctx.UpdateSubresource(s.Tex, 0, null, (IntPtr)p, (uint)stride, 0u);
                s.Gen = gen;
                if (gen <= 2) VideoLog.Write($"Compositor: slot {index} upload frame gen={gen} {w}x{h} op={s.Opacity:0.00}");
            }
        }

        if (s.Srv is null) return;

        // constant buffer: rect + opacity
        var cb = new CbData { X = s.X, Y = s.Y, W = s.W, H = s.H, Opacity = (float)s.Opacity };
        var mapped = _ctx.Map(_cb!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        *(CbData*)mapped.DataPointer = cb;
        _ctx.Unmap(_cb!, 0);

        _ctx.PSSetShaderResource(0, s.Srv);
        _ctx.Draw(6, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CbData
    {
        public float X, Y, W, H;       // rect (float4)
        public float Opacity;          // float
        public float Pad0, Pad1, Pad2; // float3 pad
    }

    private void EnsureTexture(Slot s, int w, int h)
    {
        if (s.Tex is not null && s.TexW == w && s.TexH == h) return;

        s.Srv?.Dispose();
        s.Tex?.Dispose();

        s.Tex = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            Format = Format.B8G8R8A8_UNorm,
            MipLevels = 1,
            ArraySize = 1,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
        });
        s.Srv = _device.CreateShaderResourceView(s.Tex);
        s.TexW = w; s.TexH = h;
        s.Gen = -1; // forçar upload
    }

    public void Dispose()
    {
        _quit = true;
        if (_thread is { IsAlive: true }) _thread.Join(1000);

        foreach (var s in _slots)
        {
            s.Srv?.Dispose();
            s.Tex?.Dispose();
        }
        _rtv?.Dispose();
        _swap?.Dispose();
        _cb?.Dispose();
        _sampler?.Dispose();
        _blend?.Dispose();
        _vs?.Dispose();
        _ps?.Dispose();
        _ctx?.Dispose();
        _device?.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
}
