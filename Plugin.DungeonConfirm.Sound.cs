using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Stellar.DungeonHub;

// Dungeon Confirm — our own sounds, replacing the game's MatchAudio cue (no longer played, .Lua.cs). Defaults = two
// user-approved embedded WAVs (44.1 kHz stereo 16-bit): dc_enter (3.05 s boom) when the window opens for a REAL request,
// and dc_tick (0.97 s beep) on each countdown second 10..1 while I still have to answer. Either can be swapped for the
// user's own .wav / .mp3 (.SoundFiles.cs). Previews sound exactly like a real request (open sound + the last-10 s
// ticks, same toggles / files / volume). Replace OFF = silent everywhere: the sounds belong to the replacement window.
//
// Playback = DcMixer (winmm waveOut + a small software mixer on its own thread). winmm ignores the game's mixer, so it is
// heard with the game muted or in the background; that's the point, don't route it through Z.AudioMgr. One voice per
// tag: a new play REPLACES that tag's voice (a long custom tick file never stacks under the next second's tick), while
// the other tags keep playing — the open sound and the ticks MIX. (The first version used PlaySound, which has one
// voice per process: a long custom open sound swallowed every tick.)
//
// The open sound plays until the window closes or I answer (Confirm / Decline / an end state) — a long custom file is
// cut then (DcSoundStopAll). Ticks play on top of it. The settings ▶ preview (tag dc_test) is a play/stop toggle and
// stops on settings close, on a file change for its row, and with Replace OFF (DcSoundPreview).
//
// Volume: the decoded PCM is kept; the mixer clip is rebuilt with the volume baked in and converted to the mixer format
// (DcMixer.Convert) the first time a sound is needed after the volume / file changed — one clip per slot (open, tick),
// never per play. Volume 0 = nothing is played at all.
//
// Threads: DcWavFor(mayDecode: false) is the main-thread path — CACHE ONLY, it never decodes or builds a clip (not even
// the embedded defaults). Anything missing goes to a short background thread (DcPlay), which only starts the sound if
// no Stop for that tag happened meanwhile (per-tag generation counters). DcWarmAll pre-builds both slots off-thread at
// startup and after every volume / file change, so a real play is always a cache hit. DcMixer.Play/Stop only touch its
// voice list: winmm is called from its own thread alone (a main-thread winmm call blocked until the previous tick
// finished — the 2026-11-08 growing tick hitch).
//
// Tick timing: a 50 ms Every (main thread, arithmetic only) takes s = ceil(remaining) — exactly the number the window's
// timer shows (DcTimerText) — and ticks when s is 1..10 and lower than the last ticked second.
public sealed partial class Plugin
{
    private const string DcTagEnter = "dc_enter", DcTagTick = "dc_tick", DcTagTest = "dc_test";

    private DcMixer _dcAudio = null!;
    private IDisposable? _dcSoundTimer;
    private bool _dcSoundOpen, _dcSoundTicks;
    private int  _dcSoundVolume;              // 0..100
    private string _dcOpenFile = "", _dcTickFile = "";   // "" = Default (embedded)
    private int  _dcLastTick = int.MaxValue;  // last countdown second ticked for the request on screen
    private volatile bool _dcSoundDisposed, _dcSoundBroken;   // Broken: the embedded WAVs failed to load
    private volatile int _dcGenEnter, _dcGenTick, _dcGenTest;  // bumped by a Stop: a pending background play is dropped

    private readonly object _dcWavLock = new();
    private volatile DecodedAudio? _dcPcmEnter, _dcPcmTick;   // written once off-thread; the main thread only peeks
    private int _dcWarming; private volatile bool _dcWarmAgain;    // DcWarmAll coalescing

    private sealed class DcWavSlot { public DecodedAudio? Pcm; public int Vol = -1; public short[]? Wav; }
    private readonly DcWavSlot _dcSlotOpen = new(), _dcSlotTick = new();
    private static readonly short[] DcPending = new short[0];   // "needs a background build" sentinel (own instance: compared by reference)

