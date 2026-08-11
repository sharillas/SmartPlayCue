using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Flyleaf.FFmpeg;
using NAudio.Wave;
using static Flyleaf.FFmpeg.Raw;

namespace StagePlayout.App.Video;

public enum DecoderState { Empty, Ready, Playing, Paused, Ended, Failed }

/// <summary>
/// Decoder próprio baseado em FFmpeg (SW decode) para o compositor GPU:
/// - Vídeo: frames BGRA publicados em ring buffer (thread-safe via FrameLock)
/// - Áudio: S16 48kHz stereo via NAudio (WASAPI shared)
/// - Pre-roll: abre já com o 1.º frame pronto (GO instantâneo)
/// - Loop interno, seek, pacing por clock próprio
/// </summary>
public unsafe sealed class FFDecoder : IDisposable
{
    private const int OutRate = 48000;

    // ===== Estado público =====
    public DecoderState State { get; private set; } = DecoderState.Empty;
    public long DurationTicks { get; private set; }
    public long CurTimeTicks => _curContentMs * 10_000;
    public string? Error { get; private set; }
    public bool Loop;
    public event EventHandler? Ended;

    private double _volume = 1.0;
    public double Volume
    {
        get => _volume;
        set { _volume = value; if (_volProvider != null) _volProvider.Volume = (float)value; }
    }

    // ===== Frame atual (lido pelo compositor) =====
    public readonly object FrameLock = new();
    public byte[]? FrameData;
    public int FrameWidth, FrameHeight, FrameStride;
    public long FrameGen;

    // ===== FFmpeg =====
    private AVFormatContext* _fmt;
    private AVCodecContext* _vctx;
    private AVCodecContext* _actx;
    private AVStream* _vstream;
    private SwsContext* _sws;
    private int _swsSrcFmt = -2;
    private SwrContext* _swr;
    private AVPacket* _pkt;
    private AVFrame* _vframe;
    private AVFrame* _aframe;
    private int _vidx = -1, _aidx = -1;
    private int _inRate;

    // HW decode (D3D11VA): GPU descodifica; transferência NV12->CPU e sws->BGRA
    private AVBufferRef* _hwDevice;
    private bool _hwActive;
    private AVFrame* _swFrame;

    // Deinterlace automático (yadif bob) para conteúdo interlaçado
    private AVFilterGraph* _filterGraph;
    private AVFilterContext* _bufSrc;
    private AVFilterContext* _bufSink;
    private AVFrame* _filtFrame;
    private bool _deintChecked;
    private bool _deintActive;

    // ===== Pacing / clock (precisão sub-ms) =====
    private readonly Stopwatch _sw = new();
    private double _swBaseMs;    // conteúdo (ms) quando o relógio (re)começou
    private long _curContentMs;  // pts (ms) do último frame publicado
    private double NowMs => _swBaseMs + (_sw.IsRunning ? _sw.Elapsed.TotalMilliseconds : 0);

    // Pts híbrido: usa o pts real quando válido/monotónico; senão sintético (frame duration)
    private long _lastRawPts = long.MinValue;
    private double _lastGoodDue;
    private double _frameDurMs = 33.3;

    // ===== Ring de buffers BGRA =====
    private byte[][] _bufs = Array.Empty<byte[]>();
    private int _bufIdx;

    // ===== Áudio (NAudio) =====
    private BufferedWaveProvider? _waveProvider;
    private VolumeWaveProvider16? _volProvider;
    private WasapiOut? _wasapi;
    private byte[] _audioBuf = Array.Empty<byte>();

    private Thread? _thread;
    private volatile bool _quit;

    static FFDecoder()
    {
        // DLLs nativas do FFmpeg ficam em .\FFmpeg (resolver próprio)
        FFmpegNatives.Ensure();
    }

    // ===== API =====

