using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Stellar.DungeonHub;

/// <summary>
/// Local-speaker sound player for mechanic alerts / voice callouts (NOT team voice).
/// <para><b>API choice:</b> winmm <c>PlaySound(SND_MEMORY | SND_SYNC | SND_NODEFAULT)</c> on a dedicated background
/// thread, fed an in-memory 16-bit PCM WAV. No Unity/IL2CPP objects (an <c>AudioClip</c>/<c>AudioSource</c> route
/// would need interop arrays + a GameObject + main-thread calls), no COM, no device handles to leak; it plays through
/// the default output in shared mode alongside the game, focus or not. SYNC on our own thread = the game thread never
/// waits, and sounds naturally never overlap (one at a time).</para>
/// <para><b>Queue policy:</b> at most <see cref="MaxPending"/> waiting sounds; a new request beyond that DROPS THE
/// OLDEST waiting one (a stale callout is useless), and plays start at least <see cref="MinGapMs"/> apart.</para>
/// <para><b>Files:</b> decoded once with Voice Inject's <c>AudioDecoder</c> (WAV parser / Media Foundation — MF must
/// run on a thread we own, which this one is) and cached by path; a changed file (size or write time) is re-decoded.
/// Volume is applied when building the WAV image, so the cache holds the untouched PCM.</para>
/// <para><b>Tagged sounds</b> (Dungeon Confirm, its own instance): <see cref="PlayWav"/> takes a ready WAV image (volume
/// already baked by the caller, built once per volume), <see cref="IsBusyWith"/> asks whether a tagged sound is queued
/// or playing, and <see cref="Stop"/> drops queued ones and cuts the current one. ⚠️ winmm plays ONE sound per process:
/// a PlaySound from another instance's thread interrupts this one's (last-started wins).</para>
/// </summary>
internal sealed class MechAudioPlayer : IDisposable
{
    [DllImport("winmm.dll", SetLastError = true)]
    private static extern bool PlaySound(byte[]? pszSound, IntPtr hmod, uint fdwSound);
    private const uint SndSync = 0x0, SndNoDefault = 0x2, SndMemory = 0x4;

    public int MaxPending = 2;
    public int MinGapMs = 1000;

    private sealed class Req { public string? Path, Tag; public DecodedAudio? Pcm; public byte[]? Wav; public float Volume; public bool DecodeOnly; }
    private sealed class Cached { public DecodedAudio? Audio; public string? Error; public long Size; public DateTime Mtime; }

    private readonly LinkedList<Req> _queue = new();
    private readonly object _lock = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Dictionary<string, Cached> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Thread _thread;
    private volatile bool _stop;
    private long _lastStart;
    private Req? _current;          // dequeued and about to play / playing (guarded by _lock)
    private bool _cutCurrent;       // Stop() hit _current before its PlaySound began (guarded by _lock)
    private readonly Action<string> _log;

    public string LastStatus { get; private set; } = "";

    public MechAudioPlayer(Action<string> logFromAnyThread)
    {
        _log = logFromAnyThread;
        _thread = new Thread(Run) { IsBackground = true, Name = "DungeonHub.MechAudio" };
        _thread.Start();
    }

    public void PlayFile(string path, float volume) => Enqueue(new Req { Path = path, Volume = volume });
    public void PlayPcm(DecodedAudio pcm, float volume) => Enqueue(new Req { Pcm = pcm, Volume = volume });
    public void PlayWav(byte[] wav, string tag) => Enqueue(new Req { Wav = wav, Tag = tag });

    /// <summary>True while a sound with this tag waits in the queue or is playing.</summary>
    public bool IsBusyWith(string tag)
    {
        lock (_lock)
        {
            if (_current?.Tag == tag) return true;
            foreach (var q in _queue) if (q.Tag == tag) return true;
            return false;
        }
    }

    /// <summary>Drops queued sounds with this tag (null = all) and cuts the current one if it matches. Any thread.</summary>
    public void Stop(string? tag = null)
    {
        bool cut;
        lock (_lock)
        {
            for (var n = _queue.First; n != null;) { var next = n.Next; if (tag == null || n.Value.Tag == tag) _queue.Remove(n); n = next; }
            cut = _current != null && (tag == null || _current.Tag == tag);
            if (cut) _cutCurrent = true;   // covers the gap between dequeue and the PlaySound call
        }
        if (cut) try { PlaySound(null, IntPtr.Zero, 0); } catch { }
    }

    public void Preload(string path) => Enqueue(new Req { Path = path, DecodeOnly = true }, front: true);

    private void Enqueue(Req r, bool front = false)
    {
        lock (_lock)
        {
            if (front) _queue.AddFirst(r); else _queue.AddLast(r);
            int playable = 0;
            foreach (var q in _queue) if (!q.DecodeOnly) playable++;
            for (var n = _queue.First; playable > MaxPending && n != null;)
            {
                var next = n.Next;
                if (!n.Value.DecodeOnly) { _queue.Remove(n); playable--; }
                n = next;
            }
        }
        _signal.Set();
    }

