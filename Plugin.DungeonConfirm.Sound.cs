using System;
using System.IO;
using System.Threading;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.DungeonHub;

// Dungeon Confirm — our own sounds, replacing the game's MatchAudio cue (no longer played, .Lua.cs). Two user-approved
// embedded WAVs (44.1 kHz stereo 16-bit): dc_enter (3.05 s boom) once when the window opens for a REAL request, and
// dc_tick (0.97 s beep) on each countdown second 10..1 while I still have to answer. Preview opens stay silent (the
// Test button covers sound).
//
// Playback = a dedicated MechAudioPlayer (winmm PlaySound, in-memory WAV, own background thread — Mechanic-Callouts.md
// "Local audio"). winmm ignores the game's mixer, so it is heard with the game muted or in the background; that's the
// point, don't route it through Z.AudioMgr. MinGapMs = 0: the player's queue is serial, so two ticks never overlap (a
// tick due while the previous one's last ~30 ms still plays just waits for it).
//
// Volume: the decoded PCM is kept; the two WAV images are rebuilt with the volume baked into the samples
// (MechAudioPlayer.BuildWav) the first time a sound is needed after the volume changed — never per play, and not on
// every slider drag step. Volume 0 = nothing is played at all.
//
// Tick timing: a 50 ms Every (main thread, arithmetic only) takes s = ceil(remaining) — exactly the number the window's
// timer shows (DcTimerText) — and ticks when s is 1..10 and lower than the last ticked second. A second that is due while
// the entry sound is still queued/playing (a late open) is SKIPPED, not delayed. Ticks stop (queued dropped, current cut)
// as soon as I no longer need to act: answered, an end state, or the window hidden (hide also cuts the entry sound).
public sealed partial class Plugin
{
    private const string DcTagEnter = "dc_enter", DcTagTick = "dc_tick";

    private MechAudioPlayer _dcAudio = null!;
    private IDisposable? _dcSoundTimer;
    private bool _dcSoundOpen, _dcSoundTicks;
    private int  _dcSoundVolume;              // 0..100
    private int  _dcLastTick = int.MaxValue;  // last countdown second ticked for the request on screen
    private volatile bool _dcTestRunning, _dcSoundDisposed, _dcSoundBroken;   // Broken: the embedded WAVs failed to load

    private readonly object _dcWavLock = new();   // the test thread and the main thread both build buffers
    private DecodedAudio? _dcPcmEnter, _dcPcmTick;
    private byte[]? _dcWavEnter, _dcWavTick;
    private int _dcWavVolume = -1;                // the volume the two images were built with

    private void InitDcSound()
    {
        _dcSoundOpen   = _cfg.Get<bool>("dungeon_confirm_sound_open", true);
        _dcSoundTicks  = _cfg.Get<bool>("dungeon_confirm_sound_ticks", true);
        _dcSoundVolume = Math.Clamp(_cfg.Get<int>("dungeon_confirm_sound_volume", 80), 0, 100);
        _dcAudio = new MechAudioPlayer(s => _services.Framework.Post(() => _services.Log.Info("[DungeonConfirm][Sound] " + s)))
            { MinGapMs = 0, MaxPending = 4 };
        _dcSoundTimer = _services.Framework.Every(TimeSpan.FromMilliseconds(50), DcSoundTick);
    }

    private void DisposeDcSound()
    {
        _dcSoundDisposed = true;   // ends a running Test sequence
        _dcSoundTimer?.Dispose();
        _dcAudio?.Stop();
        _dcAudio?.Dispose();
    }

    // Called once per request, on its first show (DcApplySnap — never for Preview, re-polls or re-centres).
    private void DcSoundOnOpen()
    {
        _dcLastTick = int.MaxValue;
        if (_dcSoundOpen && DcSoundBuffers() is { } b) _dcAudio.PlayWav(b.Enter, DcTagEnter);
    }

    private void DcSoundStopTicks() => _dcAudio?.Stop(DcTagTick);
    private void DcSoundStopAll()   => _dcAudio?.Stop();

    // I still have to act: team = a member (never the leader) who hasn't confirmed/declined; match = until I answer.
    private bool DcNeedsMyAnswer => _dc != null && !_dcPreview && _dcEnd == DcEnd.None && !_dcConfirmed &&
        (_dcShow5 || _dcShowRaid) && (_dc.Match ? !_dcMatchDeclined : !_dc.IAmLeader) && _dc.Me is not { Ready: not 0 };

