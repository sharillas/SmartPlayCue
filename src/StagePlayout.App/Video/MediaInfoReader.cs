using System.IO;
using System.Runtime.InteropServices;
using Flyleaf.FFmpeg;
using static Flyleaf.FFmpeg.Raw;

namespace StagePlayout.App.Video;

public readonly record struct MediaInfo(
    int Width, int Height, string VideoCodec, string AudioCodec,
    long DurationTicks, long FileSizeBytes, double Fps);

/// <summary>
/// Leitura leve de metadata (só container/headers — sem find_stream_info nem
/// decode). Rápida o suficiente para listas com 100+ clips.
/// </summary>
public static class MediaInfoReader
{
    static MediaInfoReader()
    {
        // DLLs nativas do FFmpeg ficam em .\FFmpeg (resolver próprio)
        FFmpegNatives.Ensure();
    }

    public static unsafe MediaInfo? Read(string path)
    {
        AVFormatContext* fmt = null;
        try
        {
            if (avformat_open_input(&fmt, path, null, null) < 0)
                return null;

            var w = 0;
            var h = 0;
            double fps = 0;
            var vc = "";
            var ac = "";

            for (uint i = 0; i < fmt->nb_streams; i++)
            {
                var st = fmt->streams[i];
                var par = st->codecpar;
                if (par->codec_type == AVMediaType.Video && vc.Length == 0)
                {
                    vc = avcodec_get_name(par->codec_id);
                    w = par->width;
                    h = par->height;
                    fps = av_q2d(st->avg_frame_rate);
                }
                else if (par->codec_type == AVMediaType.Audio && ac.Length == 0)
                {
                    ac = avcodec_get_name(par->codec_id);
                }
            }

            var dur = fmt->duration > 0 ? fmt->duration * 10 : 0L;
            long size = 0;
            try { size = new FileInfo(path).Length; } catch { /* ignore */ }

            avformat_close_input(&fmt);
            return new MediaInfo(w, h, vc, ac, dur, size, fps);
        }
        catch
        {
            if (fmt != null) avformat_close_input(&fmt);
            return null;
        }
    }

}