    public void Dispose()
    {
        _stop = true;
        try { PlaySound(null, IntPtr.Zero, 0); } catch { }   // cut a sound in progress
        _signal.Set();
    }

    private void Run()
    {
        while (!_stop)
        {
            Req? r = null;
            lock (_lock) { if (_queue.First != null) { r = _queue.First.Value; _queue.RemoveFirst(); _current = r; _cutCurrent = false; } }
            if (r == null) { _signal.WaitOne(500); continue; }
            try
            {
                byte[]? wav = r.Wav;
                if (wav == null)
                {
                    var audio = r.Pcm ?? (r.Path != null ? Get(r.Path) : null);
                    if (r.DecodeOnly || audio == null || audio.Pcm.Length == 0) continue;
                    wav = BuildWav(audio, r.Volume);
                }
                long wait = MinGapMs - (Environment.TickCount64 - _lastStart);
                if (wait > 0) Thread.Sleep((int)wait);
                if (_stop) break;
                lock (_lock) if (_cutCurrent) continue;   // stopped while waiting
                _lastStart = Environment.TickCount64;
                PlaySound(wav, IntPtr.Zero, SndMemory | SndSync | SndNoDefault);
            }
            catch (Exception ex) { LastStatus = "playback error: " + ex.Message; _log(LastStatus); }
            finally { lock (_lock) _current = null; }
        }
        try { _signal.Dispose(); } catch { }
    }

    // Decode-once cache (by path; re-decoded when the file's size or write time changed). Runs on this thread.
    private DecodedAudio? Get(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) { LastStatus = "file not found: " + path; return null; }
            if (_cache.TryGetValue(path, out var c) && c.Size == fi.Length && c.Mtime == fi.LastWriteTimeUtc)
                return c.Audio;
            c = new Cached { Size = fi.Length, Mtime = fi.LastWriteTimeUtc };
            try
            {
                c.Audio = AudioDecoder.Decode(path);
                LastStatus = $"loaded {Path.GetFileName(path)} ({c.Audio.Seconds:0.0} s, {c.Audio.Decoder})";
            }
            catch (Exception ex) { c.Error = ex.Message; LastStatus = $"decode failed: {Path.GetFileName(path)}: {ex.Message}"; }
            _cache[path] = c;
            _log(LastStatus);
            return c.Audio;
        }
        catch (Exception ex) { LastStatus = "file error: " + ex.Message; return null; }
    }

    // RIFF/WAVE image (PCM 16-bit) with the volume baked in.
    public static byte[] BuildWav(DecodedAudio a, float volume)
    {
        float v = Math.Clamp(volume, 0f, 1f);
        int dataLen = a.Pcm.Length * 2;
        var b = new byte[44 + dataLen];
        void W32(int o, int x) => BitConverter.TryWriteBytes(new Span<byte>(b, o, 4), x);
        void W16(int o, short x) => BitConverter.TryWriteBytes(new Span<byte>(b, o, 2), x);
        "RIFF"u8.CopyTo(new Span<byte>(b, 0, 4)); W32(4, 36 + dataLen); "WAVE"u8.CopyTo(new Span<byte>(b, 8, 4));
        "fmt "u8.CopyTo(new Span<byte>(b, 12, 4)); W32(16, 16); W16(20, 1); W16(22, (short)a.Channels);
        W32(24, a.Rate); W32(28, a.Rate * a.Channels * 2); W16(32, (short)(a.Channels * 2)); W16(34, 16);
        "data"u8.CopyTo(new Span<byte>(b, 36, 4)); W32(40, dataLen);
        for (int i = 0; i < a.Pcm.Length; i++) W16(44 + 2 * i, (short)(a.Pcm[i] * v));
        return b;
    }

    // Default alert chime: two short rising tones (880 → 1320 Hz), 22.05 kHz mono, soft attack/decay envelope.
    public static DecodedAudio Chime()
    {
        const int rate = 22050;
        int n = (int)(rate * 0.34);
        var pcm = new short[n];
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / rate, local = t < 0.15 ? t : t - 0.17;
            if (t >= 0.15 && t < 0.17) continue;                              // tiny gap between the two notes
            double f = t < 0.15 ? 880 : 1320, len = t < 0.15 ? 0.15 : 0.17;
            double env = Math.Min(1, local / 0.01) * Math.Max(0, 1 - local / len);
            pcm[i] = (short)(Math.Sin(2 * Math.PI * f * t) * env * 0.6 * short.MaxValue);
        }
        return new DecodedAudio(pcm, rate, 1, "chime");
    }
}
