using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Xna.Framework;
using Yukar.Common;
using Yukar.Common.Rom;
using Yukar.Engine;

namespace Yukar.Battle
{
    internal static class ExGauge
    {
        internal const int CurrentReference = 31;
        internal const int Maximum = 30;
        private const string IconPrefix = "EX到達アイコン:";
        private sealed class IconDisplayState
        {
            internal bool Initialized;
            internal bool Visible;
        }
        private static readonly ConditionalWeakTable<AbstractRenderObject, IconDisplayState> IconDisplayStates
            = new ConditionalWeakTable<AbstractRenderObject, IconDisplayState>();
        private static int exReadySoundId = -1;
        private static Guid exReadySoundGuid = Guid.Empty;
        private static readonly Regex GainPattern = new Regex(
            @"(?:^|[\r\n])\s*(?:EX増加量|EX獲得量)\s*[:：=]\s*(\d+)\s*(?=$|[\r\n])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal static int SkillGain(NSkill skill)
        {
            var match = GainPattern.Match(skill?.tags ?? "");
            int value;
            return match.Success && int.TryParse(match.Groups[1].Value, out value)
                ? Math.Min(Maximum, value) : 1;
        }

        internal static void ResetSoundCache()
        {
            // Bakin unloads battle sounds between test runs, while plugin static fields can remain alive.
            // Discard the old native sound ID so the next threshold reloads the resource.
            exReadySoundId = -1;
            exReadySoundGuid = Guid.Empty;
        }

        internal static void Initialize(BattleCharacterBase character, Catalog catalog)
        {
            if (!(character is BattlePlayerData)) return;
            bool consumption;
            var info = catalog.getGameSettings().GetCastStatusParamInfo(CurrentReference, out consumption);
            if (info == null || !consumption || info.name != "EX") return;

            // EX has a fixed capacity, even when the cast's database growth value is zero.
            character.baseStatusValue.SetStatus(info.guId, Maximum);
            var current = character.consumptionStatusValue.GetStatus(info.guId);
            character.consumptionStatusValue.SetStatus(info.guId, Math.Max(0, Math.Min(Maximum, current)));
        }

        internal static void Add(BattleCharacterBase character, Catalog catalog, int amount)
        {
            if (!(character is BattlePlayerData) || amount <= 0) return;
            bool consumption;
            var info = catalog.getGameSettings().GetCastStatusParamInfo(CurrentReference, out consumption);
            if (info == null || !consumption || info.name != "EX") return;
            Initialize(character, catalog);
            var current = character.consumptionStatusValue.GetStatus(info.guId);
            var next = (int)Math.Min(Maximum, (long)current + amount);
            character.consumptionStatusValue.SetStatus(info.guId, next);
        }

        private static MenuSettings.MenuItem Copy(MenuSettings.MenuItem item)
        {
            using (var stream = new MemoryStream())
            {
                item.save(new BinaryWriter(stream));
                stream.Position = 0;
                var copy = new MenuSettings.MenuItem();
                copy.load(new BinaryReader(stream));
                copy.guid = Guid.NewGuid();
                return copy;
            }
        }

        private static IEnumerable<MenuSettings.MenuItem> Flatten(IEnumerable<MenuSettings.MenuItem> items)
        {
            foreach (var item in items)
            {
                yield return item;
                foreach (var child in Flatten(item.subItems)) yield return child;
            }
        }

        // Apply to the in-memory battle layout so the editor's project file is never overwritten.
        internal static void Prepare(LayoutProperties.LayoutNode layout, Catalog catalog)
        {
            if (layout.Usage != LayoutProperties.LayoutNode.UsageInGame.BattleStatus) return;
            var all = Flatten(layout.MenuSettings.items).ToList();
            var template = all.FirstOrDefault(x => x.name == "EXバー");
            if (template == null) return;
            var sprite = catalog.getItemFromName("EX", typeof(Yukar.Common.Resource.NSpriteSet))
                as Yukar.Common.Resource.NSpriteSet;
            var drawMotion = sprite?.motions.FirstOrDefault(x =>
                x.sprite.getResource(catalog) is Yukar.Common.Resource.NSprite);
            foreach (var parent in all.Where(x => Regex.IsMatch(x.name ?? "", @"^キャスト[1-4]$")))
            {
                int index = int.Parse(parent.name.Substring(4)) - 1;
                var bar = parent.subItems.FirstOrDefault(x => x.name == "EXバー");
                if (bar == null)
                {
                    bar = Copy(template);
                    parent.subItems.Add(bar);
                }
                bar.sliderVariable = "\\partysp[" + index + "][31]";
                bar.sliderMinimumValue = 0;
                bar.sliderMaximumValue = Maximum;
                bar.sliderInitialValue = 0;
                if (drawMotion == null) continue;
                for (int level = 1; level <= 3; level++)
                {
                    string name = IconPrefix + index + ":" + level;
                    if (parent.subItems.Any(x => x.name == name)) continue;
                    var icon = Copy(bar);
                    icon.name = name;
                    icon.layoutType = MenuSettings.MenuItem.LayoutType.IMAGE_PANEL;
                    icon.displayType = MenuSettings.MenuItem.DisplayType.IMAGE;
                    // IMAGE_PANEL renders an NSpriteSet motion, not MenuItem.image.
                    icon.animationSpriteId = sprite.guId;
                    icon.drawAnimationMotionId = drawMotion.sprite.getGuid();
                    icon.appearAnimationMotionId = Guid.Empty;
                    icon.disappearAnimationMotionId = Guid.Empty;
                    icon.decisionAnimationMotionId = Guid.Empty;
                    icon.useText = false;
                    icon.text = "";
                    icon.sliderVariable = "";
                    icon.size = new Vector2(bar.size.X * 21f / 143f, bar.size.Y);
                    // Centres of the three diamonds in the 143 x 21 background image.
                    float center = level == 1 ? 38.5f : level == 2 ? 85.5f : 131.5f;
                    icon.pos = bar.pos + new Vector2((center / 143f - 0.5f) * bar.size.X, 0);
                    parent.subItems.Add(icon);
                }
            }
        }

        private static void PlayReadySound(Catalog catalog)
        {
            var sound = catalog.getItemFromName("SE_Item_Use_03", typeof(Yukar.Common.Resource.SoundResource))
                as Yukar.Common.Resource.SoundResource;
            if (sound == null) return;
            if (exReadySoundId < 0 || exReadySoundGuid != sound.guId)
            {
                exReadySoundId = Audio.LoadSound(sound);
                exReadySoundGuid = sound.guId;
            }
            if (exReadySoundId >= 0)
                Audio.PlaySound(exReadySoundId, 0f, Math.Max(0f, Math.Min(1f, sound.Volume / 100f)));
        }

        // forceImmediate is used immediately after the battle-status layout is shown.
        // It cancels the root layout's automatic appearance playback, avoiding the EX sound at battle start.
        internal static void Update(LayoutDrawer drawer, Catalog catalog, BattleSequenceManager battle,
            bool forceImmediate = false)
        {
            if (battle == null) return;
            var info = catalog.getGameSettings().GetCastStatusParamInfo(CurrentReference);
            if (info == null) return;
            bool displayedNewIcon = false;
            foreach (var obj in drawer.GetRenderObjects())
            {
                var name = obj.MenuItem?.name;
                if (name == null || !name.StartsWith(IconPrefix, StringComparison.Ordinal)) continue;
                var parts = name.Substring(IconPrefix.Length).Split(':');
                int index, level;
                if (parts.Length != 2 || !int.TryParse(parts[0], out index) || !int.TryParse(parts[1], out level)) continue;
                var players = battle.PlayerViewDataList;
                bool visible = index >= 0 && index < players.Count &&
                    players[index].battleStatusData.consumptionStatusValue.GetStatus(info.guId) >= level * 10;
                var state = IconDisplayStates.GetOrCreateValue(obj);
                obj.EnableDrawable(visible);
                if (forceImmediate || !state.Initialized)
                {
                    state.Initialized = true;
                    state.Visible = visible;
                }
                else if (visible != state.Visible)
                {
                    if (visible) displayedNewIcon = true;
                    state.Visible = visible;
                }
            }
            if (displayedNewIcon) PlayReadySound(catalog);
        }
    }
}