    public bool Open(string path, bool autoPlay)
    {
        try
        {
            AVFormatContext* fmt = null;
            if (avformat_open_input(&fmt, path, null, null) < 0)
            {
                Error = "avformat_open_input falhou";
                return false;
            }
            _fmt = fmt;

            if (avformat_find_stream_info(_fmt, null) < 0)
            {
                Error = "find_stream_info falhou";
                return false;
            }

            _vidx = av_find_best_stream(_fmt, AVMediaType.Video, -1, -1, null, 0);
            _aidx = av_find_best_stream(_fmt, AVMediaType.Audio, -1, -1, null, 0);

            if (_vidx < 0)
            {
                Error = "sem stream de vídeo";
                return false;
            }

            _vstream = _fmt->streams[_vidx];
            var vcodec = avcodec_find_decoder(_vstream->codecpar->codec_id);
            _vctx = avcodec_alloc_context3(vcodec);
            avcodec_parameters_to_context(_vctx, _vstream->codecpar);
            _vctx->thread_count = 0; // auto (multithread)

            // HW decode D3D11VA (se disponível): menos CPU, mais fluidez
            AVBufferRef* hwdev = null;
            if (av_hwdevice_ctx_create(&hwdev, AVHWDeviceType.D3d11va, null, null, 0) >= 0)
            {
                _vctx->hw_device_ctx = av_buffer_ref(hwdev);
                _hwDevice = hwdev;
                _hwActive = true;
            }

            if (avcodec_open2(_vctx, vcodec, null) < 0)
            {
                Error = "codec de vídeo não abriu";
                return false;
            }

            DurationTicks = _fmt->duration > 0 ? _fmt->duration * 10 : 0;

            var fps = av_q2d(_vstream->avg_frame_rate);
            if (fps > 1) _frameDurMs = 1000.0 / fps;

            if (_aidx >= 0)
                SetupAudio(_fmt->streams[_aidx]);

            _pkt = av_packet_alloc();
            _vframe = av_frame_alloc();
            _aframe = av_frame_alloc();

            State = DecoderState.Ready;

            PreRollFirstFrame(); // 1.º frame já disponível (pre-roll)

            VideoLog.Write($"FFDecoder Open OK: {Path.GetFileName(path)} hw={_hwActive} prerollGen={FrameGen} {FrameWidth}x{FrameHeight}");

            if (autoPlay) Play();

            _thread = new Thread(Pump) { IsBackground = true, Name = "FFDecoder" };
            _thread.Start();
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            State = DecoderState.Failed;
            VideoLog.Write($"FFDecoder Open EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public void Play()
    {
        if (State is DecoderState.Playing or DecoderState.Empty or DecoderState.Failed) return;
        if (State == DecoderState.Ended) Seek(0);
        State = DecoderState.Playing;
        _sw.Start();
        _wasapi?.Play();
    }

    public void Pause()
    {
        if (State != DecoderState.Playing) return;
        State = DecoderState.Paused;
        _sw.Stop();
        _wasapi?.Pause();
    }

    public void Seek(long ticks)
    {
        if (_fmt == null) return;
        var ms = ticks / 10_000;
        av_seek_frame(_fmt, -1, ms * 1000, SeekFlags.Backward);
        if (_vctx != null) avcodec_flush_buffers(_vctx);
        if (_actx != null) avcodec_flush_buffers(_actx);
        _waveProvider?.ClearBuffer();
        _swBaseMs = ms;
        _curContentMs = ms;
        _lastRawPts = long.MinValue;
        _lastGoodDue = ms;
        _sw.Reset();
        if (State == DecoderState.Playing) _sw.Start();
    }

    // ===== Internals =====

    private void SetupAudio(AVStream* astream)
    {
        try
        {
            var acodec = avcodec_find_decoder(astream->codecpar->codec_id);
            _actx = avcodec_alloc_context3(acodec);
            avcodec_parameters_to_context(_actx, astream->codecpar);
            if (avcodec_open2(_actx, acodec, null) < 0) { _actx = null; return; }

            _inRate = _actx->sample_rate;
            _swr = swr_alloc();
            var outLayout = AV_CHANNEL_LAYOUT_STEREO;
            av_opt_set_chlayout(_swr, "in_chlayout", &_actx->ch_layout, 0);
            av_opt_set_int(_swr, "in_sample_rate", _actx->sample_rate, 0);
            av_opt_set_sample_fmt(_swr, "in_sample_fmt", _actx->sample_fmt, 0);
            av_opt_set_chlayout(_swr, "out_chlayout", &outLayout, 0);
            av_opt_set_int(_swr, "out_sample_rate", OutRate, 0);
            av_opt_set_sample_fmt(_swr, "out_sample_fmt", AVSampleFormat.S16, 0);
            if (swr_init(_swr) < 0) { _swr = null; _actx = null; return; }

            _waveProvider = new BufferedWaveProvider(new WaveFormat(OutRate, 16, 2))
            {
                BufferDuration = TimeSpan.FromSeconds(1),
                DiscardOnBufferOverflow = true
            };
            _volProvider = new VolumeWaveProvider16(_waveProvider) { Volume = (float)_volume };
            _wasapi = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 60);
            _wasapi.Init(_volProvider);
            // Nota: só toca quando Play() for chamado
        }
        catch
        {
            _actx = null; _swr = null; _wasapi = null; _waveProvider = null; // segue sem áudio
        }
    }

    /// <summary>Decodifica apenas o 1.º frame de vídeo (sem avançar o clock).</summary>
    private void PreRollFirstFrame()
    {
        for (var guard = 0; guard < 400 && FrameGen == 0; guard++)
        {
            if (av_read_frame(_fmt, _pkt) < 0) break;
            if (_pkt->stream_index == _vidx)
                SendVideo(_pkt, pace: false);
            av_packet_unref(_pkt);
        }
    }

    private void Pump()
    {
        while (!_quit)
        {
            try
            {
                if (State != DecoderState.Playing)
                {
                    Thread.Sleep(5);
                    continue;
                }

                var ret = av_read_frame(_fmt, _pkt);
                if (ret < 0)
                {
                    // EOF: esvaziar os decoders
                    if (_vctx != null) { avcodec_send_packet(_vctx, null); DrainVideo(pace: true); }
                    if (_actx != null) { avcodec_send_packet(_actx, null); DrainAudio(); }
                    OnEof();
                    continue;
                }

                if (_pkt->stream_index == _vidx)
                    SendVideo(_pkt, pace: true);
                else if (_pkt->stream_index == _aidx && _actx != null)
                    SendAudio(_pkt);

                av_packet_unref(_pkt);
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                State = DecoderState.Failed;
                VideoLog.Write($"FFDecoder pump EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Envia pacote com tratamento de EAGAIN (decoder cheio):
    /// drena os frames pendentes e reenvia. Spin limitado — nunca bloqueia para sempre.
    /// </summary>
    private void SendVideo(AVPacket* pkt, bool pace)
    {
        const int EAGAIN = -11;
        var ret = avcodec_send_packet(_vctx, pkt);
        var spins = 0;
        while (ret == EAGAIN && !_quit && spins++ < 2000)
        {
            DrainVideo(pace);
            Thread.Sleep(1); // dá tempo às worker threads do decoder
            ret = avcodec_send_packet(_vctx, pkt);
        }
        DrainVideo(pace);
    }

    private void SendAudio(AVPacket* pkt)
    {
        const int EAGAIN = -11;
        var ret = avcodec_send_packet(_actx, pkt);
        var spins = 0;
        while (ret == EAGAIN && !_quit && spins++ < 2000)
        {
            DrainAudio();
            Thread.Sleep(1);
            ret = avcodec_send_packet(_actx, pkt);
        }
        DrainAudio();
    }

    private void OnEof()
    {
        if (Loop)
        {
            Seek(0);
            return;
        }
        // Imagens/stills (duração ~0): ficam paradas, nunca "acabam"
        if (DurationTicks > 0 && DurationTicks < TimeSpan.TicksPerSecond / 2)
        {
            State = DecoderState.Paused;
            _sw.Stop();
            return;
        }
        if (State != DecoderState.Ended)
        {
            State = DecoderState.Ended;
            _sw.Stop();
            _wasapi?.Pause();
            Ended?.Invoke(this, EventArgs.Empty);
        }
    }

    private void DrainVideo(bool pace)
    {
        while (!_quit && avcodec_receive_frame(_vctx, _vframe) >= 0)
        {
            // HW decode: frames D3D11 -> transferir para CPU (NV12)
            AVFrame* frame = _vframe;
            if (_hwActive && _vframe->format == (int)AVPixelFormat.D3d11)
            {
                if (_swFrame == null) _swFrame = av_frame_alloc();
                av_frame_unref(_swFrame);
                if (av_hwframe_transfer_data(_swFrame, _vframe, 0) < 0)
                {
                    av_frame_unref(_vframe);
                    continue;
                }
                av_frame_copy_props(_swFrame, _vframe);
                frame = _swFrame;
            }

            // detetar conteúdo interlaçado no 1.º frame -> ativar yadif bob
            if (!_deintChecked)
            {
                _deintChecked = true;
                if (frame->flags.HasFlag(FrameFlags.Interlaced))
                    _deintActive = InitDeinterlace(frame);
            }

            if (_deintActive)
            {
                if (av_buffersrc_add_frame(_bufSrc, frame) < 0)
                {
                    av_frame_unref(_vframe);
                    continue;
                }
                while (!_quit && av_buffersink_get_frame(_bufSink, _filtFrame) >= 0)
                {
                    PaceAndPublish(_filtFrame, pace);
                    av_frame_unref(_filtFrame);
                }
            }
            else
            {
                PaceAndPublish(frame, pace);
            }

            av_frame_unref(_vframe);
        }
    }

    private void PaceAndPublish(AVFrame* frame, bool pace)
    {
        var pts = frame->best_effort_timestamp;
        double dueMs;

        if (pts != long.MinValue && pts >= _lastRawPts)
        {
            // pts real e monotónico
            dueMs = pts * av_q2d(_vstream->time_base) * 1000.0;
            if (dueMs < _lastGoodDue) dueMs = _lastGoodDue + _frameDurMs; // segurança
            _lastRawPts = pts;
        }
        else
        {
            // pts inválido/a recuar (ex.: hw decode sem best_effort) -> sintético
            dueMs = _lastGoodDue + _frameDurMs;
        }
        _lastGoodDue = dueMs;

        // pacing: só publica quando chega a hora do frame
        if (pace)
        {
            while (!_quit && State == DecoderState.Playing)
            {
                var rem = dueMs - NowMs;
                if (rem <= 0.5) break;
                if (rem > 10) Thread.Sleep(1);
                else Thread.SpinWait(2000); // precisão final sub-ms
            }
        }

        PublishFrame(frame);
        _curContentMs = (long)dueMs;
    }

    /// <summary>Yadif bob (mode=1): 1080i -> 50p suave (cada campo vira um frame).</summary>
    private bool InitDeinterlace(AVFrame* frame)
    {
        try
        {
            _filterGraph = avfilter_graph_alloc();
            if (_filterGraph == null) return false;

            var tb = _vstream->time_base;
            var fps = _vstream->avg_frame_rate;
            var args = $"video_size={frame->width}x{frame->height}:pix_fmt={frame->format}" +
                       $":time_base={tb.Num}/{tb.Den}:pixel_aspect=1/1:frame_rate={fps.Num}/{fps.Den}";

            AVFilterContext* src = null, sink = null, yadif = null;
            if (avfilter_graph_create_filter(&src, avfilter_get_by_name("buffer"), "in", args, null, _filterGraph) < 0)
                return false;
            if (avfilter_graph_create_filter(&sink, avfilter_get_by_name("buffersink"), "out", null, null, _filterGraph) < 0)
                return false;
            if (avfilter_graph_create_filter(&yadif, avfilter_get_by_name("yadif"), "yadif", "mode=1", null, _filterGraph) < 0)
                return false;
            if (avfilter_link(src, 0, yadif, 0) < 0)
                return false;
            if (avfilter_link(yadif, 0, sink, 0) < 0)
                return false;
            if (avfilter_graph_config(_filterGraph, null) < 0)
                return false;

            _bufSrc = src;
            _bufSink = sink;
            _filtFrame = av_frame_alloc();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void PublishFrame(AVFrame* frame)
    {
        var w = frame->width;
        var h = frame->height;
        if (w <= 0 || h <= 0) return;

        EnsureBuffers(w, h);
        EnsureSws(frame->format, w, h);
        if (_sws == null) return;

        var dst = _bufs[_bufIdx];
        fixed (byte* pDst = dst)
        {
            var srcData = new byte*[4];
            var srcStride = new int[4];
            var dstData = new byte*[4];
            var dstStride = new int[4];
            for (var i = 0; i < 4; i++)
            {
                srcData[i] = (byte*)frame->data[i];
                srcStride[i] = frame->linesize[i];
            }
            dstData[0] = pDst;
            dstStride[0] = w * 4;
            sws_scale(_sws, srcData, srcStride, 0, h, dstData, dstStride);
        }

        lock (FrameLock)
        {
            FrameData = dst;
            FrameWidth = w;
            FrameHeight = h;
            FrameStride = w * 4;
            FrameGen++;
        }
        _bufIdx = (_bufIdx + 1) % _bufs.Length;
    }

    private void DrainAudio()
    {
        while (!_quit && avcodec_receive_frame(_actx, _aframe) >= 0)
        {
            if (_waveProvider != null && _swr != null)
            {
                var outSamples = (int)av_rescale_rnd(_aframe->nb_samples, OutRate, _inRate, AVRounding.Up) + 64;
                EnsureAudioBuffer(outSamples * 4); // stereo * S16

                fixed (byte* pOut = _audioBuf)
                {
                    byte* outPtr = pOut;
                    var n = swr_convert(_swr, &outPtr, outSamples, (byte**)&_aframe->data, _aframe->nb_samples);
                    if (n > 0)
                    {
                        // throttle limitado: nunca bloquear o pump para sempre
                        var waited = 0;
                        while (!_quit && State == DecoderState.Playing && waited < 400 &&
                               _waveProvider.BufferedBytes > _waveProvider.BufferLength / 2)
                        {
                            Thread.Sleep(5);
                            waited += 5;
                        }
                        if (State == DecoderState.Playing)
                            _waveProvider.AddSamples(_audioBuf, 0, n * 4);
                    }
                }
            }
            av_frame_unref(_aframe);
        }
    }

    private void EnsureBuffers(int w, int h)
    {
        var size = w * h * 4;
        if (_bufs.Length == 3 && _bufs[0].Length == size) return;
        _bufs = new byte[3][];
        for (var i = 0; i < 3; i++) _bufs[i] = new byte[size];
        _bufIdx = 0;
    }

    private void EnsureSws(int srcFmt, int w, int h)
    {
        if (_sws != null && _swsSrcFmt == srcFmt) return;
        if (_sws != null) sws_freeContext(_sws);
        _swsSrcFmt = srcFmt;
        _sws = sws_getContext(w, h, (AVPixelFormat)srcFmt, w, h, AVPixelFormat.Bgra,
            SwsFlags.Bilinear, null, null, null);
    }

    private void EnsureAudioBuffer(int size)
    {
        if (_audioBuf.Length < size)
            _audioBuf = new byte[size];
    }

    public void Dispose()
    {
        _quit = true;
        State = DecoderState.Empty;
        if (_thread is { IsAlive: true }) _thread.Join(800);

        try { _wasapi?.Stop(); } catch { }
        _wasapi?.Dispose();
        _wasapi = null;

        if (_vctx != null) { fixed (AVCodecContext** p = &_vctx) avcodec_free_context(p); }
        if (_actx != null) { fixed (AVCodecContext** p = &_actx) avcodec_free_context(p); }
        if (_sws != null) { sws_freeContext(_sws); _sws = null; }
        if (_swr != null) { fixed (SwrContext** p = &_swr) swr_free(p); }
        if (_pkt != null) { fixed (AVPacket** p = &_pkt) av_packet_free(p); }
        if (_vframe != null) { fixed (AVFrame** p = &_vframe) av_frame_free(p); }
        if (_aframe != null) { fixed (AVFrame** p = &_aframe) av_frame_free(p); }
        if (_swFrame != null) { fixed (AVFrame** p = &_swFrame) av_frame_free(p); }
        if (_filtFrame != null) { fixed (AVFrame** p = &_filtFrame) av_frame_free(p); }
        if (_hwDevice != null) { fixed (AVBufferRef** p = &_hwDevice) av_buffer_unref(p); }
        if (_filterGraph != null) { fixed (AVFilterGraph** p = &_filterGraph) avfilter_graph_free(p); }
        if (_fmt != null) { fixed (AVFormatContext** p = &_fmt) avformat_close_input(p); }
    }
}
