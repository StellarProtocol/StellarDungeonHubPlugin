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
// Playback = a dedicated MechAudioPlayer (winmm PlaySound, in-memory WAV, own background thread — Mechanic-Callouts.md
// "Local audio"). winmm ignores the game's mixer, so it is heard with the game muted or in the background; that's the
// point, don't route it through Z.AudioMgr. MinGapMs = 0, and every play first STOPS the previous sound with the same
// tag (DcStart): a long custom tick file never stacks under the next second's tick.
//
// The open sound plays until the window closes or I answer (Confirm / Decline / an end state) — a long custom file is
// cut then (DcSoundStopAll). A tick that is due while the open sound still plays is SKIPPED, not delayed.
//
// Volume: the decoded PCM is kept; the WAV image is rebuilt with the volume baked into the samples
// (MechAudioPlayer.BuildWav) the first time a sound is needed after the volume / file changed — one image per slot
// (open, tick), never per play. Volume 0 = nothing is played at all.
//
// Threads: DcWavFor(mayDecode: false) is the main-thread path (cache only, small images built inline); anything that
// needs a decode or a big image build goes to a short background thread (DcPlay), which only starts the sound if no
// Stop for that tag happened meanwhile (per-tag generation counters).
//
// Tick timing: a 50 ms Every (main thread, arithmetic only) takes s = ceil(remaining) — exactly the number the window's
// timer shows (DcTimerText) — and ticks when s is 1..10 and lower than the last ticked second.
public sealed partial class Plugin
{
    private const string DcTagEnter = "dc_enter", DcTagTick = "dc_tick", DcTagTest = "dc_test";
    private const int DcInlineBuildMax = 48000 * 2 * 3;   // ≤ ~3 s of 48 kHz stereo: cheap enough to bake on the main thread

    private MechAudioPlayer _dcAudio = null!;
    private IDisposable? _dcSoundTimer;
    private bool _dcSoundOpen, _dcSoundTicks;
    private int  _dcSoundVolume;              // 0..100
    private string _dcOpenFile = "", _dcTickFile = "";   // "" = Default (embedded)
    private int  _dcLastTick = int.MaxValue;  // last countdown second ticked for the request on screen
    private volatile bool _dcSoundDisposed, _dcSoundBroken;   // Broken: the embedded WAVs failed to load
    private volatile int _dcGenEnter, _dcGenTick, _dcGenTest;  // bumped by a Stop: a pending background play is dropped

    private readonly object _dcWavLock = new();
    private DecodedAudio? _dcPcmEnter, _dcPcmTick;

    private sealed class DcWavSlot { public DecodedAudio? Pcm; public int Vol = -1; public byte[]? Wav; }
    private readonly DcWavSlot _dcSlotOpen = new(), _dcSlotTick = new();
    private static readonly byte[] DcPending = Array.Empty<byte>();   // "needs a background build" sentinel

    private void InitDcSound()
    {
        _dcSoundOpen   = _cfg.Get<bool>("dungeon_confirm_sound_open", true);
        _dcSoundTicks  = _cfg.Get<bool>("dungeon_confirm_sound_ticks", true);
        _dcSoundVolume = Math.Clamp(_cfg.Get<int>("dungeon_confirm_sound_volume", 80), 0, 100);
        _dcOpenFile    = _cfg.Get<string>("dungeon_confirm_sound_open_file", "") ?? "";
        _dcTickFile    = _cfg.Get<string>("dungeon_confirm_sound_tick_file", "") ?? "";
        _dcAudio = new MechAudioPlayer(s => _services.Framework.Post(() => _services.Log.Info("[DungeonConfirm][Sound] " + s)))
            { MinGapMs = 0, MaxPending = 4 };
        _dcSoundTimer = _services.Framework.Every(TimeSpan.FromMilliseconds(50), DcSoundTick);
        DcRefreshSoundList(force: true);   // README + folder (.SoundFiles.cs)
        DcWarm(false); DcWarm(true);       // decode a chosen custom file now, so the first open plays at once
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
        if (_dcSoundOpen && _dcReplace) DcPlay(false, DcTagEnter);
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
        if (!_dcSoundTicks || _dcAudio.IsBusyWith(DcTagEnter)) return;   // open sound still going → skip
        DcPlay(true, DcTagTick);
    }

