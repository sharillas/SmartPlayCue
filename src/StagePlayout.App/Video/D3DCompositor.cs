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
    public const int SlotLayer3 = 4;
    public const int SlotLayer4 = 5;
    public const int SlotCount = 6;

    /// <summary>Resolução do preview PROGRAM lido pela janela de controlo (16:9).</summary>
    public const int PreviewWidth = 480;
    public const int PreviewHeight = 270;

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
    private ID3D11BlendState? _blendAdd;
    private int _width, _height;
    public int OutputWidth => _width;
    public int OutputHeight => _height;

    // Preview PROGRAM: render target fora do ecrã + staging para leitura CPU.
    // O render loop faz copy+map (mesmo thread D3D11) e publica bytes num buffer
    // partilhado; a UI lê com TryCopyPreviewInto (sem chamadas D3D fora do loop).
    private ID3D11Texture2D? _previewTex;
    private ID3D11RenderTargetView? _previewRtv;
    private ID3D11Texture2D? _previewStaging;
    private readonly object _previewLock = new();
    private byte[] _previewBuffer = new byte[PreviewWidth * PreviewHeight * 4];
    private long _previewSeq;
    private long _previewReadSeq;
    private int _frameCounter;

    private readonly Slot[] _slots = { new(), new(), new(), new(), new(), new() };
    private int[] _drawOrder = { 0, 1, 2, 3, 4, 5 }; // layers (2..5) sempre por cima

    private Thread? _thread;
    private volatile bool _quit;
    private readonly Stopwatch _frameClock = new();

    // Diagnóstico de vsync: contagem de frames perdidos (missed presents)
    private long _dropCount;
    private double _refIntervalMs = 16.7; // média móvel do intervalo saudável

    /// <summary>Frames de vsync perdidos desde o arranque do compositor.</summary>
    public long DropCount => Interlocked.Read(ref _dropCount);

    public event Action<int>? FadeCompleted;

    private sealed class Slot
    {
        public FFDecoder? Source;
        public double Opacity, Target, Rate; // Rate: unidades por segundo
        public double BaseVolume = 1.0;      // volume master do slot
        public double VolumeScale = 1.0;     // escala por cue (0..1)
        public double LastAppliedVolume = -1;
        public float X, Y = 0, W = 1, H = 1;
        public float Z;
        public float Rotation; // radians
        public long Gen;
        public bool BlendAdd;             // true = blend aditivo (Add), false = alpha normal
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

    /// <summary>Volume base do slot — o volume efetivo = BaseVolume × VolumeScale × opacidade.</summary>
    public void SetBaseVolume(int slot, double vol)
    {
        lock (_apiLock) _slots[slot].BaseVolume = vol;
    }

    /// <summary>Escala de volume por cue (0..1) — multiplica o volume efetivo do slot.</summary>
    public void SetVolumeScale(int slot, double scale)
    {
        lock (_apiLock) _slots[slot].VolumeScale = scale;
    }

    public void SetGeometry(int slot, float x, float y, float w, float h)
    {
        lock (_apiLock)
        {
            _slots[slot].X = x; _slots[slot].Y = y;
            _slots[slot].W = w; _slots[slot].H = h;
        }
    }

    public void SetRotation(int slot, float radians)
    {
        lock (_apiLock) _slots[slot].Rotation = radians;
    }

    /// <summary>Blend mode do slot: false = alpha normal, true = aditivo (Add).</summary>
    public void SetBlendMode(int slot, bool additive)
    {
        lock (_apiLock) _slots[slot].BlendAdd = additive;
    }

    /// <summary>Z-order dos slots de programa (layers têm prioridade fixa no topo).</summary>
    public void SetZ(int slot, float z)
    {
        lock (_apiLock)
        {
            _slots[slot].Z = z;
            var progFirst = _slots[1].Z < _slots[0].Z ? 1 : 0;
            _drawOrder = new[] { progFirst, 1 - progFirst, 2, 3, 4, 5 };
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
        InitPreview();
    }

    private void InitPreview()
    {
        var device = _device!;
        _previewTex = device.CreateTexture2D(new Texture2DDescription
        {
            Width = PreviewWidth,
            Height = PreviewHeight,
            Format = Format.B8G8R8A8_UNorm,
            MipLevels = 1,
            ArraySize = 1,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
        });
        _previewRtv = device.CreateRenderTargetView(_previewTex);

        _previewStaging = device.CreateTexture2D(new Texture2DDescription
        {
            Width = PreviewWidth,
            Height = PreviewHeight,
            Format = Format.B8G8R8A8_UNorm,
            MipLevels = 1,
            ArraySize = 1,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
    }

    private void CreateRtv()
    {
        _rtv?.Dispose();
        using var back = _swap!.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _device!.CreateRenderTargetView(back);
    }

    private const string VsSrc = @"
cbuffer CB : register(b0) { float4 rect; float opacity; float rotation; float2 pad; }
struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD; };
VSOut main(uint id : SV_VertexID)
{
    float2 uv = float2((id == 1 || id == 3 || id == 4) ? 1.0 : 0.0,
                       (id >= 2 && id != 3) ? 1.0 : 0.0);
    VSOut o;
    // rotate UV around center if needed
    float2 ruv = uv;
    if (rotation != 0.0)
    {
        float s, c;
        sincos(rotation, s, c);
        float2 ctr = uv - 0.5;
        ruv = float2(ctr.x * c - ctr.y * s + 0.5, ctr.x * s + ctr.y * c + 0.5);
    }
    float2 p01 = ruv * rect.zw + rect.xy;
    o.pos = float4(p01 * float2(2.0, -2.0) + float2(-1.0, 1.0), 0, 1);
    o.uv = uv; // keep original UVs for texture, position is rotated
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
        var addDesc = new BlendDescription();
        addDesc.RenderTarget[0] = new RenderTargetBlendDescription
        {
            SourceBlend = Blend.One,
            DestinationBlend = Blend.One,
            SourceBlendAlpha = Blend.One,
            DestinationBlendAlpha = Blend.One,
            BlendOperation = BlendOperation.Add,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All,
        };
        _blendAdd = _device.CreateBlendState(addDesc);
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

                // vsync health: intervalo >> média = frames perdidos (UI/OSC leem DropCount)
                var dtMs = dt * 1000.0;
                if (dtMs > _refIntervalMs * 1.8)
                {
                    var missed = (int)(dtMs / _refIntervalMs) - 1;
                    if (missed > 0)
                        Interlocked.Add(ref _dropCount, missed);
                }
                _refIntervalMs = _refIntervalMs * 0.95 + Math.Min(dtMs, _refIntervalMs * 2.0) * 0.05;

                if (++resizeGuard >= 30)
                {
                    resizeGuard = 0;
                    CheckResize();
                }

                Render((float)dt);
                if ((_frameCounter++ & 1) == 0)
                    CapturePreview();
                _swap!.Present(1, PresentFlags.None); // vsync
                errorCount = 0;
            }
            catch (Exception ex)
            {
                if (++errorCount <= 5)
                    VideoLog.Write($"Compositor render ERRO: {ex.Message}");

                // TDR / device lost: tenta recriar o device D3D11 e continuar
                if (errorCount >= 3 && IsDeviceRemoved() && RecoverDevice())
                {
                    VideoLog.Write("Compositor: device recuperado (TDR)");
                    errorCount = 0;
                }
                Thread.Sleep(errorCount >= 3 ? 250 : 50); // nunca matar o render loop
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

    private bool IsDeviceRemoved()
    {
        try
        {
            // S_OK = 0; qualquer outro valor = device perdido (TDR / removed)
            return _device is not null && (int)_device.DeviceRemovedReason != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// TDR / device lost: destrói os recursos do device antigo e recria tudo.
    /// As texturas dos slots voltam a ser criadas (lazy) no device novo.
    /// </summary>
    private bool RecoverDevice()
    {
        try
        {
            VideoLog.Write("Compositor: device lost (TDR) — a recriar...");

            foreach (var s in _slots)
            {
                s.Srv?.Dispose(); s.Tex?.Dispose();
                s.Srv = null; s.Tex = null;
                s.Gen = -1; // forçar re-upload no device novo
            }
            _rtv?.Dispose(); _rtv = null;
            _previewStaging?.Dispose(); _previewStaging = null;
            _previewRtv?.Dispose(); _previewRtv = null;
            _previewTex?.Dispose(); _previewTex = null;
            _swap?.Dispose(); _swap = null;
            _cb?.Dispose(); _cb = null;
            _sampler?.Dispose(); _sampler = null;
            _blend?.Dispose(); _blend = null;
            _blendAdd?.Dispose(); _blendAdd = null;
            _vs?.Dispose(); _vs = null;
            _ps?.Dispose(); _ps = null;
            _ctx?.Dispose(); _ctx = null;
            _device?.Dispose(); _device = null;

            InitDevice();
            return true;
        }
        catch (Exception ex)
        {
            VideoLog.Write($"Compositor: recuperação FALHOU: {ex.Message}");
            return false;
        }
    }

    private void Render(float dt)
    {
        if (_rtv is null) return;
        DrawScene(_rtv, _width, _height, dt, animate: true);
    }

    /// <summary>Desenha a cena completa (programa + layers) no target dado.</summary>
    private void DrawScene(ID3D11RenderTargetView rtv, int w, int h, float dt, bool animate)
    {
        _ctx!.OMSetRenderTargets(rtv);
        _ctx.RSSetViewport(0, 0, w, h);
        _ctx.ClearRenderTargetView(rtv, new Color4(0f, 0f, 0f, 1f));

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
            DrawSlot(i, dt, animate);
    }

    /// <summary>Copia o preview para staging e publica o frame (chamado pelo render loop).</summary>
    private void CapturePreview()
    {
        if (_previewRtv is null || _previewTex is null || _previewStaging is null) return;

        // 2.º desenho no target de preview (animate=false: não avança fades 2× por frame)
        DrawScene(_previewRtv, PreviewWidth, PreviewHeight, 0, animate: false);

        _ctx!.CopyResource(_previewStaging, _previewTex);
        var box = _ctx.Map(_previewStaging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var rowPitch = (int)box.RowPitch;
            var src = box.DataPointer;
            var rowBytes = PreviewWidth * 4;
            var size = PreviewHeight * rowBytes;
            lock (_previewLock)
            {
                if (_previewBuffer.Length != size)
                    _previewBuffer = new byte[size];
                fixed (byte* dst = _previewBuffer)
                {
                    if (rowPitch == rowBytes)
                    {
                        Buffer.MemoryCopy((void*)src, dst, size, size);
                    }
                    else
                    {
                        for (var y = 0; y < PreviewHeight; y++)
                            Buffer.MemoryCopy((void*)(src + y * rowPitch), dst + y * rowBytes, rowBytes, rowBytes);
                    }
                }
                _previewSeq++;
            }
        }
        finally
        {
            _ctx.Unmap(_previewStaging, 0);
        }
    }

    /// <summary>
    /// Copia o último frame de preview para o buffer do chamador (thread-safe, sem chamadas D3D).
    /// Retorna false se não há frame novo.
    /// </summary>
    public bool TryCopyPreviewInto(byte[] dst, out long seq)
    {
        seq = _previewReadSeq;
        if (_previewSeq == _previewReadSeq) return false;
        lock (_previewLock)
        {
            if (_previewSeq == _previewReadSeq) return false;
            Buffer.BlockCopy(_previewBuffer, 0, dst, 0, Math.Min(dst.Length, _previewBuffer.Length));
            _previewReadSeq = _previewSeq;
            seq = _previewReadSeq;
            return true;
        }
    }

    private void DrawSlot(int index, float dt, bool animate)
    {
        Slot s;
        FFDecoder? dec;
        lock (_apiLock)
        {
            s = _slots[index];
            // progresso da opacidade (dentro do lock para consistência)
            if (animate && s.Opacity != s.Target)
            {
                var step = s.Rate * dt;
                var diff = s.Target - s.Opacity;
                s.Opacity = Math.Abs(diff) <= step ? s.Target : s.Opacity + Math.Sign(diff) * step;
                if (s.Opacity == s.Target)
                    FadeCompleted?.Invoke(index);
            }
            dec = s.Source;

            // áudio segue o fade de vídeo: volume efetivo = base × escala(cue) × opacidade
            if (animate && dec != null)
            {
                var v = s.Opacity * s.BaseVolume * s.VolumeScale;
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

        if (_ctx is null) return;

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
        if (_cb is null) return;

        // constant buffer: rect + opacity + rotation
        var cb = new CbData { X = s.X, Y = s.Y, W = s.W, H = s.H, Opacity = (float)s.Opacity, Rotation = s.Rotation };
        var mapped = _ctx.Map(_cb, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        *(CbData*)mapped.DataPointer = cb;
        _ctx.Unmap(_cb, 0);

        _ctx.OMSetBlendState(s.BlendAdd ? _blendAdd : _blend);
        _ctx.PSSetShaderResource(0, s.Srv);
        _ctx.Draw(6, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CbData
    {
        public float X, Y, W, H;       // rect (float4)
        public float Opacity;          // float
        public float Rotation;         // float (radians)
        public float Pad0, Pad1;       // float2 pad
    }

    private void EnsureTexture(Slot s, int w, int h)
    {
        if (s.Tex is not null && s.TexW == w && s.TexH == h) return;
        if (_device is null) return;

        s.Srv?.Dispose();
        s.Tex?.Dispose();

        s.Tex = _device.CreateTexture2D(new Texture2DDescription
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
        _previewStaging?.Dispose();
        _previewRtv?.Dispose();
        _previewTex?.Dispose();
        _cb?.Dispose();
        _sampler?.Dispose();
        _blend?.Dispose();
        _blendAdd?.Dispose();
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
