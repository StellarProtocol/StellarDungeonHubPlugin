using System;
using System.Runtime.InteropServices;

namespace Stellar.DungeonHub;

// Raw COM / Media Foundation surface for AudioDecoder.MediaFoundation.cs (custom .mp3 sounds). Trimmed from
// StellarExperimentPlugin's VoiceNative.cs to the decode half only — the capture-hook P/Invokes (VirtualProtect,
// GetProcAddress …) and the DirectSound structs are not needed here. Every signature is blittable (pointers / ints),
// except the one path string for MFCreateSourceReaderFromURL.
internal static unsafe class VoiceNative
{
    // ── ole32 (COM apartment for the Media Foundation decode thread) ──────────────────────────────────────────────
    [DllImport("ole32", ExactSpelling = true)]
    public static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32", ExactSpelling = true)]
    public static extern void CoUninitialize();

    public const uint CoInitMultithreaded = 0x0;

    // ── Media Foundation ─────────────────────────────────────────────────────────────────────────────────────────
    [DllImport("mfplat", ExactSpelling = true)]
    public static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat", ExactSpelling = true)]
    public static extern int MFShutdown();

    [DllImport("mfplat", ExactSpelling = true)]
    public static extern int MFCreateMediaType(IntPtr* mediaType);

    [DllImport("mfreadwrite", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes, IntPtr* reader);

    // ── COM helpers ──────────────────────────────────────────────────────────────────────────────────────────────
    // Function pointer for vtable slot `index` of COM object `obj` (slot = index * 8 on x64).
    public static IntPtr Slot(IntPtr obj, int index) => (*(IntPtr**)obj)[index];

    public static void Release(IntPtr obj)
    {
        if (obj != IntPtr.Zero) ((delegate* unmanaged<IntPtr, uint>)Slot(obj, 2))(obj);
    }
}
