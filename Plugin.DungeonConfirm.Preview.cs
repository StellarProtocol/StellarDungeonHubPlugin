namespace Stellar.DungeonHub;

// Dungeon Confirm — "Preview 5-player" / "Preview raid": the real window on FAKE data, to check the look in game without
// a party. Purely local: no Lua at all, every button only closes, and the countdown runs to the "Time ran out" end
// state, with the real open sound + countdown ticks (current sound settings; silent while Replace is OFF). A real
// request arriving meanwhile replaces the preview (DcTick). Art paths are real table values
// (ChallengeHeroDungeonTable 6007-style key art / RaidDungeonTable 13001 MonsterShowTexture over its DiffBgTexture) so
// the crop and the raid compose are exercised; class icons use real profession ids; affix ids/icons are AffixTable rows.
// SugarMahiru = me (accent outline, no YOU tag) + ASSIST (the longest name+tag case, cut at 552 px) with a 6-digit
// score; Tomo = score 0 → "—" (no score sent). Both previews leave slots empty (4 of 5 / 7 of 20 over groups 1-3) so
// the placeholder rows show.
public sealed partial class Plugin
{
    private void DcStartPreview(bool raid)
    {
        if (_services.ClientState.Phase != Stellar.Abstractions.Domain.GamePhase.World) return;
        DcHide();
        _dcDismissed = true;   // the stash's current request (if any) is not ours to show again
        var s = raid ? DcPreviewRaid() : DcPreviewFive();
        _dcPreview = true;
        _dcSeenAt = DcNow;
        _dcEnd = DcEnd.None; _dcConfirmed = false; _dcMatchDeclined = false; _dcArmUntil = 0;
        _dc = s;
        if (s.Twenty) DcBuildGrid(s); else DcFitAffixLabels(s);
        DcShow();
        DcSoundOnOpen();   // previews sound like a real request: open sound now, ticks for the last 10 s (.Sound.cs)
    }

    private static DcMember DcFake(string name, int lvl, int prof, int ready, bool leader = false, bool me = false,
        bool assist = false, int slot = 0, int score = 0) =>
        new() { CharId = name, Name = name, Level = lvl, Prof = prof, Ready = ready, Leader = leader, Me = me, Assist = assist, Slot = slot, Score = score };

    private static DcSnap DcPreviewFive()
    {
        var s = new DcSnap
        {
            DungeonId = 6007, TotalSec = 30, Title = "Goblin Lair", TypeName = "Master 17", DiffKind = 3,
            Art = "ui/textures/dungeon_textures/dungeon_gebulin_1", KeyLeft = 2,
        };
        s.Affixes.Add(new DcAffix { Id = 102, Name = "Void Corruption Afterimage", Icon = "ui/atlas/affix/com_affix_s1_flux_shadow1" });
        s.Affixes.Add(new DcAffix { Id = 106, Name = "Illusion Invasion", Icon = "ui/atlas/affix/com_affix_s1_flux_meteor1" });
        s.Members.Add(DcFake("Mahiru",      60, 1, 1, leader: true, score: 48215));
        s.Members.Add(DcFake("SugarMahiru", 60, 2, 0, me: true, assist: true, score: 123456));
        s.Members.Add(DcFake("Kirin",       58, 5, 1, score: 39870));   // prof 5 = green badge (1/2 red, 9 blue)
        s.Members.Add(DcFake("Tomo",        59, 9, 0, assist: true));
        return s;
    }

    private static DcSnap DcPreviewRaid()
    {
        var s = new DcSnap
        {
            DungeonId = 13001, Twenty = true, RaidContent = true, TotalSec = 30, Title = "Dragon Shackles", TypeName = "Easy", DiffKind = 1,
            Art = "ui/textures/roid/raid_easy_monster", ArtBg = "ui/textures/roid/raid_easy_bg",
        };
        // 7 members: group 1 slots 1-3, group 2 slots 6-7, group 3 slots 11-12, group 4 empty ("0/5").
        string[] names = { "Mahiru", "SugarMahiru", "Kirin", "Hikari", "Ren", "Sora", "Mei" };
        // Profs cover all three badge colours (red 1/2/11, blue 9/12, green 5/13); Ren declined (ready 2) shows the ✕ mark.
        int[] slots = { 1, 2, 3, 6, 7, 11, 12 }, ready = { 1, 0, 1, 1, 2, 1, 0 }, prof = { 1, 9, 5, 2, 12, 13, 11 };
        for (int i = 0; i < names.Length; i++)
            s.Members.Add(DcFake(names[i], 60, prof[i], ready[i], leader: i == 0, me: i == 1, slot: slots[i],
                score: i == 4 ? 0 : 30000 + i * 4321));
        return s;
    }
}
