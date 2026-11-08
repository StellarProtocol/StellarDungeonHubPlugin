using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Stellar.DungeonHub;

/// <summary>
/// Dungeon Confirm's local-speaker player: a tiny software mixer on winmm <c>waveOut</c>, so the open sound and the
/// countdown ticks play AT THE SAME TIME (a long custom open sound used to swallow every tick).
/// <para><b>Why not PlaySound:</b> winmm PlaySound has ONE voice per process — a new sound cuts the current one. The
/// mixer instead keeps one voice per tag (open / tick / test), sums them into small rotating buffers and feeds them to
/// a <c>waveOut</c> device. A new sound with a tag REPLACES that tag's voice (the next tick cuts the previous tick, a
/// long tick file never stacks); other tags keep playing.</para>
/// <para><b>Format:</b> one fixed device format, 44.1 kHz stereo 16-bit. Clips are converted ONCE, off the main thread,
/// by <see cref="Convert"/> (resample + channel map + volume baked in) and cached by the caller; mixing is then a plain
/// sum + clamp.</para>
/// <para>⚠️ <b>Threads:</b> every waveOut call happens on this class's own thread — never the game's main thread (a
/// main-thread winmm call is what caused the 2026-11-08 growing tick hitch). <see cref="Play"/> / <see cref="Stop"/> /
/// <see cref="IsBusyWith"/> only touch the voice list under a lock (microseconds) and wake the thread. The device is
/// opened lazily on the first sound and closed after a few idle seconds, so no stream is held open between requests
/// and a changed default output device is picked up on the next open.</para>
/// <para><b>Latency:</b> <see cref="BufCount"/> × <see cref="BufMs"/> ms queued (~100 ms). A Stop that leaves nothing
/// playing also <c>waveOutReset</c>s, so "stop all" is immediate rather than draining the queued buffers.</para>
/// </summary>
internal sealed class DcMixer : IDisposable
{
    public const int Rate = 44100, Channels = 2;
    private const int BufCount = 4, BufMs = 25, BufFrames = Rate * BufMs / 1000, BufSamples = BufFrames * Channels;
    private const int IdleCloseMs = 3000;

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx { public ushort FormatTag, Channels; public uint SamplesPerSec, AvgBytesPerSec; public ushort BlockAlign, BitsPerSample, CbSize; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHdr { public IntPtr Data; public uint BufferLength, BytesRecorded; public IntPtr User; public uint Flags, Loops; public IntPtr Next, Reserved; }

    [DllImport("winmm.dll")] private static extern uint waveOutOpen(out IntPtr hwo, uint device, ref WaveFormatEx fmt, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(IntPtr hwo, IntPtr hdr, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(IntPtr hwo, IntPtr hdr, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutWrite(IntPtr hwo, IntPtr hdr, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutReset(IntPtr hwo);
    [DllImport("winmm.dll")] private static extern uint waveOutClose(IntPtr hwo);
    private const uint WaveMapper = 0xFFFFFFFF, CallbackEvent = 0x00050000, WhdrDone = 0x1;
    private static readonly uint HdrSize = (uint)Marshal.SizeOf<WaveHdr>();
    private static readonly int FlagsOffset = (int)Marshal.OffsetOf<WaveHdr>(nameof(WaveHdr.Flags));

    private sealed class Voice { public string Tag = ""; public short[] Clip = Array.Empty<short>(); public int Pos; }

    private readonly object _lock = new();
    private readonly List<Voice> _voices = new();    // guarded by _lock
    private bool _resetReq;                          // guarded by _lock: a Stop emptied the mix → cut queued buffers
    private readonly AutoResetEvent _signal = new(false);   // Play / Stop / Dispose
    private readonly AutoResetEvent _bufDone = new(false);  // winmm CALLBACK_EVENT: a buffer finished
    private readonly Thread _thread;
    private volatile bool _stop;
    private readonly Action<string> _log;

    // Player-thread state only.
    private IntPtr _hwo;
    private readonly IntPtr[] _hdr = new IntPtr[BufCount], _buf = new IntPtr[BufCount];
    private readonly bool[] _inFlight = new bool[BufCount];
    private readonly int[] _acc = new int[BufSamples];
    private readonly short[] _out = new short[BufSamples];
    private long _lastActive;
    private bool _openFailedLogged;

    public DcMixer(Action<string> logFromAnyThread)
    {
        _log = logFromAnyThread;
        _thread = new Thread(Run) { IsBackground = true, Name = "DungeonHub.DcMixer" };
        _thread.Start();
    }

    /// <summary>Starts <paramref name="clip"/> (from <see cref="Convert"/>) under <paramref name="tag"/>, replacing
    /// that tag's current voice. Any thread; no winmm call.</summary>
    public void Play(string tag, short[] clip)
    {
        if (clip.Length == 0 || _stop) return;
        lock (_lock)
        {
            _voices.RemoveAll(v => v.Tag == tag);
            _voices.Add(new Voice { Tag = tag, Clip = clip });
            _resetReq = false;   // something plays again: let the queued buffers run out naturally
        }
        Wake();
    }

    /// <summary>Drops the voice with this tag (null = all). Any thread; no winmm call.</summary>
    public void Stop(string? tag = null)
    {
        lock (_lock)
        {
            if (_voices.RemoveAll(v => tag == null || v.Tag == tag) == 0) return;
            if (_voices.Count == 0) _resetReq = true;
        }
        Wake();
    }

    private void Wake() { try { _signal.Set(); } catch (ObjectDisposedException) { } }   // the thread may be gone (unload)

    /// <summary>True while a voice with this tag is still playing.</summary>
    public bool IsBusyWith(string tag)
    {
        lock (_lock) { foreach (var v in _voices) if (v.Tag == tag) return true; return false; }
    }

    public void Dispose()
    {
        _stop = true;
        lock (_lock) _voices.Clear();
        try { _signal.Set(); } catch (ObjectDisposedException) { }   // the player thread resets + closes the device
    }

    private void Run()
    {
        var waits = new WaitHandle[] { _signal, _bufDone };
        try
        {
            while (!_stop)
            {
                bool reset, any;
                lock (_lock) { reset = _resetReq; _resetReq = false; any = _voices.Count > 0; }
                if (reset && _hwo != IntPtr.Zero) { waveOutReset(_hwo); Array.Clear(_inFlight); }
                if (any && _hwo == IntPtr.Zero && !Open()) { lock (_lock) _voices.Clear(); any = false; }
                if (_hwo != IntPtr.Zero)
                {
                    long now = Environment.TickCount64;
                    for (int i = 0; i < BufCount && any; i++)
                    {
                        if (_inFlight[i] && (Marshal.ReadInt32(_hdr[i], FlagsOffset) & WhdrDone) == 0) continue;
                        any = Mix(i);
                        uint r = waveOutWrite(_hwo, _hdr[i], HdrSize);
                        if (r != 0) { _log($"waveOutWrite failed ({r}); reopening on the next sound"); Close(); break; }
                        _inFlight[i] = true; _lastActive = now;
                    }
                    if (_hwo != IntPtr.Zero && !any && now - _lastActive > IdleCloseMs && AllDone()) Close();
                }
                WaitHandle.WaitAny(waits, 50);
            }
        }
        catch (Exception ex) { _log("mixer thread error: " + ex.Message); }
        finally
        {
            try { Close(); } catch { }
            try { _signal.Dispose(); _bufDone.Dispose(); } catch { }
        }
    }

    // Sums every voice into buffer i (clamped), drops finished voices. Returns whether anything is left to play.
    private bool Mix(int i)
    {
        Array.Clear(_acc);
        lock (_lock)
        {
            for (int v = _voices.Count - 1; v >= 0; v--)
            {
                var vo = _voices[v];
                int n = Math.Min(BufSamples, vo.Clip.Length - vo.Pos);
                for (int k = 0; k < n; k++) _acc[k] += vo.Clip[vo.Pos + k];
                vo.Pos += n;
                if (vo.Pos >= vo.Clip.Length) _voices.RemoveAt(v);
            }
            for (int k = 0; k < BufSamples; k++) _out[k] = (short)Math.Clamp(_acc[k], short.MinValue, short.MaxValue);
            Marshal.Copy(_out, 0, _buf[i], BufSamples);
            return _voices.Count > 0;
        }
    }

    private bool AllDone()
    {
        for (int i = 0; i < BufCount; i++)
            if (_inFlight[i] && (Marshal.ReadInt32(_hdr[i], FlagsOffset) & WhdrDone) == 0) return false;
        return true;
    }

    private bool Open()
    {
        var fmt = new WaveFormatEx { FormatTag = 1, Channels = Channels, SamplesPerSec = Rate, BitsPerSample = 16,
            BlockAlign = Channels * 2, AvgBytesPerSec = Rate * Channels * 2 };
        uint r = waveOutOpen(out var hwo, WaveMapper, ref fmt, _bufDone.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CallbackEvent);
        if (r != 0)
        {
            if (!_openFailedLogged) _log($"waveOutOpen failed ({r}) — no output device?");
            _openFailedLogged = true;
            return false;
        }
        _openFailedLogged = false;
        _hwo = hwo;
        for (int i = 0; i < BufCount; i++)
        {
            _buf[i] = Marshal.AllocHGlobal(BufSamples * 2);   // unmanaged: winmm reads it after waveOutWrite returns
            _hdr[i] = Marshal.AllocHGlobal((int)HdrSize);
            Marshal.StructureToPtr(new WaveHdr { Data = _buf[i], BufferLength = (uint)(BufSamples * 2) }, _hdr[i], false);
            waveOutPrepareHeader(_hwo, _hdr[i], HdrSize);   // prepared once per open, re-written many times
            _inFlight[i] = false;
        }
        _lastActive = Environment.TickCount64;
        return true;
    }

    // Reset returns every queued buffer (marked done) so they can be unprepared and freed.
    private void Close()
    {
        if (_hwo == IntPtr.Zero) return;
        waveOutReset(_hwo);
        for (int i = 0; i < BufCount; i++)
        {
            if (_hdr[i] != IntPtr.Zero) { waveOutUnprepareHeader(_hwo, _hdr[i], HdrSize); Marshal.FreeHGlobal(_hdr[i]); _hdr[i] = IntPtr.Zero; }
            if (_buf[i] != IntPtr.Zero) { Marshal.FreeHGlobal(_buf[i]); _buf[i] = IntPtr.Zero; }
            _inFlight[i] = false;
        }
        waveOutClose(_hwo);
        _hwo = IntPtr.Zero;
    }

    /// <summary>Converts decoded audio to the mixer format (44.1 kHz stereo, linear-interpolated resample; mono → both
    /// sides, &gt; 2 channels → the first two) with <paramref name="volume"/> (0..1) baked in. Off the main thread: a
    /// long custom file is tens of MB of work.</summary>
    public static short[] Convert(DecodedAudio a, float volume)
    {
        float v = Math.Clamp(volume, 0f, 1f);
        int ch = Math.Max(1, a.Channels), inFrames = a.Pcm.Length / ch;
        if (inFrames == 0 || a.Rate <= 0) return Array.Empty<short>();
        long outFrames = (long)inFrames * Rate / a.Rate;
        var o = new short[outFrames * Channels];
        double step = (double)a.Rate / Rate;
        for (long f = 0; f < outFrames; f++)
        {
            double src = f * step;
            int i0 = (int)src, i1 = Math.Min(i0 + 1, inFrames - 1);
            float t = (float)(src - i0);
            for (int c = 0; c < Channels; c++)
            {
                int sc = Math.Min(c, ch - 1);
                float s = a.Pcm[i0 * ch + sc] * (1 - t) + a.Pcm[i1 * ch + sc] * t;
                o[f * Channels + c] = (short)Math.Clamp(s * v, short.MinValue, short.MaxValue);
            }
        }
        return o;
    }
}
