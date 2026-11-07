using System;
using System.IO;

namespace Stellar.DungeonHub;

// Decoded source audio: 16-bit interleaved at the FILE's own rate/channels. Kept so the clip can be re-converted
// if the capture format changes (or only becomes known) after the file was loaded.
internal sealed class DecodedAudio
{
    public readonly short[] Pcm;
    public readonly int Rate, Channels;
    public readonly string Decoder;
    public DecodedAudio(short[] pcm, int rate, int channels, string decoder) { Pcm = pcm; Rate = rate; Channels = channels; Decoder = decoder; }
    public double Seconds => (double)Pcm.Length / Channels / Rate;
}

// Shared off-thread file decoder: reads an audio file into 16-bit interleaved PCM (DecodedAudio) at the file's own
// rate/channels. Used by Mechanic Callouts' local speaker playback (MechAudioPlayer). Runs on a background thread and
// may allocate freely.
//   .wav → hand-written RIFF parser (PCM 8/16/24/32-bit int, 32-bit float, WAVE_FORMAT_EXTENSIBLE of those).
//   anything else → not supported here. (Experiment's copy falls back to Windows Media Foundation; Dungeon Hub only
//   decodes its own embedded WAVs, so the MF half and its VoiceNative P/Invokes were left out.)
internal static class AudioDecoder
{
    // 16-bit samples are 2 bytes; 20 min of 48 kHz stereo ≈ 230 MB for source + clip. Longer is truncated.
    private const int MaxSeconds = 20 * 60;

    public static DecodedAudio Decode(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("file not found", path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext != ".wav" && ext != ".wave") throw new InvalidDataException("only .wav is supported");
        return DecodeWav(File.ReadAllBytes(path));
    }

    // ── WAV ──────────────────────────────────────────────────────────────────────────────────────────────────────
    // In-memory entry (embedded resources — Dungeon Confirm's sounds). Throws InvalidDataException on a bad image.
    public static DecodedAudio DecodeWavBytes(byte[] b) => DecodeWav(b);

    private static DecodedAudio DecodeWav(byte[] b)
    {
        if (b.Length < 12 || Tag(b, 0) != "RIFF" || Tag(b, 8) != "WAVE") throw new InvalidDataException("not a RIFF/WAVE file");

        int tag = 0, ch = 0, rate = 0, bits = 0, dataOff = -1, dataLen = 0;
        int p = 12;
        while (p + 8 <= b.Length)
        {
            string id = Tag(b, p);
            long size = BitConverter.ToUInt32(b, p + 4);
            int body = p + 8;
            long avail = b.Length - body;
            if (id == "fmt " && size >= 16)
            {
                tag  = BitConverter.ToUInt16(b, body);
                ch   = BitConverter.ToUInt16(b, body + 2);
                rate = BitConverter.ToInt32(b, body + 4);
                bits = BitConverter.ToUInt16(b, body + 14);
                // WAVE_FORMAT_EXTENSIBLE: real tag = first ushort of SubFormat GUID (cbSize@16, SubFormat@24).
                if (tag == 0xFFFE && size >= 26) tag = BitConverter.ToUInt16(b, body + 24);
            }
            else if (id == "data")
            {
                dataOff = body;
                // Streaming writers leave 0 / 0xFFFFFFFF in the size — clamp to what's actually in the file.
                dataLen = (int)Math.Min(size == 0 || size == 0xFFFFFFFF ? avail : size, avail);
                break;
            }
            p = (int)Math.Min(b.Length, body + size + (size & 1)); // chunks are word-aligned (pad byte)
        }
        if (dataOff < 0) throw new InvalidDataException("no data chunk");
        if (ch <= 0 || rate <= 0) throw new InvalidDataException("no/invalid fmt chunk");
        bool isFloat = tag == 3;
        if (!(tag == 1 && (bits == 8 || bits == 16 || bits == 24 || bits == 32)) && !(isFloat && bits == 32))
            throw new InvalidDataException($"unsupported WAV encoding (tag {tag}, {bits}-bit) — PCM 8/16/24/32 or float32 only");

        int bps = bits / 8;
        long frames = dataLen / (bps * ch);
        frames = Math.Min(frames, (long)MaxSeconds * rate);
        var pcm = new short[frames * ch];
        int src = dataOff;
        for (long i = 0; i < pcm.Length; i++, src += bps)
        {
            pcm[i] = bits switch
            {
                8  => (short)((b[src] - 128) << 8),
                16 => BitConverter.ToInt16(b, src),
                24 => (short)(b[src + 1] | (b[src + 2] << 8)),          // top 16 of 24 bits
                _  => isFloat ? FloatToShort(BitConverter.ToSingle(b, src))
                              : (short)(BitConverter.ToInt32(b, src) >> 16),
            };
        }
        return new DecodedAudio(pcm, rate, ch, $"WAV {(isFloat ? "float" : "PCM")} {bits}-bit");
    }

    private static string Tag(byte[] b, int at) => at + 4 <= b.Length ? System.Text.Encoding.ASCII.GetString(b, at, 4) : "";

    private static short FloatToShort(float f)
    {
        if (float.IsNaN(f)) return 0;
        float v = f * 32767f;
        return v >= 32767f ? short.MaxValue : v <= -32768f ? short.MinValue : (short)v;
    }
}