    private void InitDcSound()
    {
        _dcSoundOpen   = _cfg.Get<bool>("dungeon_confirm_sound_open", true);
        _dcSoundTicks  = _cfg.Get<bool>("dungeon_confirm_sound_ticks", true);
        _dcSoundVolume = Math.Clamp(_cfg.Get<int>("dungeon_confirm_sound_volume", 80), 0, 100);
        _dcOpenFile    = _cfg.Get<string>("dungeon_confirm_sound_open_file", "") ?? "";
        _dcTickFile    = _cfg.Get<string>("dungeon_confirm_sound_tick_file", "") ?? "";
        _dcAudio = new DcMixer(s => _services.Framework.Post(() => _services.Log.Info("[DungeonConfirm][Sound] " + s)));
        _dcSoundTimer = _services.Framework.Every(TimeSpan.FromMilliseconds(50), DcSoundTick);
        DcRefreshSoundList(force: true);   // README + folder (.SoundFiles.cs)
        DcWarmAll();                       // decode + bake both slots now, so the first open / tick plays at once
    }

    private void DisposeDcSound()
    {
        _dcSoundDisposed = true;
        _dcSoundTimer?.Dispose();
        DcSoundStopAll();
        _dcAudio?.Dispose();
    }

    private string DcSoundFile(bool tick) => tick ? _dcTickFile : _dcOpenFile;

    // Called once per request, on its first show (DcApplySnap — never on re-polls or re-centres) and once per Preview
    // open (DcStartPreview).
    private void DcSoundOnOpen()
    {
        _dcLastTick = int.MaxValue;
        if (!_dcSoundOpen || !_dcReplace) return;
        DcPlay(false, DcTagEnter);
    }

    private void DcSoundStopTicks() { _dcGenTick++; _dcAudio?.Stop(DcTagTick); }
    private void DcSoundStopAll()   { _dcGenEnter++; _dcGenTick++; _dcGenTest++; _dcAudio?.Stop(); }

    // I still have to act: team = a member (never the leader) who hasn't confirmed/declined; match = until I answer.
    // SOUND ONLY (ticks): a Preview counts too — its fake "me" is an unanswered member and its countdown runs, so it
    // ticks the last 10 s like a real request; its buttons only close (DcHide stops everything).
    private bool DcNeedsMyAnswer => _dc != null && _dcReplace && _dcEnd == DcEnd.None && !_dcConfirmed &&
        (_dcShow5 || _dcShowRaid) && (_dc.Match ? !_dcMatchDeclined : !_dc.IAmLeader) && _dc.Me is not { Ready: not 0 };

    private void DcSoundTick()
    {
        if (_dc == null) return;
        if (!DcNeedsMyAnswer) { if (_dcAudio.IsBusyWith(DcTagTick)) DcSoundStopTicks(); return; }
        int s = (int)Math.Ceiling(DcRemaining);
        if (s < 1 || s > 10 || s >= _dcLastTick) return;
        _dcLastTick = s;                                       // this second is spent, played or skipped
        if (!_dcSoundTicks) return;                            // mixes over a still-playing open sound
        DcPlay(true, DcTagTick);
    }

    // Plays the open (tick = false) or tick sound under `tag`. Ready image → now; otherwise decode / bake on a short
    // background thread and start it there, unless a Stop for the tag came first. Returns true when it went to that
    // thread (the preview's "loading" state, DcSoundPreview); a test-tag load clears _dcTestPendingGen when it ends.
    private bool DcPlay(bool tick, string tag)
    {
        var wav = DcWavFor(tick, mayDecode: false);
        if (wav == null) return false;
        if (wav != DcPending) { DcStart(tag, wav); return false; }
        int gen = DcGen(tag);
        new Thread(() =>
        {
            try
            {
                var w = DcWavFor(tick, mayDecode: true);
                if (w != null && w != DcPending && gen == DcGen(tag) && !_dcSoundDisposed) DcStart(tag, w);
            }
            catch (Exception ex) { DcSoundLog("play failed: " + ex.Message); }
            // Started (the mixer now reports it busy) or gave up: no longer loading. Only THIS load's mark is cleared,
            // so a finished stale load can't flip a newer click's ■ back to ▶.
            finally { if (tag == DcTagTest) Interlocked.CompareExchange(ref _dcTestPendingGen, -1, gen); }
        }) { IsBackground = true, Name = "DungeonHub.DcSoundLoad" }.Start();
        return true;
    }

    private int DcGen(string tag) => tag == DcTagEnter ? _dcGenEnter : tag == DcTagTick ? _dcGenTick : _dcGenTest;

