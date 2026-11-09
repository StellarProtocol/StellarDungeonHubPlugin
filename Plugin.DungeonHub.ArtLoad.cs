using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using UE = UnityEngine;

namespace Stellar.DungeonHub;

// Dungeons hub — rationing the art work per frame (first-open freeze fix, 2026-11-08).
// The texture Funcs (.Art.cs) run on the window's Apply; on an open, 16 list thumbs + the art band all asked for their
// first load in the same Apply, and a raid tab composed every (backdrop, cut-out) pair there too — each compose = two
// Blits, a new RenderTexture and a ReadPixels readback (a CPU↔GPU sync). Now:
//   • DhLoad: a path seen for the first time costs one of DhArtLoadsPerFrame request slots this frame; past that the
//     box returns null = its existing empty tile, and the next Apply asks again. Already-requested paths go straight
//     to IGameAssets.LoadByPath (its own slot cache; cheap). Paths stay "asked" for the session — the framework keeps
//     the loaded textures anyway.
//   • DhComposeAllowed: at most DhComposePerFrame raid composes per frame.
// The warm-up (.Prewarm.cs) asks the saved tab's thumbs ahead of the first open through the same gate.
public sealed partial class Plugin
{
    private const int DhArtLoadsPerFrame = 3, DhComposePerFrame = 1;

    private readonly HashSet<string> _dhArtAsked = new();
    private int _dhArtFrame = -1, _dhArtLeft, _dhCompFrame = -1, _dhCompLeft;

    private object? DhLoad(string path, out UvRect uv)
    {
        uv = new UvRect(0f, 0f, 1f, 1f);
        if (path.Length == 0) return null;
        if (!_dhArtAsked.Contains(path))
        {
            int f = UE.Time.frameCount;
            if (f != _dhArtFrame) { _dhArtFrame = f; _dhArtLeft = DhArtLoadsPerFrame; }
            if (_dhArtLeft <= 0) return null;
            _dhArtLeft--;
            _dhArtAsked.Add(path);
        }
        return _services.GameAssets.LoadByPath(path, out uv);
    }

    private bool DhComposeAllowed()
    {
        int f = UE.Time.frameCount;
        if (f != _dhCompFrame) { _dhCompFrame = f; _dhCompLeft = DhComposePerFrame; }
        if (_dhCompLeft <= 0) return false;
        _dhCompLeft--;
        return true;
    }
}