    // Plays the open (tick = false) or tick sound under `tag`. Ready image → now; otherwise decode / bake on a short
    // background thread and start it there, unless a Stop for the tag came first.
    private void DcPlay(bool tick, string tag)
    {
        var wav = DcWavFor(tick, mayDecode: false);
        if (wav == null) return;
        if (wav != DcPending) { DcStart(tag, wav); return; }
        int gen = DcGen(tag);
        new Thread(() =>
        {
            try
            {
                var w = DcWavFor(tick, mayDecode: true);
                if (w != null && w != DcPending && gen == DcGen(tag) && !_dcSoundDisposed) DcStart(tag, w);
            }
            catch (Exception ex) { DcSoundLog("play failed: " + ex.Message); }
        }) { IsBackground = true, Name = "DungeonHub.DcSoundLoad" }.Start();
    }

    private int DcGen(string tag) => tag == DcTagEnter ? _dcGenEnter : tag == DcTagTick ? _dcGenTick : _dcGenTest;

    private void DcStart(string tag, byte[] wav)
    {
        _dcAudio.Stop(tag);   // the previous sound of this kind ends first — long files never stack
        _dcAudio.PlayWav(wav, tag);
    }

    // Decode a chosen custom file in the background (settings pick / startup), so playback finds it cached.
    private void DcWarm(bool tick)
    {
        if (DcSoundFile(tick).Length == 0) return;
        new Thread(() => { try { DcWavFor(tick, mayDecode: true); } catch { } })
            { IsBackground = true, Name = "DungeonHub.DcSoundWarm" }.Start();
    }

    // The WAV image for a slot at the current volume. null = nothing to play (volume 0 / defaults broken); DcPending =
    // mayDecode was false and a decode or a big image build is needed. A custom file that is missing or fails to decode
    // falls back to the slot's default sound.
    private byte[]? DcWavFor(bool tick, bool mayDecode)
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
        pcm ??= DcDefaultPcm(tick);
        if (pcm == null) return null;
        var slot = tick ? _dcSlotTick : _dcSlotOpen;
        lock (slot) if (slot.Pcm == pcm && slot.Vol == vol && slot.Wav != null) return slot.Wav;
        if (!mayDecode && pcm.Pcm.Length > DcInlineBuildMax) return DcPending;
        var wav = MechAudioPlayer.BuildWav(pcm, vol / 100f);
        lock (slot) { slot.Pcm = pcm; slot.Vol = vol; slot.Wav = wav; }
        return wav;
    }

    private DecodedAudio? DcDefaultPcm(bool tick)
    {
        if (_dcSoundBroken) return null;
        lock (_dcWavLock)
        {
            try
            {
                return tick ? _dcPcmTick ??= DcLoadWav("dc_tick.wav") : _dcPcmEnter ??= DcLoadWav("dc_enter.wav");
            }
            catch (Exception ex)
            {
                DcSoundLog("default sound failed to load: " + ex.Message);
                _dcSoundBroken = true;   // logged once; no retry on every tick
                return null;
            }
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
    }

    // ▶ next to a dropdown: that one sound, once, at the current volume (toggles ignored — it's an explicit preview).
    private void DcSoundPreview(bool tick) { if (_dcReplace) DcPlay(tick, DcTagTest); }   // the button is inert when OFF too

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
        if (tick ? _dcOptsTick != null && _dcOptsTickKey == key : _dcOptsOpen != null && _dcOptsOpenKey == key)
            return tick ? _dcOptsTick! : _dcOptsOpen!;
        var opts = new List<string>(_dcSoundNames.Count + 2) { "Default" };
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
        if (tick) { _dcTickFile = name; _cfg.Set<string>("dungeon_confirm_sound_tick_file", name); }
        else      { _dcOpenFile = name; _cfg.Set<string>("dungeon_confirm_sound_open_file", name); }
        _cfg.Save();
        DcWarm(tick);   // decode now: a broken file shows its note right away, a good one plays instantly later
    }
}
