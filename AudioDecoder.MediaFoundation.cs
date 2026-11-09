using System;
using System.IO;

namespace Stellar.DungeonHub;

// MP3 (and any other format Windows has a decoder for) via Media Foundation's Source Reader — no NuGet, no bundled
// native lib: mfplat/mfreadwrite ship with Windows (absent only on "N" editions without the Media Feature Pack).
// COM is driven through raw vtable function pointers (indices verified against the 10.0.26100 SDK headers) rather
// than [ComImport] interfaces, keeping it on the same blittable-unsafe footing as the capture hook.
//
// We ask the reader for PCM 16-bit only and leave rate/channels native, so it inserts just the decoder (no resampler
// MFT). Ported from StellarExperimentPlugin (Voice Inject) for Dungeon Confirm's custom .mp3 sounds; the PCM goes
// to DcMixer.Convert at the file's own rate, which resamples it once (cached); nothing is resampled here.
internal static unsafe partial class AudioDecoder
{
    private const uint MfVersion              = 0x00020070; // MF_SDK_VERSION 2 << 16 | MF_API_VERSION 0x70
    private const uint MfStartupLite          = 0x1;        // MFSTARTUP_NOSOCKET
    private const uint FirstAudioStream       = 0xFFFFFFFD; // MF_SOURCE_READER_FIRST_AUDIO_STREAM
    private const uint AllStreams             = 0xFFFFFFFE; // MF_SOURCE_READER_ALL_STREAMS
    private const uint ReaderFlagError        = 0x1;        // MF_SOURCE_READERF_ERROR
    private const uint ReaderFlagEndOfStream  = 0x2;        // MF_SOURCE_READERF_ENDOFSTREAM

    // IMFSourceReader / IMFAttributes(+IMFMediaType,IMFSample) / IMFMediaBuffer vtable indices.
    private const int RdSetStreamSelection = 4, RdGetCurrentMediaType = 6, RdSetCurrentMediaType = 7, RdReadSample = 9;
    private const int AtGetUINT32 = 7, AtSetUINT32 = 21, AtSetGUID = 24;
    private const int SmConvertToContiguousBuffer = 41;
    private const int MbLock = 3, MbUnlock = 4;

