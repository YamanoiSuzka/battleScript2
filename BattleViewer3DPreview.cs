using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Yukar.Common;
using Yukar.Common.Rom;
using Yukar.Engine;
using static Yukar.Engine.BattleViewer3DPreviewBase;

namespace Yukar.Battle
{
    /// <summary>
    /// 3Dバトルの表示をエディタ中でプレビューするためのクラス
    /// A class for previewing the display of 3D battles in the editor
    /// </summary>
    public class BattleViewer3DPreview : BattleViewer3DPreviewBase
    {
        private const int MAX_ENEMY_PREVIEW_COUNT = 8;

        internal List<BattleActor> friends = new List<BattleActor>(4);
        internal BattleActor[] enemies = new BattleActor[MAX_ENEMY_PREVIEW_COUNT];
        private GetUpdateInfo getUpdateInfo;
        private EffectDrawerBase effect;

        private bool hideMonsters;
        private bool isEnemySide;
        private float lastYAngle;
        private long recentTick;
        private EnemyParty.EditParam editParam;
        private Catalog catalog;
        private Map.BattleSetting btlSett;
        private bool enemyInitialized;

        public override bool HideMonsters
        {
            get { return hideMonsters; }
            set
            {
                hideMonsters = value;
                for (int i = 0; i < enemies.Length; i++)
                {
                    if (enemies[i] != null)
                    {
                        enemies[i].mapChr.hide = hideMonsters ? MapCharacter.HideCauses.BY_DEAD : MapCharacter.HideCauses.NONE;
                    }
                }
            }
        }

        public BattleViewer3DPreview(GetUpdateInfo infoGetter, Common.Catalog catalog, Common.Rom.Map.BattleSetting btlSett)
        {
            BattleActor.map = null;
            var gs = catalog.getGameSettings();
            getUpdateInfo = infoGetter;

            // 味方キャラを読み込む
            // load an ally character
            int count = -1;
            int max = Math.Min(gs.party.Count(x => x != Guid.Empty), gs.BattlePlayerMax);

            var addCnt = max - friends.Count;

            for (int i = 0; i < addCnt; i++)
            {
                friends.Add(null);
            }

            var lyt = btlSett?.layout?.PlayerLayouts;
            for (int i = 0; i < max; i++)
            {
                var chr = gs.party[i];

                if (chr == Guid.Empty)
                    continue;
                var chrRom = catalog.getItemFromGuid(chr) as Common.Rom.Cast;
                if (chrRom == null)
                    continue;
                count++;
                friends[count] = BattleActor.GenerateFriend(catalog, null, chrRom, count % max, max);
                friends[count].mapChr.setHeroSymbol(false);

                if (lyt != null && lyt.Length > count)
                {
                    var pos = lyt[count];
                    friends[count].mapChr.pos = pos + BattleSequenceManagerBase.battleFieldCenter;
                }

                // バトルキャストのY座標を確定させるために、一度更新処理を行う
                // Perform the update process once to confirm the Y coordinate of the battle cast.
                getUpdateInfo(out var drawer, out var yangle);
                friends[count].Update(drawer, yangle, false);
            }

            this.catalog = catalog;
            this.btlSett = btlSett;

            effect = new EffectDrawer();
        }

        public override void setEditParam(EnemyParty.EditParam editParam)
        {
            this.editParam = editParam;
        }

        public override void finalize()
        {
            // キャラを破棄
            // discard character
            for (int i = 0; i < friends.Count; i++)
            {
                if (friends[i] != null)
                {
                    friends[i].Release();
                    friends[i] = null;
                }
            }
            for (int i = 0; i < enemies.Length; i++)
            {
                if (enemies[i] != null)
                {
                    enemies[i].Release();
                    enemies[i] = null;
                }
            }
            effect.finalize();
        }