    // Replaces the tag's previous voice (long files never stack); other tags keep playing.
    private void DcStart(string tag, short[] clip) => _dcAudio.Play(tag, clip);

    // Decode + bake both slots (chosen file or default, current volume) in the background — startup, file pick, volume
    // change — so playback finds them cached. One worker at a time; a request while it runs makes it go round again
    // (a volume drag = many calls, a handful of builds).
    private void DcWarmAll()
    {
        _dcWarmAgain = true;
        if (Interlocked.Exchange(ref _dcWarming, 1) == 1) return;
        new Thread(() =>
        {
            try
            {
                while (_dcWarmAgain && !_dcSoundDisposed)
                {
                    _dcWarmAgain = false;
                    try { DcWavFor(false, mayDecode: true); DcWavFor(true, mayDecode: true); } catch { }
                }
            }
            finally { Volatile.Write(ref _dcWarming, 0); }
            if (_dcWarmAgain && !_dcSoundDisposed) DcWarmAll();   // a request slipped in after the last round
        }) { IsBackground = true, Name = "DungeonHub.DcSoundWarm" }.Start();
    }

    // The WAV image for a slot at the current volume. null = nothing to play (volume 0 / defaults broken); DcPending =
    // mayDecode was false and a decode or a big image build is needed. A custom file that is missing or fails to decode
    // falls back to the slot's default sound.
    private short[]? DcWavFor(bool tick, bool mayDecode)
    {
        int vol = _dcSoundVolume;
        if (vol <= 0) return null;
        string name = DcSoundFile(tick);
        DecodedAudio? pcm = null;
        if (name.Length > 0 && !DcPeekPcm(name, out pcm))
        {
            if (!mayDecode) return DcPending;
            pcm = DcDecodeCustom(name);
        }
        if (pcm == null)
        {
            if (_dcSoundBroken) return null;
            pcm = tick ? _dcPcmTick : _dcPcmEnter;            // peek: the main thread never loads the embedded WAV
            if (pcm == null && !mayDecode) return DcPending;
            pcm ??= DcDefaultPcm(tick);
            if (pcm == null) return null;
        }
        var slot = tick ? _dcSlotTick : _dcSlotOpen;
        lock (slot) if (slot.Pcm == pcm && slot.Vol == vol && slot.Wav != null) return slot.Wav;
        if (!mayDecode) return DcPending;                     // never bake on the main thread
        var wav = DcMixer.Convert(pcm, vol / 100f);
        lock (slot) { slot.Pcm = pcm; slot.Vol = vol; slot.Wav = wav; }
        return wav;
    }

    private DecodedAudio? DcDefaultPcm(bool tick)
    {
        if (_dcSoundBroken) return null;
        var have = tick ? _dcPcmTick : _dcPcmEnter;
        if (have != null) return have;
        try
        {
            // Decoded OUTSIDE _dcWavLock (the main thread takes it in DcSoundProblem / DcPeekPcm); two background
            // threads racing here both decode the same bytes, and the first stored copy wins.
            var pcm = DcLoadWav(tick ? "dc_tick.wav" : "dc_enter.wav");
            lock (_dcWavLock) return tick ? _dcPcmTick ??= pcm : _dcPcmEnter ??= pcm;
        }
        catch (Exception ex)
        {
            if (!_dcSoundBroken) DcSoundLog("default sound failed to load: " + ex.Message);
            _dcSoundBroken = true;   // logged once; no retry on every tick
            return null;
        }
    }

    private static DecodedAudio DcLoadWav(string file)
    {
        using var s = typeof(Plugin).Assembly.GetManifestResourceStream("Stellar.DungeonHub." + file)
            ?? throw new FileNotFoundException("embedded resource missing", file);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return AudioDecoder.DecodeWavBytes(ms.ToArray());
    }

    // ── settings hooks (.DungeonConfirm.Settings.cs) ─────────────────────────────────────────────────────────────
    private void SetDcSoundBool(string key, bool v, Action<bool> apply) { apply(v); _cfg.Set<bool>(key, v); _cfg.Save(); }

    private void SetDcSoundVolume(float v)
    {
        _dcSoundVolume = (int)Math.Round(Math.Clamp(v, 0f, 100f));
        _cfg.Set<int>("dungeon_confirm_sound_volume", _dcSoundVolume); _cfg.Save();
        DcWarmAll();   // re-bake both images at the new volume off-thread (the next play would otherwise go the slow path)
    }