    private void DcSoundTick()
    {
        if (_dc == null) return;
        if (!DcNeedsMyAnswer) { if (_dcAudio.IsBusyWith(DcTagTick)) DcSoundStopTicks(); return; }
        int s = (int)Math.Ceiling(DcRemaining);
        if (s < 1 || s > 10 || s >= _dcLastTick) return;
        _dcLastTick = s;                                       // this second is spent, played or skipped
        if (!_dcSoundTicks || _dcAudio.IsBusyWith(DcTagEnter)) return;   // late open: boom still going → skip
        if (DcSoundBuffers() is { } b) _dcAudio.PlayWav(b.Tick, DcTagTick);
    }

    // The two WAV images at the current volume; null when the volume is 0 or the resources failed to load. Any thread.
    private (byte[] Enter, byte[] Tick)? DcSoundBuffers()
    {
        int vol = _dcSoundVolume;
        if (vol <= 0 || _dcSoundBroken) return null;
        lock (_dcWavLock)
        {
            try
            {
                _dcPcmEnter ??= DcLoadWav("dc_enter.wav");
                _dcPcmTick  ??= DcLoadWav("dc_tick.wav");
                if (_dcWavVolume != vol || _dcWavEnter == null || _dcWavTick == null)
                {
                    _dcWavEnter = MechAudioPlayer.BuildWav(_dcPcmEnter, vol / 100f);
                    _dcWavTick  = MechAudioPlayer.BuildWav(_dcPcmTick,  vol / 100f);
                    _dcWavVolume = vol;
                }
                return (_dcWavEnter, _dcWavTick);
            }
            catch (Exception ex)
            {
                _services.Framework.Post(() => _services.Log.Warning("[DungeonConfirm][Sound] load failed: " + ex.Message));
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

    // Test: the entry sound, wait for it to end, then 3 ticks 1 s apart — on a background thread; extra clicks while it
    // runs are ignored.
    private void DcSoundTest()
    {
        if (_dcTestRunning) return;
        _dcTestRunning = true;
        new Thread(() =>
        {
            try
            {
                if (DcSoundBuffers() is not { } b) return;
                _dcAudio.PlayWav(b.Enter, DcTagEnter);
                while (!_dcSoundDisposed && _dcAudio.IsBusyWith(DcTagEnter)) Thread.Sleep(20);
                for (int i = 0; i < 3 && !_dcSoundDisposed; i++) { _dcAudio.PlayWav(b.Tick, DcTagTick); Thread.Sleep(1000); }
            }
            catch { }
            finally { _dcTestRunning = false; }
        }) { IsBackground = true, Name = "DungeonHub.DcSoundTest" }.Start();
    }

    private void SetDcSoundBool(string key, bool v, Action<bool> apply) { apply(v); _cfg.Set<bool>(key, v); _cfg.Save(); }

    // Settings rows (Dungeon Confirm settings window, under the replace toggle).
    private HudElement BuildDcSoundRows() => new ColumnElement(new HudElement[]
    {
        DcToggleRow("Play a sound when it opens", () => _dcSoundOpen,
            v => SetDcSoundBool("dungeon_confirm_sound_open", v, x => _dcSoundOpen = x)),
        DcToggleRow("Countdown ticks (last 10 s)", () => _dcSoundTicks,
            v => SetDcSoundBool("dungeon_confirm_sound_ticks", v, x => { _dcSoundTicks = x; if (!x) DcSoundStopTicks(); })),
        DcSliderRow("Sound volume", () => _dcSoundVolume, v =>
        {
            _dcSoundVolume = (int)Math.Round(v);
            _cfg.Set<int>("dungeon_confirm_sound_volume", _dcSoundVolume); _cfg.Save();
        }, 0f, 100f, () => $"{_dcSoundVolume}%", labelWidth: 100f),
        new ButtonElement(Label: () => _dcTestRunning ? "Testing..." : "Test sound", OnClick: DcSoundTest, Width: 180f),
    }, Gap: 8f);

    // Capsule toggle + sibling label (ToggleElement is capsule-only — WindowBuilder-Patterns.md). Copied from
    // Experiment's MechToggleRow / MechSliderRow (Mechanic Callouts), which this window used while it lived there.
    private static HudElement DcToggleRow(string label, Func<bool> get, Action<bool> set) =>
        new RowElement(new HudElement[]
        {
            new ToggleElement(Label: () => "", Get: get, Set: set),
            new TextElement(() => label),
        }, Gap: 6f);

    private HudElement DcSliderRow(string label, Func<float> get, Action<float> set, float min, float max, Func<string> value,
        float labelWidth = 60f) =>
        new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(() => label, Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted), Width: labelWidth),
            new CellElement(new SliderElement(Get: get, Set: v => set(Math.Clamp(v, min, max)), Min: min, Max: max), Weight: 1f),
            new CellElement(new TextElement(value), Width: 56f),
        }, Gap: 6f);
}