        private void Update(MapData drawer, float yangle)
        {
            if (!enemyInitialized)
                InitializeEnemies();

            foreach (var mapChr in friends)
            {
                if (mapChr == null)
                    continue;

                mapChr.Update(drawer, yangle, false);
            }
            foreach (var mapChr in enemies)
            {
                if (mapChr == null)
                    continue;

                mapChr.Update(drawer, yangle, false);
            }

            lastYAngle = yangle;
            if (startTime + TimeSpan.FromMilliseconds(500) < DateTime.Now)  // エフェクトは再生開始から一定時間発生待ちにする / Wait for the effect to occur for a certain period of time from the start of playback
                effect.update();
        }

        /// <summary>
        /// 敵キャラを読み込む
        /// load an enemy character
        /// EditParam利用タイプの実装があるとは限らないため、初回Updateで読み込むようにする
        /// Since there is not necessarily an implementation of the EditParam usage type, it should be loaded on the first update.
        /// </summary>
        /// <exception cref="NotImplementedException"></exception>
        private void InitializeEnemies()
        {
            // 敵が指定されている？
            // Has the enemy been specified?
            if ((editParam?.EnemyCasts.Count ?? 0) > 0)
            {
                var max = Math.Min(editParam.EnemyCasts.Count, enemies.Length);
                for (int count = 0; count < max; count++)
                {
                    var chr = catalog.getItemFromGuid(editParam.EnemyCasts[count]) as Common.Rom.Cast;
                    if (chr != null)
                    {
                        var grp = catalog.getItemFromGuid(chr.Graphics3D) as Common.Resource.GfxResourceBase;
                        enemies[count] = BattleActor.GenerateEnemy(catalog, new BattleEnemyData() { image = grp }, count, max);
                        enemies[count].mapChr.setHeroSymbol(false);
                        Vector3[] lyt = editParam.EnemyLayouts;
                        if (lyt != null && lyt.Length > count)
                        {
                            var pos = lyt[count];
                            enemies[count].mapChr.pos = pos + BattleSequenceManagerBase.battleFieldCenter;
                        }
                    }
                }
            }
            // 指定されていない場合はランダムに敵を選ぶ
            // If not specified, randomly select enemy
            else
            {
                var count = 0;
                var monsters = catalog.getFilteredItemList(typeof(Common.Rom.Cast));
                var lyts = btlSett?.layout?.EnemyLayoutsEveryNumber;
                if (monsters.Count > 0)
                {
                    var rand = new Random();

                    var max = rand.Next(1, enemies.Length + 1);
                    if (lyts != null)
                        max = Math.Min(max, lyts.Length);

                    for (; count < max; count++)
                    {
                        var chr = monsters[rand.Next(monsters.Count)] as Common.Rom.Cast;
                        var grp = catalog.getItemFromGuid(chr.Graphics3D) as Common.Resource.GfxResourceBase;
                        enemies[count] = BattleActor.GenerateEnemy(catalog, new BattleEnemyData() { image = grp }, count, max);
                        enemies[count].mapChr.setHeroSymbol(false);
                        Vector3[] lyt = null;
                        if (lyts != null)
                            lyt = lyts[max - 1];
                        if (lyt != null && lyt.Length > count)
                        {
                            var pos = lyt[count];
                            enemies[count].mapChr.pos = pos + BattleSequenceManagerBase.battleFieldCenter;
                        }
                    }
                }
            }

            for (int i = 0; i < enemies.Length; i++)
            {
                if (enemies[i] != null)
                {
                    enemies[i].mapChr.setDisplayID(EffectDrawer3D.sDisplayID);
                }
            }

            enemyInitialized = true;
        }