    private static readonly Guid MF_MT_MAJOR_TYPE               = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE                  = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MF_MT_AUDIO_NUM_CHANNELS       = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE    = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    private static readonly Guid MFMediaType_Audio              = new("73647561-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFAudioFormat_PCM              = new("00000001-0000-0010-8000-00aa00389b71");

    // Must run on a thread we own (it's CoInitialize'd MTA here and uninitialised on exit).
    private static DecodedAudio DecodeMediaFoundation(string path)
    {
        int coHr = VoiceNative.CoInitializeEx(IntPtr.Zero, VoiceNative.CoInitMultithreaded);
        int mfHr = VoiceNative.MFStartup(MfVersion, MfStartupLite);
        IntPtr reader = IntPtr.Zero;
        try
        {
            if (mfHr < 0) throw new InvalidOperationException($"MFStartup failed hr=0x{mfHr:X8} (Media Foundation missing — Windows N edition?)");
            Check(VoiceNative.MFCreateSourceReaderFromURL(path, IntPtr.Zero, &reader), "MFCreateSourceReaderFromURL");

            var rd = *(IntPtr**)reader;
            ((delegate* unmanaged<IntPtr, uint, int, int>)rd[RdSetStreamSelection])(reader, AllStreams, 0);
            Check(((delegate* unmanaged<IntPtr, uint, int, int>)rd[RdSetStreamSelection])(reader, FirstAudioStream, 1), "select audio stream");

            IntPtr want = IntPtr.Zero;
            Check(VoiceNative.MFCreateMediaType(&want), "MFCreateMediaType");
            try
            {
                SetGuid(want, MF_MT_MAJOR_TYPE, MFMediaType_Audio);
                SetGuid(want, MF_MT_SUBTYPE, MFAudioFormat_PCM);
                var key = MF_MT_AUDIO_BITS_PER_SAMPLE;
                Check(((delegate* unmanaged<IntPtr, Guid*, uint, int>)VoiceNative.Slot(want, AtSetUINT32))(want, &key, 16), "set bits");
                Check(((delegate* unmanaged<IntPtr, uint, uint*, IntPtr, int>)rd[RdSetCurrentMediaType])(reader, FirstAudioStream, null, want),
                      "SetCurrentMediaType PCM16 (no decoder for this file?)");
            }
            finally { VoiceNative.Release(want); }

            IntPtr cur = IntPtr.Zero;
            Check(((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)rd[RdGetCurrentMediaType])(reader, FirstAudioStream, &cur), "GetCurrentMediaType");
            uint ch, rate, bits;
            try
            {
                ch   = GetU32(cur, MF_MT_AUDIO_NUM_CHANNELS);
                rate = GetU32(cur, MF_MT_AUDIO_SAMPLES_PER_SECOND);
                bits = GetU32(cur, MF_MT_AUDIO_BITS_PER_SAMPLE);
            }
            finally { VoiceNative.Release(cur); }
            if (bits != 16 || ch == 0 || rate == 0) throw new InvalidDataException($"unexpected decoder output {rate} Hz/{ch} ch/{bits}-bit");

            long maxBytes = (long)MaxSeconds * rate * ch * 2;
            using var ms = new MemoryStream();
            while (ms.Length < maxBytes)
            {
                uint idx, flags; long ts; IntPtr sample = IntPtr.Zero;
                Check(((delegate* unmanaged<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)rd[RdReadSample])
                      (reader, FirstAudioStream, 0, &idx, &flags, &ts, &sample), "ReadSample");
                try
                {
                    if ((flags & ReaderFlagError) != 0) throw new InvalidDataException("decoder reported a stream error");
                    if (sample != IntPtr.Zero) AppendSample(sample, ms);
                    if ((flags & ReaderFlagEndOfStream) != 0) break;
                }
                finally { VoiceNative.Release(sample); }
            }

            int nBytes = (int)Math.Min(ms.Length, maxBytes) & ~1;
            nBytes -= nBytes % (int)(ch * 2);
            var pcm = new short[nBytes / 2];
            Buffer.BlockCopy(ms.GetBuffer(), 0, pcm, 0, nBytes);
            return new DecodedAudio(pcm, (int)rate, (int)ch, "Media Foundation");
        }
        finally
        {
            VoiceNative.Release(reader);
            if (mfHr >= 0) VoiceNative.MFShutdown();
            if (coHr >= 0) VoiceNative.CoUninitialize();   // S_OK / S_FALSE both need balancing; RPC_E_CHANGED_MODE (<0) doesn't
        }
    }

    private static void AppendSample(IntPtr sample, MemoryStream ms)
    {
        IntPtr mb = IntPtr.Zero;
        Check(((delegate* unmanaged<IntPtr, IntPtr*, int>)VoiceNative.Slot(sample, SmConvertToContiguousBuffer))(sample, &mb), "ConvertToContiguousBuffer");
        try
        {
            byte* data; uint max, len;
            Check(((delegate* unmanaged<IntPtr, byte**, uint*, uint*, int>)VoiceNative.Slot(mb, MbLock))(mb, &data, &max, &len), "IMFMediaBuffer.Lock");
            try { ms.Write(new ReadOnlySpan<byte>(data, (int)len)); }
            finally { ((delegate* unmanaged<IntPtr, int>)VoiceNative.Slot(mb, MbUnlock))(mb); }
        }
        finally { VoiceNative.Release(mb); }
    }

    private static void SetGuid(IntPtr attrs, Guid key, Guid value) =>
        Check(((delegate* unmanaged<IntPtr, Guid*, Guid*, int>)VoiceNative.Slot(attrs, AtSetGUID))(attrs, &key, &value), "SetGUID");

    private static uint GetU32(IntPtr attrs, Guid key)
    {
        uint v = 0;
        Check(((delegate* unmanaged<IntPtr, Guid*, uint*, int>)VoiceNative.Slot(attrs, AtGetUINT32))(attrs, &key, &v), "GetUINT32");
        return v;
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what} failed hr=0x{hr:X8}");
    }
}