    // ▶ next to a dropdown: that one sound, once, at the current volume (toggles ignored — it's an explicit preview).
    // A play/stop toggle (user-approved "Option A", 2026-11-13): while that row's preview plays — including the async
    // load before playback starts — its button shows ■ on the accent, and a click stops it. Both rows share the one
    // DcTagTest voice, so ▶ on the OTHER row switches the preview to that sound (_dcTestTick records which row owns it).
    // "Playing" = loading (_dcTestPendingGen is the current _dcGenTest) or the mixer still has the voice (IsBusyWith,
    // lock-only). Any stop bumps _dcGenTest, which also ends "loading" — so DcSoundStopAll (Replace OFF, window hide,
    // answer) resets the button with no extra state. It drops back to ▶ by itself when the sound ends.
    private bool _dcTestTick;
    private int _dcTestPendingGen = -1;

    private bool DcPreviewPlaying(bool tick) =>
        _dcTestTick == tick && (Volatile.Read(ref _dcTestPendingGen) == _dcGenTest || _dcAudio?.IsBusyWith(DcTagTest) == true);

    private void DcSoundPreview(bool tick)
    {
        if (!_dcReplace) return;                       // the button is inert when OFF too
        if (DcPreviewPlaying(tick)) { DcSoundStopPreview(); return; }
        _dcGenTest++;                                  // the other row's pending load (if any) must not start after this one
        _dcTestTick = tick;
        Volatile.Write(ref _dcTestPendingGen, _dcGenTest);   // set BEFORE DcPlay, so its loader can't clear it first
        if (!DcPlay(tick, DcTagTest)) Interlocked.CompareExchange(ref _dcTestPendingGen, -1, _dcGenTest);   // started now, or nothing to play
    }

    // Stops the settings preview only (open sound / ticks of a live request keep going). Settings close, file change, ■.
    private void DcSoundStopPreview() { _dcGenTest++; _dcAudio?.Stop(DcTagTest); }

    // Dropdown options: "Default", every listed file, and the saved choice even when it is gone (so the caption still
    // names it while the yellow note explains). Rebuilt only when the list or the choice changed — the same instance
    // is handed back on every poll otherwise.
    private IReadOnlyList<string>? _dcOptsOpen, _dcOptsTick;
    private (int ver, string cur) _dcOptsOpenKey = (-1, ""), _dcOptsTickKey = (-1, "");

    private IReadOnlyList<string> DcSoundOptions(bool tick)
    {
        DcRefreshSoundList(force: false);
        string cur = DcSoundFile(tick);
        var key = (_dcSoundListVer, cur);
        var have = tick ? _dcOptsTick : _dcOptsOpen;
        // [0] = the localized "Default": a language switch rebuilds the list too (L returns the catalog's own instance)
        if (have != null && (tick ? _dcOptsTickKey : _dcOptsOpenKey) == key && ReferenceEquals(have[0], L("dc.set.default"))) return have;
        var opts = new List<string>(_dcSoundNames.Count + 2) { L("dc.set.default") };
        opts.AddRange(_dcSoundNames);
        if (cur.Length > 0 && !DcSoundListed(cur)) opts.Add(cur);
        if (tick) { _dcOptsTick = opts; _dcOptsTickKey = key; } else { _dcOptsOpen = opts; _dcOptsOpenKey = key; }
        return opts;
    }

    private int DcSoundSelected(bool tick)
    {
        string cur = DcSoundFile(tick);
        if (cur.Length == 0) return 0;
        var opts = DcSoundOptions(tick);
        for (int i = 1; i < opts.Count; i++) if (string.Equals(opts[i], cur, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    private void DcSoundPick(bool tick, int index)
    {
        var opts = DcSoundOptions(tick);
        string name = index <= 0 || index >= opts.Count ? "" : opts[index];
        if (_dcTestTick == tick) DcSoundStopPreview();   // that row's preview was the old file
        if (tick) { _dcTickFile = name; _cfg.Set<string>("dungeon_confirm_sound_tick_file", name); }
        else      { _dcOpenFile = name; _cfg.Set<string>("dungeon_confirm_sound_open_file", name); }
        _cfg.Save();
        DcWarmAll();    // decode now: a broken file shows its note right away, a good one plays instantly later
    }
}