        public override void draw(SharpKmyGfx.Render scn)
        {
            var now = DateTime.Now;
            var elapsed = (float)(now.Ticks - recentTick) / 10000000;
            Engine.GameMain.setElapsedTime(elapsed);
            recentTick = now.Ticks;

            MapData drawer;
            float yangle;
            getUpdateInfo(out drawer, out yangle);
            Update(drawer, yangle);

            foreach (var mapChr in friends)
            {
                if (mapChr == null)
                    continue;

                mapChr.Draw(scn);
            }

            if (!HideMonsters)
            {
                foreach (var mapChr in enemies)
                {
                    if (mapChr == null)
                        continue;

                    mapChr.Draw(scn);
                }
            }

            if (startTime + TimeSpan.FromMilliseconds(500) < DateTime.Now && !effect.isEndPlaying)
            {
                if (effect.origPos == Common.Resource.NSprite.OrigPos.ORIG_SCREEN)
                {
                    // 画面全体を対象としたエフェクトの場合、ターゲット色だけを反映して、エフェクト自体は画面全体に出す
                    // In the case of an effect that targets the entire screen, only the target color is reflected, and the effect itself is displayed on the entire screen.
                    effect.draw(Graphics.ScreenWidth / 2, Graphics.ScreenHeight / 2);
                }
                else
                {
                    BattleActor skillUser, actor;
                    if (isEnemySide)
                    {
                        skillUser = friends[0];
                        actor = enemies[0];
                    }
                    else
                    {
                        skillUser = enemies[0];
                        actor = friends[0];
                    }
                    effect.drawFlash();

                    var p = SharpKmyMath.Matrix4.identity();
                    var v = SharpKmyMath.Matrix4.identity();
                    scn.getViewMatrix(ref p, ref v);

                    // 位置決め(2D用)
                    // Positioning (for 2D)
                    //var color = effect.getNowTargetColor();
                    var target = actor.getScreenPos(p, v, MapScene.GetEffectPosType(effect.origPos));

                    // 描画
                    // drawing
                    effect.draw((int)target.X, (int)target.Y, false);
                    effect.drawFor3D(skillUser.mapChr, actor.mapChr, 1, lastYAngle);
                }
            }
        }

        public override SharpKmyMath.Vector3 getFriendPos(int p)
        {
            if (friends.Count == 0 || p >= friends.Count)
                return new SharpKmyMath.Vector3();
            if (friends[p] == null)
                return new SharpKmyMath.Vector3();
            return friends[p].getPos();
        }

        public override SharpKmyMath.Vector3 getEnemyPos(int p)
        {
            if (enemies.Length == 0 || p >= enemies.Length)
                return new SharpKmyMath.Vector3();
            if (enemies[p] == null)
                return new SharpKmyMath.Vector3();
            return enemies[p].getPos();
        }

        public override float getFriendHeight(int p)
        {
            if (friends.Count == 0 || p >= friends.Count)
                return 0;
            return (friends[p] == null) ? 0 : friends[p].Height;
        }

        public override void setDisplayID(uint displayID)
        {
            for (int i = 0; i < friends.Count; i++)
            {
                if (friends[i] != null)
                {
                    friends[i].mapChr.setDisplayID(displayID);
                }
            }
            for (int i = 0; i < enemies.Length; i++)
            {
                if (enemies[i] != null)
                {
                    enemies[i].mapChr.setDisplayID(displayID);
                }
            }
            EffectDrawer3D.sDisplayID = displayID;
        }

        public override void setFriendDirection(int inIdx, float inDirection)
        {
            if (friends.Count > 0 && inIdx < friends.Count)
                friends[inIdx].mapChr.setDirectionFromRadian(inDirection / 180 * (float)Math.PI);
        }

        public override void setEffect(Common.Catalog catalog, Guid guid, bool enemyEffect)
        {
            effect.finalize();
            var rom = catalog.getItemFromGuid(guid);
            if (rom == null)
            {
                effect = new EffectDrawer();
            }
            else
            {
                effect = EffectDrawerBase.createAndLoad(rom, catalog);
            }
            effect.initialize();
            isEnemySide = enemyEffect;
        }

        public override bool IsEndPlayEffect()
        {
            return effect.isEndPlaying;
        }
    }
}
