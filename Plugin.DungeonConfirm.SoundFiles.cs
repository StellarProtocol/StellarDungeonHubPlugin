using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Stellar.DungeonHub;

// Dungeon Confirm — the user's own sounds (settings window, user-approved 2026-11-07). Any .wav / .mp3 in the plugin's
// data folder `<game>\stellar\plugindata\stellar.dungeonhub.data\sounds\` can replace the open sound or the countdown
// tick (one choice each, "Default" = the embedded WAV; config keys dungeon_confirm_sound_open_file / _tick_file, the
// FILE NAME only).
//
//   • Listing goes through the framework's per-plugin store (IPluginDataStore.List("sounds/")), so we never guess where
//     the folder is to read the names. The first look writes sounds/README.txt, which also creates the folder.
//   • Decoding needs a real path (Media Foundation opens .mp3 by URL), and so does "Open sounds folder". The absolute
//     folder is RESOLVED, not assumed: candidate roots (the plugin DLL's ..\.., the working dir, the app base) are tried
//     and the one holding our README.txt wins. No match → no Explorer button target and .wav-only via Read() bytes.
//   • Decoded PCM is cached per file name and dropped when the file's size or write time changes. A decode never runs
//     on the main thread (an mp3 can take a moment): playback asks the cache without decoding and, on a miss, hands the
//     work to a background thread (.Sound.cs DcPlay). A missing / undecodable file plays the DEFAULT sound instead; the
//     saved choice is kept and the settings show "Can't play <name> · using the default sound".
public sealed partial class Plugin
{
    private const string DcSoundsDir = "sounds/", DcReadmeName = "sounds/README.txt";
    private const string DcDataFolder = "stellar.dungeonhub.data";   // the framework's <pluginGuid>.data folder
    private const string DcReadme =
        "Dungeon Hub - your own sounds\r\n\r\n" +
        "Put .wav or .mp3 files in this folder, then pick them in Dungeon Hub settings (the gear on the hub window),\r\n" +
        "under SOUNDS: one for when the confirm window opens, one for the countdown ticks.\r\n\r\n" +
        "Supported: .wav (PCM 8/16/24/32-bit or 32-bit float) and .mp3. Keep them short.\r\n" +
        "A file that is missing or can't be played falls back to the default sound.\r\n";

    private IReadOnlyList<string> _dcSoundNames = Array.Empty<string>();   // sorted file names (no folder)
    private int    _dcSoundListVer;
    private double _dcSoundListAt = double.MinValue;
    private bool   _dcSoundsInit;
    private string? _dcSoundsPath;                                          // absolute folder, null = unresolved

    private sealed class DcSrc { public long Size, Mtime; public DecodedAudio? Pcm; public bool Failed; }
    private readonly Dictionary<string, DcSrc> _dcSrc = new(StringComparer.OrdinalIgnoreCase);   // guarded by _dcWavLock

    // README + folder path, once (main thread).
    private void DcEnsureSoundsFolder()
    {
        if (_dcSoundsInit) return;
        _dcSoundsInit = true;
        bool has = false;
        foreach (var n in _services.Data.List(DcSoundsDir)) if (n == DcReadmeName) { has = true; break; }
        if (!has) _services.Data.Write(DcReadmeName, Encoding.UTF8.GetBytes(DcReadme));
        _dcSoundsPath = DcResolveSoundsPath();
        if (_dcSoundsPath == null) _services.Log.Warning("[DungeonConfirm][Sound] couldn't locate the sounds folder on disk");
        else _services.Log.Info("[DungeonConfirm][Sound] sounds folder: " + _dcSoundsPath);
    }

    private static string? DcResolveSoundsPath()
    {
        var roots = new List<string>();
        try { roots.Add(Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? "", "..", "..")); } catch { }
        try { roots.Add(Path.Combine(Environment.CurrentDirectory, "stellar")); } catch { }
        try { roots.Add(Path.Combine(AppContext.BaseDirectory, "stellar")); } catch { }
        foreach (var root in roots)
        {
            try
            {
                var dir = Path.GetFullPath(Path.Combine(root, "plugindata", DcDataFolder, "sounds"));
                if (File.Exists(Path.Combine(dir, "README.txt"))) return dir;
            }
            catch { }
        }
        return null;
    }

    private static bool DcIsSoundFile(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext == ".wav" || ext == ".mp3";
    }

    // Re-list the folder (main thread). Throttled to once a second unless forced: the dropdown's Options Func is polled
    // on every refresh, so this is also what picks up files added while the settings are open.
    private void DcRefreshSoundList(bool force)
    {
        if (!force && DcNow - _dcSoundListAt < 1.0) return;
        _dcSoundListAt = DcNow;
        DcEnsureSoundsFolder();
        var names = new List<string>();
        foreach (var n in _services.Data.List(DcSoundsDir))
        {
            var f = n.Substring(DcSoundsDir.Length);
            if (f.IndexOf('/') < 0 && DcIsSoundFile(f)) names.Add(f);   // this folder only, sounds only
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        if (names.Count == _dcSoundNames.Count)
        {
            bool same = true;
            for (int i = 0; i < names.Count && same; i++) same = names[i] == _dcSoundNames[i];
            if (same) return;
        }
        _dcSoundNames = names;
        _dcSoundListVer++;
    }

    private bool DcSoundListed(string name)
    {
        foreach (var n in _dcSoundNames) if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // The chosen file can't be played → its name (for the yellow note), else "". Main thread, per refresh: a short list
    // scan + a dictionary look-up.
    private string DcSoundProblem(bool tick)
    {
        string name = DcSoundFile(tick);
        if (name.Length == 0) return "";
        if (!DcSoundListed(name)) return name;
        lock (_dcWavLock) return _dcSrc.TryGetValue(name, out var c) && c.Failed ? name : "";
    }

    // Cache-only look-up (no decode). True = the cache answers for the file as it is on disk now (pcm null = it failed or
    // is gone → the caller plays the default). False = not decoded yet / changed since → decode off the main thread.
    private bool DcPeekPcm(string name, out DecodedAudio? pcm)
    {
        pcm = null;
        if (!DcStat(name, out long size, out long mtime)) return true;   // missing → default, nothing to decode
        lock (_dcWavLock)
        {
            if (!_dcSrc.TryGetValue(name, out var c) || c.Size != size || c.Mtime != mtime) return false;
            pcm = c.Pcm;
            return true;
        }
    }

    // Decode (background threads only — MF needs a thread we own). Null = missing / undecodable.
    private DecodedAudio? DcDecodeCustom(string name)
    {
        if (!DcStat(name, out long size, out long mtime)) return null;
        var src = new DcSrc { Size = size, Mtime = mtime };
        try
        {
            src.Pcm = _dcSoundsPath != null ? AudioDecoder.Decode(Path.Combine(_dcSoundsPath, name))
                    : AudioDecoder.DecodeWavBytes(_services.Data.Read(DcSoundsDir + name) ?? throw new FileNotFoundException(name));
            DcSoundLog($"loaded {name} ({src.Pcm.Seconds:0.0} s, {src.Pcm.Decoder})");
        }
        catch (Exception ex) { src.Failed = true; src.Pcm = null; DcSoundLog($"can't play {name}: {ex.Message}"); }
        lock (_dcWavLock) _dcSrc[name] = src;   // decoded OUTSIDE the lock: the main thread never waits on a decode
        return src.Pcm;
    }

    // Size + write time of a sound file; false when it isn't there. Without a resolved folder the name is only known to
    // the store, so it is "unchanged forever" (size -1) once decoded.
    private bool DcStat(string name, out long size, out long mtime)
    {
        size = -1; mtime = 0;
        if (_dcSoundsPath == null) return DcSoundListed(name);
        try
        {
            var fi = new FileInfo(Path.Combine(_dcSoundsPath, name));
            if (!fi.Exists) return false;
            size = fi.Length; mtime = fi.LastWriteTimeUtc.Ticks;
            return true;
        }
        catch { return false; }
    }

    private void DcSoundLog(string s) => _services.Framework.Post(() => _services.Log.Info("[DungeonConfirm][Sound] " + s));

    // "Open sounds folder": Explorer at the resolved folder (re-created if the user deleted it).
    private void DcOpenSoundsFolder()
    {
        DcEnsureSoundsFolder();
        if (_dcSoundsPath == null) return;
        try
        {
            if (!Directory.Exists(_dcSoundsPath)) _services.Data.Write(DcReadmeName, Encoding.UTF8.GetBytes(DcReadme));
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + _dcSoundsPath + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) { _services.Log.Warning("[DungeonConfirm][Sound] open folder failed: " + ex.Message); }
    }
}
