#define ENABLE_TEST
using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Yukar.Common;
using Yukar.Common.Rom;
using System.Linq;
using Yukar.Engine;
using static Yukar.Engine.BattleEnum;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Security.Cryptography;

namespace Yukar.Battle
{
    /// <summary>
    /// バトルイベント管理クラス
    /// Battle event management class
    /// </summary>
    public class BattleEventController : BattleEventControllerBase
    {
        Queue<MemberChangeData> memberChangeQueue = new Queue<MemberChangeData>();
		internal Queue<MemberChangeData> MemberChangeQueue { get => memberChangeQueue; }

        // BTL_APPEARで生存中に消去したモンスターの元配置を UniqueID→moveTargetPos で保持する
        // Maintain the original location of monsters deleted while alive with BTL_APPEAR using UniqueID→moveTargetPos
        // (消去後は enemyData / defeatedEnemyData のどちらにも残らないため別途保存)
        // (After deletion, it will not remain in either enemyData / defeatedEnemyData, so save it separately)
        Dictionary<int, Vector3> savedSlotPositions = new Dictionary<int, Vector3>();

		private List<MapCharacter> dummyChrs = new List<MapCharacter>();
        private List<MapCharacter> extras = new List<MapCharacter>();
        private Catalog catalog;
        private BattleSequenceManager battle;

        public bool battleUiVisibility = true;
        private List<ScriptRunner> mapRunnerBorrowed = new List<ScriptRunner>();

        /// <summary>
        /// メンバー交代をキューイングしておくための構造体
        /// Structure for queuing member changes
        /// </summary>
        internal class ActionData
        {
            internal Script.Command cmd;
            internal Guid evGuid;
        }
        private List<ActionData> actionQueue = new List<ActionData>();

        private BattleResultState reservedResult = BattleResultState.NonFinish;
        internal Vector3[] playerLayouts;
        internal bool isBattleEndEventStarted;
        private bool isBattleForciblyTerminate;
        private int currentProcessingTrigger = -1;

        // メンバーチェンジ用
        // For member change
        private LayoutManager memberChangeUi;
        private BattleViewer3D.BattleUI.LayoutDrawerController overridedMemberChangeUi;
        private List<BattlePlayerData> originalBattleParty;
        private List<Common.GameData.Hero> originalReserves;

        public override CameraManager CameraManager { get => (battle.battleViewer as BattleViewer3D)?.camManager; }

        public override SharpKmyMath.Vector2 ShakeValue
        {
            set
            {
                var viewer = battle.battleViewer as BattleViewer3D;
                if (viewer != null)
                {
                    viewer.shakeValue = value;
                }

                base.ShakeValue = value;
            }
        }

        public BattleEventController() : base()
        {
            var dummyRef = new Map.EventRef();
            hero = new MapCharacter(dummyRef);
            hero.Name = "Player";
            isBattle = true;
        }

        internal void init(BattleSequenceManager battle, Catalog catalog,
            List<BattlePlayerData> playerData, List<BattleEnemyData> enemyMonsterData,
            MapEngine mapEngine)
        {
            this.battle = battle;
            this.catalog = catalog;

            // CALL_BATTLE_CS2 の呼び出し対象を登録する（this と battle のみをデフォルト登録）
            // Register the call target of CALL_BATTLE_CS2 (only this and battle are registered by default)
            battlePluginScripts.Clear();
            battlePluginScripts.Add(this);
            battlePluginScripts.Add(battle);
            this.mapEngine = mapEngine;

            // バトル開始時のパーティ情報を記録する（レイアウト再生成時に上書きされないよう）
            // Record party information at the start of the battle (so that it is not overwritten when regenerating the layout)
            originalBattleParty = playerData.ToList();
            originalReserves = GameMain.instance.data.party.Reserves.ToList();

            // バトルイベントを初期化する
            // Initialize battle event
            foreach (var guid in catalog.getGameSettings().battleEvents)
            {
                var ev = catalog.getItemFromGuid<Event>(guid);
                if (ev == null || !ev.IsValid())
                    continue;

                AddEvent(ev);
            }

            // 敵パーティ固有のバトルイベントを追加する
            // Add enemy party-specific battle events
            if (battle.currentEnemyParty != null)
            {
                var rom = battle.currentEnemyParty;

                // 他の敵パーティとバトルイベントを共有している場合はそちらを使う
                // If you are sharing a battle event with another enemy party, use that.
                var shareTarget = catalog.getItemFromGuid<EnemyParty>(rom.ShareBattleEventsGuid);
                if (shareTarget != null)
                    rom = shareTarget;

                foreach (var guid in rom.battleEvents)
                {
                    var ev = catalog.getItemFromGuid<Event>(guid);
                    if (ev == null || !ev.IsValid())
                        continue;

                    AddEvent(ev);
                }
            }

            memberChangeQueue.Clear();
            savedSlotPositions.Clear();
            extras = null;
            cameraControlMode = Map.CameraControlMode.NORMAL;
        }

        internal void AddEvent(Event inEvent, RomItem parentRom = null)
        {
            var newEventRef = new Map.EventRef();
            newEventRef.guId = inEvent.guId;
            newEventRef.pos.X =
            newEventRef.pos.Z = -1;

            if ((parentRom is NItem item) && item.useEnhance)
            {
                newEventRef.guIdCast = item.guId;
            }

            var dummyChr = new MapCharacter(inEvent, newEventRef, this, false, false, true);
            checkAllSheet(dummyChr, true, true, false, parentRom);
            dummyChrs.Add(dummyChr);
        }

        private void initBattleFieldPlacedEvents()
        {
            if (extras != null)
                return;

            var v3d = battle.battleViewer as BattleViewer3D;

            if (v3d == null)
                return;

            // マップ配置イベントの表示状態を更新する
            // Update the display state of the map placement event
            extras = v3d.extras;
            foreach (var chr in extras)
            {
                checkAllSheet(chr, true, false, true);
            }

            // バトルフィールドと移動マップが一致している場合、グラフィック変更や削除などの状態もチェックする
            // If the battlefield and movement map match, also check the state of graphic changes, deletions, etc.
            if (v3d.mapDrawer.mapRom.guId == owner.mapScene.map.guId)
            {
                foreach (var chr in extras.ToArray())
                {
                    var original = owner.mapScene.mapCharList.FirstOrDefault(x => x.guId == chr.rom.guId);

                    // 既に消えている場合は削除
                    // Delete if already gone
                    if (original == null)
                    {
                        chr.Reset();
                        extras.Remove(chr);
                    }
                    else
                    {
                        // 向き、位置をコピー
                        // copy direction and position
                        chr.setPosition(original.getPosition());
                        chr.setRotation(original.getRotation());

                        // グラフィックが変化していれば反映
                        // Reflect if graphics change
                        var res = original.getGraphic() as Common.Resource.GfxResourceBase;
                        if (chr.getGraphic() != res)
                            chr.ChangeGraphic(res, v3d.mapDrawer);

                        // スケール、透明状態、モーションをコピー
                        // Copy scale, transparency and motion
                        chr.setScale(original.getScale());
                        chr.playMotion(original.currentMotion);
                        chr.hide = original.hide;

                        // サブグラフィックの状態もコピー
                        // Copy subgraphic state
                        chr.LoadSubGraphicState(original.SaveSubGraphicState().ToList());
                    }
                }
            }
        }

        /// <summary>
        /// バトル終了時のパーティデータをマップ用に復元する
        /// Restore the party data at the end of the battle for the map
        /// </summary>
        internal void RestoreParty()
        {
            var mapScene = owner.mapScene;
            var data = owner.data;

            // 終了・リセット時には menuWindow が null になるので実行しない
            // When exiting or resetting, menuWindow will be null, so it will not be executed.
            if (mapScene?.menuWindow == null)
                return;

            if (originalBattleParty == null)
                return;

            if (catalog.getGameSettings().KeepBattleParty)
            {
                // バトル時のパーティを維持する場合は差分があればマップキャラを更新する
                // If you want to maintain the party during battle, update the map characters if there are differences.
                var isDifferent = false;
                if (data.party.Members.Count != originalBattleParty.Count)
                {
                    isDifferent = true;
                }
                else
                {
                    for (int i = 0; i < originalBattleParty.Count; i++)
                    {
                        if (data.party.Members[i] != originalBattleParty[i].player)
                        {
                            isDifferent = true;
                            break;
                        }
                    }
                }

                if (isDifferent)
                    mapScene?.RefreshHeroMapChr();
            }
            else
            {
                // 元のパーティデータを書き戻す
                // Write back original party data
                data.party.Members.Clear();
                data.party.Members.AddRange(originalBattleParty.Select(x => x.player));
                data.party.ReservesRaw.Clear();
                data.party.ReservesRaw.AddRange(originalReserves);
            }
        }

        internal void term()
        {
            owner?.HideALLFreeLayout(true);

            foreach (var runner in runnerDic.getList())
            {
                runner.finalize();
            }
            runnerDic.Clear();

            foreach (var mapChr in dummyChrs)
            {
                mapChr.Reset();
            }
            dummyChrs.Clear();

            foreach (var runner in mapRunnerBorrowed)
            {
                runner.owner = owner.mapScene;
            }
            mapRunnerBorrowed.Clear();

            foreach (var mapChr in mapCharList)
            {
                mapChr.Reset();
            }
            mapCharList.Clear();

            releaseMenu();
            spManager?.Clear();
            spManager = null;

            if (catalog.getGameSettings().KeepBattleParty)
                IsSurvivingReserveExists();// マップ画面へのパーティ反映用にメンバー情報を収集する / Collect member information to reflect the party on the map screen

            if (memberChangeUi != null)
            {
                memberChangeUi.Release();
                memberChangeUi = null;
            }

            // パーティデータを復元する
            // Restore party data
            RestoreParty();

            originalBattleParty = null;
            originalReserves = null;
            owner = null;
        }

        internal void update()
        {
            if (owner == null || battle == null)
                return;

            // バトルビューアからのカメラ情報をセットしておく
            // Set the camera information from the battle viewer
            var viewer = battle.battleViewer as BattleViewer3D;
            if (viewer != null)
            {
                // 前フレームのデータを反映(heroには注視点を入れておく)
                // Reflect the data of the previous frame (put the gaze point in the hero)
#if false
                var now = viewer.camera.Now;
                hero.pos = now.offset;
                xAngle = now.angle.X;
                yAngle = now.angle.Y;
                dist = now.distance;
                eyeHeight = now.eyeHeight;
                fovy = now.fov;
                nearClip = now.nearClip;
#else
                var camManager = viewer.camManager;
                Quaternion qt = camManager.camQuat;
                Vector3 rot = new Vector3();

                camManager.convQuaternionToRotation(qt, out rot);

                hero.pos = new Vector3(camManager.m_intp_target.x, camManager.m_intp_target.y, camManager.m_intp_target.z);
                //xAngle = camManager.m_view_angle.x;
                //yAngle = camManager.m_view_angle.y;
                //dist = camManager.m_distance;
                //camOffset = camManager.m_last_offset;
                //fovy = camManager.m_fovy;
                //nearClip = camManager.m_nearClip;
#endif

                mapDrawer = viewer.mapDrawer;
                map = viewer.mapDrawer.mapRom;
            }

            // バトルフィールドに置いてあるイベントの初期化
            // Initialize the event placed in the battlefield
            initBattleFieldPlacedEvents();

            // スクリプト処理直前のCSharp割り込み
            // CSharp interrupt just before script processing
            foreach (var mapChr in dummyChrs)
            {
                mapChr.BeforeUpdate();
            }

            // イベント処理
            // event handling
            var isEventProcessed = procScript();
            if (!isEventProcessed)
            {
                // シートチェンジ判定
                // Sheet change judgment
                foreach (var mapChr in dummyChrs)
                {
                    checkAllSheet(mapChr, false, true, false);
                    mapChr.UpdateOnlyScript();
                }
                if (extras != null)
                {
                    foreach (var chrs in extras)
                    {
                        checkAllSheet(chrs, false, false, true);
                    }
                }
            }

            // 各ウィンドウのアップデート
            // Update each window
            updateWindows();
            spManager.Update();
            memberChangeUi?.Update();

            // Face用のキャラクターを更新
            // Updated characters for Face
            foreach (var mapChr in mapCharList)
            {
                mapChr.update();
            }
            owner.mapScene.UpdateFace();
        }

        private bool procScript()
        {
            bool result = false;

            // 通常スクリプトのアップデート
            // Normal script update
            var runners = runnerDic.getList().Union(mapRunnerBorrowed).ToArray();
            foreach (var runner in runners)
            {
                if (exclusiveRunner == null || (runner == exclusiveRunner && !exclusiveInverse) ||
                    (runner != exclusiveRunner && exclusiveInverse))
                {
                    if (!runner.isParallelTriggers())
                    {
                        bool isFinished = runner.Update();
                        // 完了したスクリプトがある場合は、ページ遷移をチェックする
                        // Check page transitions if there is a completed script
                        if (isFinished)
                            break;
                        // 並列動作しないので、自動移動以外は最初に見つかったRunningしか実行しない
                        // Since it does not operate in parallel, only the first found Running other than automatic movement is executed
                        if (runner.state == ScriptRunner.ScriptState.Running)
                        {
                            result = true;
                            break;
                        }
                    }
                }
            }

            // その他の並列スクリプトのアップデート
            // Other parallel script updates
            foreach (var runner in runners)
            {
                if (runner.isParallelTriggers() || runner.isEffectTriggers())
                {
                    runner.Update();
                }
            }

            return result;
        }

        internal void Draw(SharpKmyGfx.Render scn)
        {
            foreach (var mapChr in mapCharList)
            {
                mapChr.draw(scn);
            }
        }

        internal new void Draw()
        {
            if (owner == null)
                return;

            Graphics.BeginDraw();

            DrawMovies(0);

            // スプライト描画
            // sprite drawing
            spManager.Draw(0, SpriteManager.SYSTEM_SPRITE_INDEX, 0);

            owner.mapScene.DrawFace();

            // エフェクト描画
            // effect drawing
            DrawEffects();
            DrawMovies(1);

            // スプライト描画 優先度・中
            // Sprite drawing priority medium
            spManager.Draw(0, SpriteManager.SYSTEM_SPRITE_INDEX, 1);

            // スクリーンカラーを適用
            // Apply Screen Color
            Graphics.DrawFillRect(0, 0, Graphics.ViewportWidth, Graphics.ViewportHeight,
                screenColor.R, screenColor.G, screenColor.B, screenColor.A);

            // スプライト描画(顔グラフィック)
            // Sprite drawing (face graphics)
            spManager.Draw(SpriteManager.SYSTEM_SPRITE_INDEX, SpriteManager.MAX_SPRITE);

            // フリーレイアウト描画
            // free layout drawing
            if (battle.battleState < BattleState.FinishFadeIn)
                owner.DrawFreeLayouts(true);

            // ウィンドウ描画
            // window drawing
            drawWindows();
            memberChangeUi?.Draw();

            // ムービー描画
            // movie drawing
            DrawMovies(2);

            // スプライト描画 優先度・高
            // Sprite drawing priority/high
            spManager.Draw(0, SpriteManager.SYSTEM_SPRITE_INDEX, 2);

            // スクリプトからの描画
            // drawing from script
            foreach (var mapChr in dummyChrs)
            {
                mapChr.AfterDraw();
            }

            Graphics.EndDraw();
        }

        public override void GetCharacterScreenPos(MapCharacter chr, out int x, out int y, EffectPosType pos = EffectPosType.Ground, Vector3? offset = null)
        {
            var viewer = battle.battleViewer as BattleViewer3D;
            SharpKmyMath.Matrix4 p, v;
            if (viewer != null)
            {
                var asp = owner.getScreenAspect();
                // 読み取り専用呼び出し：カメラアニメ時間を進めないようにする
                // Read-only call: Prevent camera animation time from advancing
                // (HLVARIABLE 経由で並列イベントから呼ばれた際、cam_battle_use_skill 等の
                // (When called from a parallel event via HLVARIABLE, cam_battle_use_skill etc.
                //  再生フレームが余分に進み、ダメージ表示中にカメラが地面に潜る不具合への対策)
                // (Countermeasures for the issue where the playback frame advances extra and the camera goes into the ground while damage is being displayed)
                viewer.createCameraMatrix(out p, out v/*, viewer.camera.Now*/, asp, advanceTime: false);
                GetCharacterScreenPos(chr, out x, out y, p, v, pos, offset);
            }
            else
            {
                x = y = 10000;
            }
        }

        public override void SetEffectColor(MapCharacter selfChr, Color color)
        {
            var viewer = battle.battleViewer as BattleViewer3D;
            if (viewer == null)
                return;

            var actor = viewer.searchFromActors(selfChr);
            if (actor == null)
                return;

            actor.overRidedColor = color;
        }

        public void start(Script.Trigger trigger)
        {
            switch (trigger)
            {
                case Script.Trigger.BATTLE_END:
                    // BTL_STOP 命令で再突入する可能性があるので処理しないようにする
                    // Do not process the BTL_STOP instruction as it may re-enter
                    if (isBattleEndEventStarted)
                        return;

                    isBattleEndEventStarted = true;
                    break;

                case Script.Trigger.BATTLE_BEFORE_ACTION:
                    currentProcessingTrigger = 3;
                    break;
                case Script.Trigger.BATTLE_AFTER_ACTION:
                    currentProcessingTrigger = 4;
                    break;
                case Script.Trigger.BATTLE_BEFORE_COMMAND_SELECT:
                    currentProcessingTrigger = 5;
                    break;
                case Script.Trigger.BATTLE_AFTER_COMMAND_SELECT:
                    currentProcessingTrigger = 6;
                    break;
                case Script.Trigger.BATTLE_CANCEL_COMMAND_SELECT:
                    currentProcessingTrigger = 7;
                    break;
                case Script.Trigger.BATTLE_AFTER_RESULT:
                    currentProcessingTrigger = 8;
                    break;
            }

            if (currentProcessingTrigger >= 0)
            {
                // アクティブキャラクターによる条件パネル判定のために今一度CheckAllSheetする
                // CheckAllSheet again to check the condition panel based on the active character
                foreach (var mapChr in dummyChrs)
                {
                    checkAllSheet(mapChr, false, true, false);
                }
            }

            foreach (var runner in runnerDic.getList().ToArray())
            {
                if (runner.state == ScriptRunner.ScriptState.Running)
                    runnerDic.bringToFront(runner);

                if (runner.Trigger == trigger)
                    runner.Run();
            }
        }

        internal bool isBusy(bool gaugeUpdate = true)
        {
            if (battle == null)
                return false;

            // ステータス変動によるゲージアニメをここでやってしまう
            // I will do a gauge animation by status change here
            bool isUpdated = false;
            if (gaugeUpdate)
            {
                foreach (var player in battle.playerData)
                {
                    isUpdated |= battle.UpdateBattleStatusData(player);
                }
                foreach (var enemy in battle.enemyData)
                {
                    isUpdated |= battle.UpdateBattleStatusData(enemy);
                }
                if (isUpdated)
                {
                    battle.statusUpdateTweener.Update();
                }
            }

            // メンバーチェンジを処理する
            // Handle member changes
            if (procMemberChange())
                isUpdated = true;

            // コマンド指定を処理する
            // process command specification
            if (battle.battleState == BattleState.Wait || battle.battleState == BattleState.PlayerTurnStart)// バトルプラグインではWaitを通らずPlayerTurnStartになる / In the battle plugin, it is PlayerTurnStart without passing Wait.
            {
                foreach (var action in actionQueue)
                {
                    if (action.cmd.type == Script.Command.FuncType.BTL_ACTION)
                    {
                        procSetAction(action.cmd, action.evGuid);
                    }
                }
                actionQueue.Clear();
            }

            var isResultInit = battle.battleState == BattleState.ResultInit;

            foreach (var runner in runnerDic.getList())
            {
                if ((!runner.isParallelTriggers() || (isResultInit && runner.isEffectTriggers())) &&
                    runner.state == ScriptRunner.ScriptState.Running)
                {
                    return true;
                }
            }

            foreach (var runner in mapRunnerBorrowed)
            {
                if (runner.state == ScriptRunner.ScriptState.Running)
                {
                    return true;
                }
            }

            // ダメージ用テキストとステータス用ゲージのアニメーションが終わるまで待つ
            // Wait for damage text and status gauge animation to finish
            if (isUpdated)
                return true;

            return false;
        }

        private void procSetAction(Script.Command curCommand, Guid evGuid)
        {
            int cur = 0;
            var tgt = getTargetData(curCommand, ref cur, evGuid);
            if (tgt == null)
                return;

            tgt.lastHitCheckResult = BattleCharacterBase.HitCheckResult.NONE;
            BattleCommand cmd = new BattleCommand();
            switch (curCommand.attrList[cur++].GetInt())
            {
                case 0:
                    {
                        cur++;// オプション用引数の数を合わせるために予約してある 今は意味のない0が入っているのでスキップ / Reserved to match the number of option arguments.Currently, there are meaningless 0s, so skip it.
                        cmd.type = BattleCommand.CommandType.ATTACK;
                        cmd.power = 100;
                        tgt.selectedBattleCommandType = BattleCommandType.Attack;
                        tgt.selectedBattleCommand = cmd;
                        battle.battleViewer.commandTargetSelector.Clear();
                        var tgt2 = getTargetData(curCommand, ref cur, evGuid);
                        if (tgt2 != null)
                        {
                            battle.battleViewer.commandTargetSelector.AddBattleCharacters(new List<BattleCharacterBase>() { tgt2 });
                            battle.battleViewer.commandTargetSelector.SetSelect(tgt2);
                        }
                        tgt.targetCharacter = battle.GetTargetCharacters(tgt);

                        // ターゲット無効かどうかチェックする
                        // Check if the target is disabled
                        if (tgt2 == null && tgt.targetCharacter.ElementAtOrDefault(0) == null)
                        {
                            battle.CheckAndDoReTarget(tgt);
                        }
                    }
                    break;
                case 1:
                    cmd.type = BattleCommand.CommandType.GUARD;
                    cmd.power = (int)ScriptRunner.GetNumOrVariable(owner, evGuid, curCommand.attrList[cur++], false);
                    tgt.selectedBattleCommandType = BattleCommandType.Guard;
                    tgt.selectedBattleCommand = cmd;
                    tgt.targetCharacter = battle.GetTargetCharacters(tgt);
                    break;
                case 2:
                    cmd.type = BattleCommand.CommandType.CHARGE;
                    cmd.power = (int)ScriptRunner.GetNumOrVariable(owner, evGuid, curCommand.attrList[cur++], false);
                    tgt.selectedBattleCommandType = BattleCommandType.Charge;
                    tgt.selectedBattleCommand = cmd;
                    tgt.targetCharacter = battle.GetTargetCharacters(tgt);
                    break;
                case 3:
                    bool skipMessage = false;
                    string message = "";
                    if (curCommand.attrList.Count > cur)
                    {
                        var option = curCommand.attrList[cur++];
                        if (option is Script.IntAttr)
                        {
                            // boolが入っている場合はそのままフラグとして解釈
                            // If bool is included, it is interpreted as a flag.
                            skipMessage = curCommand.attrList[cur++].GetBool();
                        }
                        else
                        {
                            // 文字列が入っている場合はメッセージとして解釈
                            // If it contains a string, it is interpreted as a message.
                            message = option.GetString();
                        }
                    }
                    if (skipMessage)
                    {
                        tgt.selectedBattleCommandType = BattleCommandType.Cancel;
                    }
                    else
                    {
                        tgt.selectedCommandCustomMessage = message;
                        tgt.selectedBattleCommandType = BattleCommandType.Nothing;
                    }
                    break;
                case 4:
                    var guid = curCommand.attrList[cur++].GetGuid();
                    var skill = owner.catalog.getItemFromGuid<NSkill>(guid);
                    if (skill != null)
                    {
                        cmd.type = BattleCommand.CommandType.SKILL;
                        cmd.guId = guid;
                        tgt.selectedBattleCommandType = BattleCommandType.Skill;
                        tgt.selectedItem = null;
                        tgt.selectedSkill = skill;
                        tgt.selectedBattleCommand = cmd;
                        battle.battleViewer.commandTargetSelector.Clear();
                        var tgt2 = getTargetData(curCommand, ref cur, evGuid);
                        if (tgt2 != null)
                        {
                            battle.battleViewer.commandTargetSelector.AddBattleCharacters(new List<BattleCharacterBase>() { tgt2 });
                            battle.battleViewer.commandTargetSelector.SetSelect(tgt2);
                        }
                        tgt.targetCharacter = battle.GetTargetCharacters(tgt);

                        // アクティブキャラクターのターゲットを変える場合、無効かどうかチェックする
                        // When changing the target of the active character, check if it is invalid
                        if (tgt2 == null && tgt.targetCharacter.ElementAtOrDefault(0) == null)
                        {
                            battle.CheckAndDoReTarget(tgt);
                        }
                    }
                    else
                    {
                        tgt.selectedBattleCommandType = BattleCommandType.Nothing;
                    }
                    break;
                case 5:
                    {
                        cur++;// オプション用引数の数を合わせるために予約してある 今は意味のない0が入っているのでスキップ / Reserved to match the number of option arguments.Currently, there are meaningless 0s, so skip it.
                        battle.battleViewer.commandTargetSelector.Clear();
                        var tgt2 = getTargetData(curCommand, ref cur, evGuid);
                        if (tgt2 != null)
                        {
                            battle.battleViewer.commandTargetSelector.AddBattleCharacters(new List<BattleCharacterBase>() { tgt2 });
                            battle.battleViewer.commandTargetSelector.SetSelect(tgt2);
                        }
                        tgt.targetCharacter = battle.GetTargetCharacters(tgt);

                        // アクティブキャラクターのターゲットを変える場合、無効かどうかチェックする
                        // When changing the target of the active character, check if it is invalid
                        if (tgt2 == null && tgt == battle.activeCharacter)
                        {
                            battle.CheckAndDoReTarget();
                        }
                    }
                    break;
                case 6:
                    cmd.type = BattleCommand.CommandType.ESCAPE;
                    if (tgt is BattlePlayerData)
                    {
                        battle.SkipPlayerBattleCommand(true);
                    }
                    else
                    {
                        tgt.selectedBattleCommandType = BattleCommandType.MonsterEscape;
                        tgt.selectedBattleCommand = cmd;
                    }
                    break;
            }

            if (tgt is BattlePlayerData)
                ((BattlePlayerData)tgt).forceSetCommand = true;
        }

        public override void applyCameraToBattle()
        {
            var viewer = battle.battleViewer as BattleViewer3D;

            // 待機カメラ再生中であればすぐに反映する
            // If the standby camera is playing, it will be reflected immediately.
            if(viewer?.camManager.ntpCamera?.name == Camera.NAME_BATTLE_WAIT &&
                viewer.camManager.ntpCamera.category != owner.data.system.currentBattleCameraSet)
            {
                viewer.PlayCameraAnimation(Camera.NAME_BATTLE_WAIT);
            }

#if false
            // カメラ情報を更新する
            // Update camera information
            var viewer = battle.battleViewer as BattleViewer3D;
            if (viewer != null)
            {
                // 前フレームのデータを反映(heroには注視点を入れておく)
                // Reflect the data of the previous frame (put the gaze point in the hero)
                var newCam = new ThirdPersonCameraSettings();
                newCam.offset = hero.pos;
                newCam.offset.Y -= viewer.camera.defaultHeight;
                newCam.angle.X = xAngle;
                newCam.angle.Y = yAngle;
                newCam.distance = dist;
                newCam.eyeHeight = eyeHeight;
                newCam.fov = fovy;
                newCam.nearClip = nearClip;
                viewer.camera.push(newCam, 0f);
            }
#endif
        }

        private bool procMemberChange()
        {
            // まだフェード中だったら次の処理をしない
            // If it's still fading, don't do the next process
            if (!battle.battleViewer.IsFadeEnd)
                return true;

            var viewer = battle.battleViewer as BattleViewer3D;
            if (viewer != null)
            {
                // まだ移動中だったら次の処理をしない
                // If it is still moving, do not proceed to the next step
                foreach (var actor in viewer.friends)
                {
                    if (actor == null)
                        continue;

                    if (actor.mapChr.moveMacros.Count > 0)
                        return true;
                }
            }

            // キューが空っぽだったら何もしない
            // do nothing if the queue is empty
            if (memberChangeQueue.Count == 0)
                return false;

            var entry = memberChangeQueue.Dequeue();
            entry.finished = true;

            if (entry.mob)
                return addRemoveMonster(entry, viewer);
            else
                return addRemoveParty(entry, viewer);
        }

        private bool addRemoveMonster(MemberChangeData entry, BattleViewer3D viewer)
        {
            // もうある場合はターゲットから消す
            // If there is more, remove it from the target
            var tgt = battle.enemyData.FirstOrDefault(x => x.UniqueID == entry.idx);

            // BTL_APPEARで座標指定がない場合に引き継ぐ配置位置を確定する
            // Determine the placement position to take over when no coordinates are specified with BTL_APPEAR
            // 優先順位: (1)現在の tgt → (2)倒済み defeatedEnemyData → (3)生存中に消去済み savedSlotPositions
            // Priority: (1) Current tgt → (2) Defeated defeatedEnemyData → (3) Deleted while alive savedSlotPositions
            Vector3? inheritedPosition = null;
            if (tgt != null)
            {
                inheritedPosition = tgt.moveTargetPos;
            }
            else
            {
                var defeated = battle.defeatedEnemyData.FirstOrDefault(x => x.UniqueID == entry.idx);
                if (defeated != null)
                    inheritedPosition = defeated.moveTargetPos;
                else if (savedSlotPositions.TryGetValue(entry.idx, out var savedPos))
                    inheritedPosition = savedPos;
            }

            if (tgt != null)
            {
                // 生存中に消去される場合は、後で同スロットへ再出現するときのために位置を保存しておく
                // If it is deleted while it is still alive, save its position in case it reappears in the same slot later.
                if (!tgt.IsDeadCondition())
                    savedSlotPositions[entry.idx] = tgt.moveTargetPos;

                battle.enemyData.Remove(tgt);
                battle.targetEnemyData.Remove(tgt);

                battle.battleViewer.AddFadeOutCharacter(tgt);
                battle.removeVisibleEnemy(tgt);

                tgt.selectedBattleCommandType = BattleCommandType.Skip;

                if (tgt.IsDeadCondition())
                    battle.defeatedEnemyData.Add(tgt);
            }

            if (catalog.getItemFromGuid((Guid)entry.id) == null)
                return true;

            var data = battle.addEnemyData((Guid)entry.id, entry.layout, entry.idx, entry.level);

            // 座標指定がなく元スロットの位置情報がある場合は引き継ぐ
            // If coordinates are not specified and there is position information of the original slot, it will be inherited.
            // (カスタムレイアウト・デフォルトレイアウト・生存中消去いずれも対応)
            // (Supports custom layout, default layout, and deletion while alive)
            if (entry.layout == null && inheritedPosition.HasValue)
            {
                data.pos = inheritedPosition.Value;
                data.moveTargetPos = inheritedPosition.Value;
                data.arrangmentType = BattleEnemyData.MonsterArrangementType.Manual;
            }

            var index = Math.Max(0, Math.Min(entry.idx - 1, battle.enemyData.Count));
            battle.enemyData.Insert(index, data);
            battle.addVisibleEnemy(data);
            battle.stockEnemyData.Remove(data);
            battle.targetEnemyData.Insert(index, data);
            data.imageAlpha = 0;
            battle.battleViewer.AddFadeInCharacter(data);
            if (viewer != null)
            {
                // entry.layout 指定あり、または元スロットの位置を引き継いだ場合はカスタム扱い
                // If entry.layout is specified or the position of the original slot is inherited, it will be treated as a custom
                bool useCustomLayout = entry.layout.HasValue || inheritedPosition.HasValue;
                var actor = viewer.AddEnemyMember(data, entry.idx, useCustomLayout);
                actor.queueActorState(BattleActor.ActorStateType.APPEAR, "walk", (int)BattleActor.ESCAPE_MAX_COUNT);
                actor.queueActorState(BattleActor.ActorStateType.APPEAR_END);
                actor.setConditionOpacity(0);
            }

            //battle.battleViewer.refreshLayout(battle.playerData, battle.enemyData);
            return true;
        }

        private bool addRemoveParty(MemberChangeData entry, BattleViewer3D viewer)
        {
            var tgt = searchPartyFromId(entry.id);
            bool doRefreshLayout = false;

            if (entry.cmd == MemberChangeData.Command.ADD || entry.cmd == MemberChangeData.Command.ADD_NEW)
            {
                // 4人以上いたら加入できない
                // You cannot join if there are more than 4 people.
                if (battle.playerData.Count >= catalog.getGameSettings().BattlePlayerMax)
                    return true;

                Common.GameData.Hero hero = null;
                if (entry.cmd == MemberChangeData.Command.ADD)
                {
                    // 既に加入していたら何もしない
                    // If already subscribed, do nothing
                    if (tgt != null)
                        return true;

                    // 直接Heroインスタンスを指定している場合はそれを使う
                    // If you specify a Hero instance directly, use it
                    hero = entry.id as Common.GameData.Hero;

                    // 見つからなければパーティからGUIDベースで探す
                    // If not found, search based on GUID from party
                    if (hero == null)
                    {
                        if (!(entry.id is Guid))
                            return true;

                        hero = owner.data.party.GetHero((Guid)entry.id);
                    }
                }
                else if (entry.cmd == MemberChangeData.Command.ADD_NEW)
                {
                    // まだ戦闘に加入していないパーティキャラを探す
                    // Find party characters who have not yet joined the battle
                    hero = owner.data.party.getFullList().FirstOrDefault(x => x.rom.guId == (Guid)entry.id && !battle.playerData.Exists(y => y.player == x));

                    // 見つからなければ新規に生成
                    // If not found, create a new one
                    if (hero == null)
                        hero = Common.GameData.Party.createHeroFromRom(catalog, catalog.getItemFromGuid<Cast>((Guid)entry.id));
                }

                if (hero == null)
                    return true;

                var data = battle.addPlayerData(hero);
                battle.addVisiblePlayer(data);
                battle.stockPlayerData.Remove(data);
                battle.ReserveHeros.Remove(hero);

				if (entry.idx < 0)
                {
                    battle.playerData.Add(data);
                    battle.targetPlayerData.Add(data);
                }
                else
                {
                    battle.PlayerViewDataList.Remove(data);

                    var index = Math.Max(0, Math.Min(entry.idx, battle.playerData.Count));
                    battle.PlayerViewDataList.Insert(index, data);
                    battle.playerData.Insert(index, data);
                    battle.targetPlayerData.Insert(index, data);
                }

                data.imageAlpha = 0;
                if (viewer != null)
                {
                    BattleActor.party = owner.data.party;
                    var actor = viewer.AddPartyMember(data, playerLayouts, entry.idx);
                    actor.queueActorState(BattleActor.ActorStateType.APPEAR, "walk", (int)BattleActor.ESCAPE_MAX_COUNT);
                    actor.queueActorState(BattleActor.ActorStateType.APPEAR_END);
                    //actor.setOpacityMultiplier(0);
                    actor.mapChr.pos.Z += 1;  // 一歩下げる / take a step back
                }
                else
                {
                    battle.battleViewer.AddFadeInCharacter(data);
                }

                doRefreshLayout = true;
            }
            else if (entry.cmd == MemberChangeData.Command.SET_WAIT)
            {
                if (tgt == null)   // いないメンバーは外せない / You can't remove members who aren't there
                    return true;

                var data = tgt as BattlePlayerData;

                if (viewer != null)
                {
                    var actor = viewer.searchFromActors(tgt);
                    actor?.queueActorState(BattleActor.ActorStateType.WAIT);
                }
            }
            else if (entry.cmd == MemberChangeData.Command.FADE_OUT)
            {
                // 0人になるような場合は外せない
                // If you have 0 people, you can't remove it
                if (battle.playerData.Count < 2)
                    return true;

                // 外す
                // remove
                if (tgt == null)   // いないメンバーは外せない / You can't remove members who aren't there
                    return true;

                var data = tgt as BattlePlayerData;

                if (viewer != null)
                {
                    var actor = viewer.searchFromActors(tgt);
                    actor?.queueActorState(BattleActor.ActorStateType.DESTROY);
                    actor?.walk(actor.X, actor.Z + 1);
                }
                else
                {
                    battle.battleViewer.AddFadeOutCharacter(data);
                }
            }
            else if (entry.cmd == MemberChangeData.Command.REMOVE ||
                entry.cmd == MemberChangeData.Command.REMOVE_NOPACK)
            {
                // 0人になるような場合は外せない
                // If you have 0 people, you can't remove it
                if (battle.playerData.Count <= 1 && !entry.force)
                    return true;

                // 外す
                // remove
                if (tgt == null)   // いないメンバーは外せない / You can't remove members who aren't there
                    return true;

                // 詰める アンド アクター解放
                // stuffing and releasing actors
                if (viewer != null)
                {
                    var actor = viewer.searchFromActors(tgt);
                    int removeIndex = 0;
                    foreach (var fr in viewer.friends)
                    {
                        if (actor == fr)
                            break;
                        removeIndex++;
                    }
                    for (int i = removeIndex; i < viewer.friends.Count - 1; i++)
                    {
                        viewer.friends[i] = viewer.friends[i + 1];
                        viewer.friends[i + 1] = null;
                    }
                    viewer.friends[viewer.friends.Count - 1] = null;
                    actor?.Release();
                }

                var data = tgt as BattlePlayerData;

                data.selectedBattleCommandType = BattleCommandType.Skip;

                battle.removeVisiblePlayer(data);
                //battle.stockPlayerData.Add(data);
                battle.playerData.Remove(data);
                battle.targetPlayerData.Remove(data);

				// GameDataに状態を反映する
				// Reflect state in GameData
				battle.ApplyPlayerDataToGameData(data);

                if (entry.cmd == MemberChangeData.Command.REMOVE)
                    doRefreshLayout = true;
            }
            else if (entry.cmd == MemberChangeData.Command.SET_IDX)
            {
                // インデックス指定の入れ替え
                // Replacing index specifications
                if (entry.id == null)
                {
                    // idx か level が範囲外なら何もしない
                    // Do nothing if idx or level is out of range
                    if (entry.idx < 0 || entry.idx >= battle.playerData.Count ||
                        entry.level < 0 || entry.level >= battle.playerData.Count)
                        return true;

                    // idx と level の順番を入れ替える
                    // Swap the order of idx and level
                    var a = battle.playerData[entry.idx];
                    var b = battle.playerData[entry.level];
                    battle.playerData[entry.idx] = b;
                    battle.playerData[entry.level] = a;
                    battle.targetPlayerData[entry.idx] = b;
                    battle.targetPlayerData[entry.level] = a;
                    battle.PlayerViewDataList[entry.idx] = b;
                    battle.PlayerViewDataList[entry.level] = a;
                }
                // 挿入型
                // Insertion type
                else
                {
                    if (tgt == null)   // いないメンバーは外せない / You can't remove members who aren't there
                        return true;
                    var data = tgt as BattlePlayerData;
                    var oldIndex = battle.playerData.IndexOf(data);
                    battle.PlayerViewDataList.Remove(data);
                    battle.playerData.Remove(data);
                    battle.targetPlayerData.Remove(data);
                    int tgtIndex = entry.idx > oldIndex ? entry.idx - 1 : entry.idx;
                    int index = Math.Max(0, Math.Min(tgtIndex, battle.playerData.Count));
                    battle.PlayerViewDataList.Insert(index, data);
                    battle.playerData.Insert(index, data);
                    battle.targetPlayerData.Insert(index, data);
                }
                doRefreshLayout = true;
            }
            else if (entry.cmd == MemberChangeData.Command.ADD_CHECK)
            {
                // キューに積まれているADD コマンドをチェックし、既に加入していたらキューをクリアする
                // Check the ADD commands in the queue and clear the queue if they have already been added.
                foreach (var cmd in memberChangeQueue.ToArray())
                {
                    if (cmd.cmd == MemberChangeData.Command.ADD)
                    {
                        // 既に加入していたら何もしない
                        // If already subscribed, do nothing
                        if (searchPartyFromId(cmd.id) != null)
                        {
                            memberChangeQueue.Clear();
                            break;
                        }
                    }
                }

                return true;
            }

            // 追加・削除結果に従って整列させる
            // Arrange according to addition/deletion results
            if (doRefreshLayout)
            {
                battle.battlePlayerMax = Math.Max(battle.battlePlayerMax, battle.playerData.Count);
                battle.MovePlayerPosition();
                battle.battleViewer.refreshLayout(battle.playerData, null);
            }

            return true;
        }

        internal void checkAllSheet(MapCharacter mapChr, bool inInitialize, bool applyScript, bool applyGraphic, RomItem parentRom = null)
        {
            var rom = mapChr.rom;

            int nowPage = mapChr.currentPage;
            int destPage = -1;
            for (int i = rom.sheetList.Count - 1; i >= 0; i--)
            {
                if (CheckAllCondition(mapChr, rom.sheetList[i].condList))
                {
                    destPage = i;
                    break;
                }
            }

            if (nowPage != destPage)
            {
                // 遷移先のページが有る場合
                // If there is a transition destination page
                if (destPage >= 0)
                {
                    var sheet = rom.sheetList[destPage];

                    if (applyGraphic)
                    {
                        changeCharacterGraphic(mapChr, sheet.graphic);
                        mapChr.playMotion(sheet.graphicMotion, inInitialize ? 0 : 0.2f);
                        mapChr.setDirection(sheet.direction, nowPage < 0);
                    }

                    if (applyScript)
                    {
                        // 前回登録していた Script を RunnerDic から外す
                        // Remove the previously registered Script from RunnerDic
                        if (nowPage >= 0)
                        {
                            var scriptId = mapChr.GetScriptId(catalog, nowPage);
                            if (runnerDic.ContainsKey(scriptId))
                            {
                                if (runnerDic[scriptId].state != ScriptRunner.ScriptState.Running)
                                {
                                    runnerDic[scriptId].finalize();
                                    runnerDic.Remove(scriptId);
                                }
                                else
                                {
                                    if (runnerDic[scriptId].Trigger == Common.Rom.Script.Trigger.PARALLEL ||
                                        runnerDic[scriptId].Trigger == Common.Rom.Script.Trigger.AUTO_PARALLEL)
                                        runnerDic[scriptId].removeTrigger = ScriptRunner.RemoveTrigger.ON_COMPLETE_CURRENT_LINE;
                                    else
                                        runnerDic[scriptId].removeTrigger = ScriptRunner.RemoveTrigger.ON_EXIT;
                                }
                            }
                        }

                        var script = mapChr.GetScript(catalog, destPage);

                        if (script != null)
                        {
                            // 付随するスクリプトを RunnerDic に登録する
                            // Register accompanying scripts with RunnerDic
                            var scriptId = mapChr.GetScriptId(catalog, destPage);

                            mapChr.expand = script.expandArea;
                            if (script.commands.Count > 0)
                            {
                                var runner = new ScriptRunner(this, mapChr, script, scriptId, parentRom);

                                // 自動的に開始(並列)が removeOnExit状態で残っている可能性があるので、関係ないGUIDに差し替える
                                // Automatically start (parallel) may remain in the removeOnExit state, so replace it with an unrelated GUID
                                if (runnerDic.ContainsKey(scriptId))
                                {
                                    var tmp = runnerDic[scriptId];
                                    tmp.key = Guid.NewGuid();
                                    runnerDic.Remove(scriptId);
                                    runnerDic.Add(tmp.key, tmp);
                                }

                                // 辞書に登録
                                // Add to dictionary
                                runnerDic.Add(scriptId, runner);

                                // 自動的に開始の場合はそのまま開始する
                                // If it starts automatically, just start
                                if (script.trigger == Common.Rom.Script.Trigger.AUTO ||
                                    script.trigger == Common.Rom.Script.Trigger.AUTO_REPEAT ||
                                    script.trigger == Common.Rom.Script.Trigger.PARALLEL ||
                                    script.trigger == Common.Rom.Script.Trigger.PARALLEL_MV ||
                                    script.trigger == Common.Rom.Script.Trigger.AUTO_PARALLEL ||
                                    script.trigger == Common.Rom.Script.Trigger.BATTLE_PARALLEL ||
                                    script.trigger == Common.Rom.Script.Trigger.GETITEM)
                                    runner.Run();
                            }
                        }
                    }
                }
                // 遷移先のページがない場合
                // When there is no transition destination page
                else
                {
                    // 前回登録していた Script を RunnerDic から外す
                    // Remove the previously registered Script from RunnerDic
                    if (nowPage >= 0)
                    {
                        if (applyGraphic)
                        {
                            changeCharacterGraphic(mapChr, Guid.Empty);
                        }

                        if (applyScript)
                        {
                            var scriptId = mapChr.GetScriptId(catalog, nowPage);

                            if (runnerDic.ContainsKey(scriptId))
                            {
                                if (runnerDic[scriptId].state != ScriptRunner.ScriptState.Running)
                                {
                                    runnerDic[scriptId].finalize();
                                    runnerDic.Remove(scriptId);
                                }
                                else
                                {
                                    if (runnerDic[scriptId].Trigger == Common.Rom.Script.Trigger.PARALLEL ||
                                        runnerDic[scriptId].Trigger == Common.Rom.Script.Trigger.AUTO_PARALLEL ||
                                        runnerDic[scriptId].Trigger == Common.Rom.Script.Trigger.PARALLEL_MV)
                                        runnerDic[scriptId].removeTrigger = ScriptRunner.RemoveTrigger.ON_COMPLETE_CURRENT_LINE;
                                    else
                                        runnerDic[scriptId].removeTrigger = ScriptRunner.RemoveTrigger.ON_EXIT;
                                }
                            }
                        }
                    }
                }

                mapChr.currentPage = destPage;
            }
        }

        /// <summary>
        /// イベントの全シートから条件パネルを満たしているかどうか調べる
        /// Check whether the condition panel is satisfied from all sheets of the event
        /// </summary>
        /// <param name="mapChr"></param>
        /// <param name="condList"></param>
        /// <returns></returns>
        public bool CheckAllCondition(MapCharacter mapChr, IEnumerable<Common.Rom.Event.Condition> condList)
        {
            var data = owner.data;
            bool ok = true;
            foreach (var cond in condList)
            {
                switch (cond.type)
                {
                    case Common.Rom.Event.Condition.Type.COND_TYPE_SWITCH:
                        if (string.IsNullOrEmpty(cond.name))
                        {
                            var entry = catalog.getGameSettings().varDefs.getVariableEntry(mapChr.guId, VariableDefs.VarType.FLAG, cond.index);
                            if (entry != null)
                            {
                                cond.name = entry.name;
                                cond.local = entry.isLocal();
                            }
                        }
                        ok = data.system.GetSwitch(cond.name, cond.local ? mapChr.guId : Guid.Empty,
                            mapChr.IsDynamicGenerated) == (cond.option == 0 ? true : false);
                        break;
                    case Common.Rom.Event.Condition.Type.COND_TYPE_VARIABLE:
                        if (string.IsNullOrEmpty(cond.name))
                        {
                            var entry = catalog.getGameSettings().varDefs.getVariableEntry(mapChr.guId, VariableDefs.VarType.DOUBLE, cond.index);
                            if (entry != null)
                            {
                                cond.name = entry.name;
                                cond.local = entry.isLocal();
                            }
                        }
                        ok = EventEngine.checkCondition(data.system.GetVariable(cond.name, cond.local ? mapChr.guId : Guid.Empty,
                            mapChr.IsDynamicGenerated), cond.option, cond.cond);
                        break;
                    case Common.Rom.Event.Condition.Type.COND_TYPE_STRVARIABLE:
                        if (string.IsNullOrEmpty(cond.name))
                        {
                            var entry = catalog.getGameSettings().varDefs.getVariableEntry(mapChr.guId, VariableDefs.VarType.DOUBLE, cond.index);
                            if (entry != null)
                            {
                                cond.name = entry.name;
                                cond.local = entry.isLocal();
                            }
                        }
                        ok = EventEngine.checkCondition(data.system.GetStrVariable(cond.name, cond.local ? mapChr.guId : Guid.Empty, mapChr.IsDynamicGenerated), cond.GetAttr(0)?.GetString(), (int)cond.cond);
                        break;
                    case Common.Rom.Event.Condition.Type.COND_TYPE_ARRAY_SW:
                        ok = (data.system.GetFromArray(cond.name,
                            cond.pointer >= 0 ? cond.pointer : (int)data.system.GetVariable(cond.pointerName, cond.local ? mapChr.guId : Guid.Empty, mapChr.IsDynamicGenerated)) > 0)
                            == (cond.option == 0 ? true : false);
                        break;
                    case Common.Rom.Event.Condition.Type.COND_TYPE_ARRAY_VAR:
                        ok = EventEngine.checkCondition(data.system.GetFromArray(cond.name,
                            cond.pointer >= 0 ? cond.pointer : (int)data.system.GetVariable(cond.pointerName, cond.local ? mapChr.guId : Guid.Empty, mapChr.IsDynamicGenerated)), cond.option, cond.cond);
                        break;
                    case Common.Rom.Event.Condition.Type.COND_TYPE_ARRAY_STRVAR:
                        ok = EventEngine.checkCondition(data.system.GetStrFromArray(cond.name,
                            cond.pointer >= 0 ? cond.pointer : (int)data.system.GetVariable(cond.pointerName, cond.local ? mapChr.guId : Guid.Empty, mapChr.IsDynamicGenerated)), cond.GetAttr(0)?.GetString(), (int)cond.cond);
                        break;
                    case Common.Rom.Event.Condition.Type.COND_TYPE_MONEY:
                        ok = EventEngine.checkCondition(data.party.GetMoney(), cond.option, cond.cond);
                        break;
                    case Common.Rom.Event.Condition.Type.COND_TYPE_ITEM:
                        ok = EventEngine.checkCondition(data.party.GetItemNum(cond.refGuid, false, true), cond.option, cond.cond);
                        break;
                    case Common.Rom.Event.Condition.Type.COND_TYPE_ITEM_WITH_EQUIPMENT:
                        ok = EventEngine.checkCondition(data.party.GetItemNum(cond.refGuid, true, true), cond.option, cond.cond);
                        break;
                    case Common.Rom.Event.Condition.Type.COND_TYPE_BATTLE:
                        ok = isMatchBattlePhase(cond.option);

                        // 誰がを指定してある場合に対応
                        // Corresponds to cases where you specify who
                        if (ok && cond.option > 2 && cond.attrList.Count > 0)
                        {
                            var active = battle.commandSelectPlayer as BattleCharacterBase;
                            if (active == null)
                                active = battle.activeCharacter;

                            switch (cond.attrList[0].GetInt())
                            {
                                case -1:   // 指定なし / unspecified
                                    break;
                                case 0:    // GUID指定 / GUID specification
                                    ok = active?.GetSource()?.guId == cond.attrList[1].GetGuid();
                                    break;
                                case 1:    // パーティ番号 / party number
                                    ok = battle.playerData.IndexOf(active as BattlePlayerData)
                                        == ScriptRunner.GetNumOrVariable(GameMain.instance, mapChr.guId, cond.attrList[1], false) - 1;
                                    break;
                                case 2:    // 敵番号 / enemy number
                                    ok = (battle.activeCharacter is BattleEnemyData enm) && enm.UniqueID
                                        == ScriptRunner.GetNumOrVariable(GameMain.instance, mapChr.guId, cond.attrList[1], false);
                                    break;
                            }
                        }
                        break;
                    case Common.Rom.Event.Condition.Type.COND_TYPE_HERO:
                        ok = data.party.ExistMember(cond.refGuid);
                        if (!ok && (cond.option >> 8) > 0)
                            ok = data.party.ExistInReserve(cond.refGuid);
                        if ((cond.option & 0xFF) != 0)
                            ok = !ok;
                        break;
                    case Common.Rom.Event.Condition.Type.COND_TYPE_HITPOINT:
                        //ok = MapEngine.checkCondition(mapChr.battleStatus.HitPoint, mapChr.battleStatus.MaxHitPoint * cond.option / 100, cond.cond);
                        break;

                    case Event.Condition.Type.COND_TYPE_OR:
                        ok = cond.attrList.Any(x => x is Script.ConditionAttr condAttr && CheckAllCondition(mapChr, condAttr.condList));
                        break;
                }
                if (!ok)
                    break;
            }
            return ok;
        }

        internal void changeCharacterGraphic(MapCharacter mapChr, Guid guid)
        {
            var v3d = battle.battleViewer as BattleViewer3D;

            if (v3d == null)
                return;

            ChangeGraphicImpl(owner.catalog, mapChr, guid, v3d.mapDrawer);
        }

        private bool isMatchBattlePhase(int phaseCode)
        {
            switch (phaseCode)
            {
                // バトル開始？
                // Battle starting?
                case 0:
                    return battle.battleState <= BattleState.BattleStart;

                // バトル中？
                // In battle?
                case 1:
                    return !(battle.battleState <= BattleState.BattleStart || battle.battleState >= BattleState.StartBattleFinishEvent);

                // バトル終了？
                // Is the battle over?
                case 2:
                    return battle.battleState >= BattleState.StartBattleFinishEvent;

                // 新バトルトリガー
                // New battle trigger
                default:
                    return currentProcessingTrigger == phaseCode;
            }
        }

        public override void showBattleUi(bool v)
        {
            battleUiVisibility = v;
        }

        public override void healBattleCharacter(Script.Command curCommand, Guid evGuid)
        {
            int cur = 0;
            var targetAttr = curCommand.attrList[cur++];
            var guid = targetAttr.GetGuid();
            // 対象がGuid（キャスト）指定の場合、GetNumOrVariable に渡すと GuidAttr が変数扱いされ、
            // If the target is Guid (cast) specified, GuidAttr is treated as a variable when passed to GetNumOrVariable,
            // 名前が空の変数を読み出して生成してしまう（HP操作と並行した「空の変数への代入」の原因）。
            // A variable with an empty name is read and generated (causing \
            // Guid指定時は idx を使用しないため、変数参照を行わない。
            // When specifying Guid, idx is not used, so variables are not referenced.
            var idx = (targetAttr is Script.GuidAttr) ? 0 : (int)ScriptRunner.GetNumOrVariable(owner, evGuid, targetAttr, false);
            Guid statusId;
            var gs = catalog.getGameSettings();

            if (curCommand.attrList[cur] is Script.GuidAttr idAttr)
            {
                var info = gs.GetCastStatusParamInfo(idAttr.GetGuid());

                if (info == null)
                {
                    return;
                }

                statusId = info.guId;
            }
            else if (curCommand.attrList[cur].GetBool())
            {
                var info = gs.GetCastStatusParamInfo(gs.maxMPStatusID, true);

				if (info == null)
				{
                    return;
				}

                statusId = info.guId;
            }
            else
            {
                statusId = gs.maxHPStatusID;
            }

            cur++;

            var valueAttr = curCommand.attrList[cur++];
            var param = 0;
            var invert = curCommand.attrList[cur++].GetInt();
            var targetId = guid;
			var targetList = new List<BattleCharacterBase>();
            var showDamage = false;
            var useFormula = false;
            BattleCharacterBase source = null;
            var attr = Guid.Empty;
            var add = true;
            var percent = false;
            var searchBattleCastOrReserveHero = false;

			if (curCommand.attrList.Count > cur)
            {
                // タイプ？
                // type?
                switch (curCommand.attrList[cur++].GetInt())
                {
                    case 0:
						searchBattleCastOrReserveHero = true;
						break;
                    case 1:
                        var ptIdx = idx - 1;
                        if (ptIdx >= 0 && battle.playerData.Count > ptIdx && battle.playerData[ptIdx] != null)
                            targetList.Add(battle.playerData[ptIdx]);
                        break;
                    case 2:
                        var mobIdx = idx;
						BattleCharacterBase target = battle.playerData.FirstOrDefault(x => x.UniqueID == mobIdx);

                        if (target == null)
                        {
							target = battle.enemyData.FirstOrDefault(x => x.UniqueID == mobIdx);
						}

						targetList.Add(target);
                        break;
                    default:
						break;
				}

				// 変数？
				// variable?
				var valueType = curCommand.attrList[cur++].GetInt();

                switch (valueType)
                {
                    case 1:// 旧変数指定 / Old variable specification
                        param = (int)ScriptRunner.GetVariable(owner, evGuid, valueAttr, false);
                        if (invert >= 2)
                            param *= -1;
                        break;
                    case 2:// 計算式 / a formula
                        useFormula = true;
                        break;
                    default:
                        param = (int)ScriptRunner.GetNumOrVariable(owner, evGuid, valueAttr, false);

                        if (invert != 0)
                        {
                            param *= -1;
                        }

                        switch (valueType)
                        {
                            case 0:// 直値・変数指定 / Direct value/variable specification
                                break;
                            case 3:// 直値・加算・パーセント / Direct value/addition/percentage
                                percent = true;
                                break;
                            case 4:// 直値・上書き / Direct value/overwrite
                                add = false;
                                break;
                            case 5:// 直値・上書き パーセント / Direct value/overwrite percentage
                                add = false;
                                percent = true;
                                break;
                        }
                        break;
                }

                showDamage = curCommand.attrList[cur++].GetBool();

                // 計算式ダメージ
                // Calculated damage
                if (useFormula)
                {
                    source = battle.activeCharacter;
                    switch (curCommand.attrList[cur++].GetInt())
                    {
                        case -1:
                            break;
                        case 0:
                            guid = curCommand.attrList[cur++].GetGuid();
                            source = searchPartyFromId(guid);
                            break;
                        case 1:
                            var ptIdx = (int)ScriptRunner.GetNumOrVariable(owner, evGuid, curCommand.attrList[cur++], false) - 1;
                            if (battle.playerData.Count > ptIdx && battle.playerData[ptIdx] != null)
                                source = battle.playerData[ptIdx];
                            break;
                        case 2:
                            var mobIdx = (int)ScriptRunner.GetNumOrVariable(owner, evGuid, curCommand.attrList[cur++], false);
                            source = battle.enemyData.FirstOrDefault(x => x.UniqueID == mobIdx);
                            break;
                    }

                    attr = curCommand.attrList.Count > cur ? curCommand.attrList[cur++].GetGuid() : (source?.AttackAttribute ?? Guid.Empty);
                }
                else
                {
                    // 属性を読み飛ばす
                    // Skip attributes
                    cur++;
                }
            }
            else
            {
                targetList.Add(battle.enemyData.FirstOrDefault(x => x.UniqueID == idx));
            }

            // 消滅保留フラグ
            // Extinction pending flag
            var setDown = true;
            if (curCommand.attrList.Count > cur)
                setDown = !curCommand.attrList[cur++].GetBool();

            // 計算式引数が最後に追加された
            // calculation argument added last
            if (useFormula && (cur < curCommand.attrList.Count))
            {
                valueAttr = curCommand.attrList[cur++];
			}

			var findReserves = false;

			if ((cur < curCommand.attrList.Count))
            {
				findReserves = curCommand.attrList[cur++].GetBool();
            }

			if (searchBattleCastOrReserveHero)
			{
				targetList.AddRange(searchBattleCastOrReserveHeroFromCastId(targetId, findReserves));

				if (findReserves)
				{
					targetList.AddRange(searchBattleCastOrReserveHeroFromCastId(Common.GameData.Party.ReservesAllId, findReserves));
				}
			}

			foreach (var target in targetList)
            {
                BattleDamageTextInfo.TextType textType = BattleDamageTextInfo.TextType.Miss;
                var value = param;

				if (useFormula)
                {
                    value = (int)battle.EvalFormula(gs.GetFormula(valueAttr.GetString(), true), source, target, attr);

                    if (invert >= 2)
                    {
                        value *= -1;
                    }
                }

                if (target != null)
                {
                    if (percent)
                    {
                        value = value * target.GetStatus(gs, statusId) / 100;
                    }

                    if (!add)
                    {
                        value -= target.consumptionStatusValue.GetStatus(statusId);
                    }

                    target.consumptionStatusValue.AddStatus(statusId, value);
                    target.consumptionStatusValue.Min(statusId, target.GetSystemStatus(gs, statusId));
                    target.consumptionStatusValue.Max(statusId, 0);

                    if (value == 0)
                    {
                        textType = (invert == 0) ? BattleDamageTextInfo.TextType.Heal : BattleDamageTextInfo.TextType.Damage;
                    }
                    else
                    {
                        textType = (value >= 0) ? BattleDamageTextInfo.TextType.Heal : BattleDamageTextInfo.TextType.Damage;
                    }

                    if (statusId == gs.maxHPStatusID)
                    {
                        target.HitPoint = target.consumptionStatusValue.GetStatus(statusId);

                        if (target.HitPoint > 0)
                        {
                            if (target.IsDeadCondition())
                            {
                                if (target is BattleEnemyData)
                                {
                                    battle.battleViewer.AddFadeInCharacter(target);
                                }
                                target.Resurrection(this);
                                target.ConsistancyHPPercentConditions(catalog, this);
							}
                        }
                        else if (target.HitPoint == 0)
                        {
                            if (setDown && !target.IsDeadCondition(true))
                            {
                                if (target is BattleEnemyData)
                                {
                                    battle.battleViewer.AddFadeOutCharacter(target);
                                    Audio.PlaySound(owner.se.defeat);
                                }
                                target.Down(catalog, this);
                            }
                        }
                    }
                    else if (statusId == gs.GetNewSystemStatusId(gs.maxMPStatusID))
                    {
                        target.MagicPoint = target.consumptionStatusValue.GetStatus(statusId);
                    }

                    if (target is BattlePlayerData)
                    {
                        ((BattlePlayerData)target).battleStatusData.MagicPoint = target.MagicPoint;
                    }

                    target.ConsistancyHPPercentConditions(catalog, this);
					target.InitializeEquipmentReAttachCondition(this);

                    if (battle.ReserveHeros.Contains(target.Hero))
                    {
						battle.ApplyPlayerDataToGameData(target as BattlePlayerData);

						continue;
                    }

					battle.statusUpdateTweener.Begin(0, 1.0f, 30);
                    battle.SetNextBattleStatus(target);
                }

                if (target != null && showDamage)
                {
                    string text = value.ToString();
                    if (value < 0)
                    {
                        text = (-value).ToString();
                    }

                    battle.battleViewer.AddDamageTextInfo(new BattleDamageTextInfo(textType, target, text, statusId, gs.GetStatusColor(statusId, (textType == BattleDamageTextInfo.TextType.Damage))));
                }
            }
        }

        public override void healParty(Script.Command curCommand, Guid evGuid)
        {
            int cur = 0;
            var tgts = new List<BattleCharacterBase>();
            getTargetDataForMapArgs(tgts, curCommand, ref cur, evGuid);

            var gs = catalog.getGameSettings();
            var tgtIsMp = curCommand.attrList[cur++].GetBool();
            var value = (int)ScriptRunner.GetNumOrVariable(owner, evGuid, curCommand.attrList[cur++], false);
            var invert = curCommand.attrList[cur++].GetBool();
            if (invert)
                value *= -1;

            foreach (var chr in tgts)
            {
                if (tgtIsMp)
                {
                    chr.MagicPoint += value;
                    if (chr.MagicPoint > chr.MaxMagicPoint)
                        chr.MagicPoint = chr.MaxMagicPoint;
                    else if (chr.MagicPoint < 0)
                        chr.MagicPoint = 0;
                    chr.consumptionStatusValue.SetStatus(gs.maxMPStatusID, chr.MagicPoint);
                }
                else
                {
                    chr.HitPoint += value;
                    if (chr.HitPoint > chr.MaxHitPoint)
                        chr.HitPoint = chr.MaxHitPoint;
                    else if (chr.HitPoint < 0)
                        chr.HitPoint = 0;
                    chr.consumptionStatusValue.SetStatus(gs.maxHPStatusID, chr.HitPoint);

                    if ((chr.HitPoint > 0) && chr.IsDeadCondition())
                        chr.Resurrection(this);
                    else if (chr.HitPoint == 0)
                        chr.Down(catalog, this);

                    chr.ConsistancyHPPercentConditions(catalog, this);
                }

                battle.SetNextBattleStatus(chr);
                battle.statusUpdateTweener.Begin(0, 1.0f, 30);
            }
        }

        public override MapCharacter getBattleActorMapChr(Script.Command curCommand, Guid evGuid)
        {
            int cur = 0;
            var tgt = getTargetData(curCommand, ref cur, evGuid);

            var viewer = battle.battleViewer as BattleViewer3D;
            if (viewer == null)
                return null;

            var actor = viewer.searchFromActors(tgt);
            if (actor == null)
                return null;

            return actor.mapChr;
        }

        private BattleCharacterBase getTargetData(Script.Command rom, ref int cur, Guid evGuid)
        {
			// ターゲット指定がない場合がある
			// Sometimes there is no target designation
			if (rom.attrList.Count <= cur)
				return null;

			BattleCharacterBase result = null;
			switch (rom.attrList[cur++].GetInt())
			{
				case 0:
					var heroGuid = rom.attrList[cur++].GetGuid();
					result = searchPartyFromId(heroGuid);
					break;
				case 1:
					var ptIdx = (int)ScriptRunner.GetNumOrVariable(owner, evGuid, rom.attrList[cur++], false) - 1;
					if (ptIdx >= 0 && battle.playerData.Count > ptIdx && battle.playerData[ptIdx] != null)
						result = battle.playerData[ptIdx];
					break;
				case 2:
					var mobIdx = (int)ScriptRunner.GetNumOrVariable(owner, evGuid, rom.attrList[cur++], false);
					result = battle.enemyData.FirstOrDefault(x => x.UniqueID == mobIdx);
					break;
			}
			return result;
		}

		private List<BattleCharacterBase> getTargetDatas(Script.Command rom, ref int cur, out bool partyAll, Guid evGuid, bool findReserves)
		{
			partyAll = false;

			// ターゲット指定がない場合がある
			// Sometimes there is no target designation
			if (rom.attrList.Count <= cur)
				return null;

			var list = new List<BattleCharacterBase>();
            var idx = cur;
            var target = getTargetData(rom, ref cur, evGuid);

            if (target != null)
            {
                list.Add(target);
            }

			if ((list.Count == 0) && (rom.attrList[idx++].GetInt() == 0))
            {
				var id = rom.attrList[idx++].GetGuid();

				partyAll = id == Guid.Empty;

				var all = partyAll || (id == Common.GameData.Party.ReservesAllId) || (id == Common.GameData.Party.MonstersAllId);

				foreach (var item in searchBattleCastOrReserveHeroFromCastId(id, findReserves))
				{
					if (item is BattleCharacterBase battleCharacter)
					{
						list.Add(battleCharacter);
					}

                    if (!all && (list.Count > 0))
                    {
                        break;
                    }
				}
			}

			return list;
		}

		private List<BattleCharacterBase> getTargetDatas(Script.Command rom, ref int cur, Guid evGuid, bool findReserves)
		{
            bool partyAll;

			return getTargetDatas(rom, ref cur, out partyAll, evGuid, findReserves);
		}

		public override BattleCharacterBase searchPartyFromId(object heroId)
        {
            if (heroId is Guid)
            {
                var id = (Guid)heroId;

                foreach (var chr in battle.playerData)
                {
                    if (chr.player.rom.guId != id)
                        continue;

                    return chr;
                }
            }
            else if (heroId is int)
            {
                var idx = (int)heroId;

                if ((0 <= idx) && (idx < battle.playerData.Count))
                {
                    return battle.playerData[idx];
                }
            }
			else if (heroId is Common.GameData.Hero hero)
			{
                foreach (var chr in battle.playerData)
                {
                    if (chr.Hero == hero)
                    {
                        return chr;
                    }
                }
            }

            return null;
        }

        public List<BattleCharacterBase> searchPartyFromCastId(Guid castId)
        {
            var list = new List<BattleCharacterBase>();

            foreach (var chr in battle.playerData)
            {
                if (chr.player.rom.guId == castId)
                {
                    list.Add(chr);
                }
            }

            return list;
        }

		/// <summary>
		/// キャストIdからバトル内キャラクターか控えのリストを返す
		/// Returns a list of in-battle characters or backups from the cast ID
		/// </summary>
		/// <param name="castId">キャストId</param>
		/// <param name="castId">Cast ID</param>
		/// <param name="findReserves">キャストIdで控え内を探すか？</param>
		/// <param name="findReserves">Do you want to search the waiting room by cast ID?</param>
		/// <returns></returns>
		public List<BattleCharacterBase> searchBattleCastOrReserveHeroFromCastId(Guid castId, bool findReserves)
		{
			var list = new List<BattleCharacterBase>();

			if (castId == Guid.Empty)
			{
				list.AddRange(battle.playerData);
			}
			else if (castId == Common.GameData.Party.ReservesAllId)
			{
                foreach (var hero in battle.ReserveHeros)
                {
					list.Add(battle.createPlayerData(hero, false));
				}
			}
			else if (castId == Common.GameData.Party.MonstersAllId)
			{
				list.AddRange(battle.enemyData);
			}
			else
			{
                list.AddRange(searchPartyFromCastId(castId));

				if (findReserves)
				{
					foreach (var hero in battle.ReserveHeros)
					{
						if (hero.rom.guId == castId)
						{
							list.Add(battle.createPlayerData(hero, false));
						}
					}
				}
			}

			return list;
		}

		public override BattleCharacterBase searchEnemyFromMapCharacter(MapCharacter mapCharacter)
		{
            var viewer = battle.battleViewer as BattleViewer3D;

            if (viewer == null)
            {
                return null;
            }

			foreach (var actor in viewer.friends)
			{
				if (actor?.mapChr == mapCharacter)
				{
					return actor.source;
				}
			}

			foreach (var actor in viewer.enemies)
			{
				if (actor?.mapChr == mapCharacter)
				{
                    return actor.source;
				}
			}

            return null;
        }

        public override void setNextAction(Script.Command curCommand, Guid evGuid)
        {
            int cur = 0;
            var tgt = getTargetData(curCommand, ref cur, evGuid);
            if (tgt == null)
                return;

            var timing = IsImmediateAction(curCommand, evGuid);
            if (timing > 0)
            {
                //if (battle.activeCharacter == tgt && timing == 1)// いま行動しているキャラに即時行動させる場合、ContinuousAction の仕組みを利用する / If you want to make the character who is currently acting take immediate action, use the ContinuousAction mechanism.
                //{
                //    if (tgt.continuousActionType != BattleCharacterBase.ContinuousActionType.BY_EVENT)
                //        timing = 2;
                //    tgt.continuousActionType = BattleCharacterBase.ContinuousActionType.BY_EVENT;
                //}

                if (timing == 1)// 即時行動する場合、キューにセットする / For immediate action, set in queue
                {
                    battle.InsertAction(new BattleSequenceManager.BattleActionEntry(tgt, () => procSetAction(curCommand, evGuid)), true, true);
                }
                else// このターンの行動を上書き / Overwrite this turn's actions
                {
                    procSetAction(curCommand, evGuid);
                }
            }
            else
            {
                // すでに同じ対象のものがキューに入っていたら、まず外す
                // If the same target is already in the queue, remove it first
                actionQueue.RemoveAll(x =>
                {
                    int cur2 = 0;
                    var tgt2 = getTargetData(x.cmd, ref cur2, evGuid);
                    return tgt == tgt2 && x.cmd.type == curCommand.type;
                });

                // 次のターンの行動指定
                // Specify next turn's action
                actionQueue.Add(new ActionData() { cmd = curCommand, evGuid = evGuid });
            }
        }

        private int IsImmediateAction(Script.Command curCommand, Guid evGuid)
        {
            int cur = 0;

            // 誰が
            // who
            var tgt = getTargetData(curCommand, ref cur, evGuid);
            if (tgt == null)
                return 0;

            // 何を
            // what
            cur += 2;// 行動種別、オプション / Action type, options

            // 誰に
            // to whom
            getTargetData(curCommand, ref cur, evGuid);
            
            // 即時行動？
            // Immediate action?
            if (curCommand.attrList.Count > cur)
            {
                return curCommand.attrList[cur++].GetInt();
            }

            return 0;
        }

        public override void setBattleStatus(Script.Command curCommand, Guid evGuid, bool typeReduced = false)
        {
            int cur = 0;
            List<BattleCharacterBase> tgts = new List<BattleCharacterBase>();
            var gs = catalog.getGameSettings();
            var tmpCur = cur;
            var partyAll = false;

			if (typeReduced)
            {
                getTargetDataForMapArgs(tgts, curCommand, ref cur, evGuid);
            }
            else
            {
				tgts.AddRange(getTargetDatas(curCommand, ref cur, out partyAll, evGuid, false));
            }

            var condition = catalog.getItemFromGuid<Common.Rom.Condition>(curCommand.attrList[cur++].GetGuid());

            if (condition == null)
            {
                return;
            }

            bool add = curCommand.attrList[cur++].GetInt() == 0;

			var findReserves = false;

			if (cur < curCommand.attrList.Count)
			{
				findReserves = curCommand.attrList[cur++].GetBool();
			}

            if (!typeReduced && findReserves && ((tgts.Count == 0) || partyAll))
            {
                List<BattleCharacterBase> list;

                if (partyAll)
                {
                    list = searchBattleCastOrReserveHeroFromCastId(Common.GameData.Party.ReservesAllId, findReserves);
				}
                else
                {
					list = getTargetDatas(curCommand, ref tmpCur, evGuid, findReserves);
				}

				if (list != null)
                {
					tgts.AddRange(list);
				}
			}

			var viewer = battle.battleViewer as BattleViewer3D;

			foreach (var tgt in tgts)
            {
                var actor = viewer.searchFromActors(tgt);
				var reserve = battle.ReserveHeros.Contains(tgt.Hero);

				// 変更前の「最も優先すべき状態変化モーション」を控えておく
				// Make a note of the \
				var prevConditionMotion = actor?.getConditionMotion();

				if (add)
                {
                    if (condition.IsDeadCondition)
                    {
                        tgt.HitPoint = 0;
                        tgt.consumptionStatusValue.SetStatus(gs.maxHPStatusID, 0);
                        tgt.SetCondition(catalog, condition.guId, this, false);

                        if (!reserve)
                        {
                            battle.SetNextBattleStatus(tgt);
                        }
                    }
                    else if (!tgt.IsDeadCondition())
                    {
                        tgt.SetCondition(catalog, condition.guId, this, false);
                    }
                }
                else
                {
                    if (condition.IsDeadCondition)
                    {
                        if (tgt.IsDeadCondition())
                        {
                            tgt.HitPoint = 1;
                            tgt.consumptionStatusValue.SetStatus(gs.maxHPStatusID, 1);
                            tgt.Resurrection(this);
                            if (!reserve)
                            {
                                battle.SetNextBattleStatus(tgt);
                            }
                        }
                    }
                    else
                    {
                        tgt.RecoveryCondition(condition.guId, this, Condition.RecoveryType.Event);
                    }
				}

				if (reserve)
				{
					battle.ApplyPlayerDataToGameData(tgt as BattlePlayerData);

                    continue;
				}

                // 最も優先すべき状態変化モーションが変化した時だけ反映する(nullへの変化=全解除を含む)。
                // Reflects only when the most prioritized state change motion changes (including change to null = complete cancellation).
                // 変化のない状態変更で、イベント等で再生中のモーションを上書きしないため。
                // This is because a motion that is being played due to an event, etc. is not overwritten by a state change that does not change.
                if (actor != null && actor.getConditionMotion() != prevConditionMotion)
                    actor.playWaitMotion();
			}

			battle.statusUpdateTweener.Begin(0, 1.0f, 30);
        }

        private void getTargetDataForMapArgs(List<BattleCharacterBase> tgts, Script.Command curCommand, ref int cur, Guid evGuid)
        {
            var type = (Common.Rom.Script.Command.ChangePartyMemberType)curCommand.attrList[cur++].GetInt();
            var id = curCommand.attrList[cur++].GetGuid();
            var attr = curCommand.attrList[cur++];

            switch (type)
            {
                case Script.Command.ChangePartyMemberType.Cast:
				case Script.Command.ChangePartyMemberType.MemberAndReserve:
                    if (id == Common.GameData.Party.ReservesAllId)
                    {
                        break;
                    }

					foreach (var chr in battle.playerData)
                    {
                        if (id == Guid.Empty || chr.player.rom.guId == id)
                        {
                            tgts.Add(chr);
                        }
                    }

                    if (tgts.Count == 0)
                    {
                        return;
                    }
                    break;
                case Script.Command.ChangePartyMemberType.Index:
                    {
                        int idx = attr.GetInt();
                        if(!(attr is Common.Rom.Script.IntAttr))
                            idx = (int)ScriptRunner.GetNumOrVariable(owner, evGuid, attr, false) - 1;
                        var tgt = searchPartyFromId(idx);

                        if (tgt == null)
                        {
                            return;
                        }

                        tgts.Add(tgt);
                    }
                    break;
                default:
                    return;
            }
        }

        public override MemberChangeData addMonster(Script.Command curCommand, Guid evGuid)
        {
            int curAttr = 0;
            var idx = (int)ScriptRunner.GetNumOrVariable(owner, evGuid, curCommand.attrList[curAttr++], false);
            var guid = curCommand.attrList[curAttr++].GetGuid();

            // #5953 「なし」は消去する効果とする
            // #5953 “None” is the effect of erasing
            //if (catalog.getItemFromGuid(guid) == null)
            //    return new MemberChangeData() { finished = true };

            var useLayout = curCommand.attrList[curAttr++].GetBool();
            Vector3? layout = null;
            if (useLayout)
            {
                layout = new Vector3((int)curCommand.attrList[curAttr++].GetFloat(), 0, (int)curCommand.attrList[curAttr++].GetFloat());
            }

            Console.WriteLine("appear : " + idx);
            MemberChangeData result;

            if (curCommand.attrList.Count > curAttr && curCommand.attrList[curAttr++].GetBool())
            {
                var level = ScriptRunner.GetNumOrVariable(owner, evGuid, curCommand.attrList[curAttr++], false);
                result = new MemberChangeData() { mob = true, cmd = MemberChangeData.Command.ADD, idx = idx, id = guid, layout = layout, level = (int)level };
            }
            else
            {
                result = new MemberChangeData() { mob = true, cmd = MemberChangeData.Command.ADD, idx = idx, id = guid, layout = layout };
            }

            memberChangeQueue.Enqueue(result);
            return result;
        }

        public override void battleStop()
        {
            if (isBattleForciblyTerminate)
                return;

            isBattleForciblyTerminate = true;
            battle.battleState = BattleState.StopByEvent;
            ((BattleViewer3D)battle.battleViewer).camManager.setWaitFunc(null);
        }

        public override void fullRecovery(bool poison = true, bool revive = true)
        {
            foreach (var chr in battle.playerData)
            {
                if (revive || chr.HitPoint >= 1)
                {
                    chr.HitPoint = chr.MaxHitPoint;
                    chr.MagicPoint = chr.MaxMagicPoint;
                    if (poison)
                        chr.conditionInfoDic.Clear();

                    battle.SetNextBattleStatus(chr);
                }
            }

            battle.statusUpdateTweener.Begin(0, 1.0f, 30);
        }

        public override int getStatus(Script.Command.IfHeroSourceType srcTypePlus, Guid option, Common.GameData.Hero hero)
        {
            var tgt = searchPartyFromId(hero.rom.guId) as BattlePlayerData;
            if (tgt == null)
                return 0;

            switch (srcTypePlus)
            {
                case Script.Command.IfHeroSourceType.STATUS_AILMENTS:
                    return hero.conditionInfoDic.ContainsKey(option) ? 1 : 0;
                case Script.Command.IfHeroSourceType.LEVEL:
                    return hero.level;
                case Script.Command.IfHeroSourceType.HITPOINT:
                    return tgt.HitPoint;
                case Script.Command.IfHeroSourceType.MAGICPOINT:
                    return tgt.MagicPoint;
                case Script.Command.IfHeroSourceType.ATTACKPOWER:
                    return tgt.Attack;
                case Script.Command.IfHeroSourceType.Defense:
                    return tgt.Defense;// TODO 体力を考慮 / TODO Consider physical strength
                case Script.Command.IfHeroSourceType.POWER:
                    return tgt.Power;
                case Script.Command.IfHeroSourceType.VITALITY:
                    return tgt.VitalityBase;
                case Script.Command.IfHeroSourceType.MAGIC:
                    return tgt.Magic;
                case Script.Command.IfHeroSourceType.SPEED:
                    return tgt.Speed;
                case Script.Command.IfHeroSourceType.EQUIPMENT_WEIGHT:
                    return 0;
                case Script.Command.IfHeroSourceType.STATUS_CUSTOM:
                    return tgt.GetStatus(catalog.getGameSettings(), option);
            }

            return 0;
        }

        public override int getStatus(Guid statusId, Common.GameData.Hero hero)
        {
            var tgt = searchPartyFromId(hero.rom.guId) as BattlePlayerData;

            if (tgt == null)
            {
                return 0;
            }

            return ScriptRunner.getBattleStatus(tgt, statusId);
        }

        public override void addStatus(Common.GameData.Hero hero, ScriptRunner.HeroStatusType type, int num)
        {
            var tgt = searchPartyFromId(hero.rom.guId) as BattlePlayerData;
            if (tgt == null)
                return;

            switch (type)
            {
                case ScriptRunner.HeroStatusType.HITPOINT:
                    tgt.MaxHitPointBase += num;
                    break;
                case ScriptRunner.HeroStatusType.MAGICPOINT:
                    tgt.MaxMagicPointBase += num;
                    break;
                case ScriptRunner.HeroStatusType.ATTACKPOWER:
                    tgt.AttackBase += num;
                    break;
                case ScriptRunner.HeroStatusType.MAGIC:
                    tgt.MagicBase += num;
                    break;
                case ScriptRunner.HeroStatusType.DEFENSE:
                    tgt.DefenseBase += num;
                    break;
                case ScriptRunner.HeroStatusType.SPEED:
                    tgt.SpeedBase += num;
                    break;
            }


            battle.SetNextBattleStatus(tgt);
            battle.statusUpdateTweener.Begin(0, 1.0f, 30);
        }

        public override void SetNextBattleStatus(List<BattleCharacterBase> battlePlayerDataList)
        {
			foreach (var tgt in battlePlayerDataList)
			{
                battle.SetNextBattleStatus(tgt);
                battle.statusUpdateTweener.Begin(0, 1.0f, 30);
            }
        }

        // パラメータの整合性を取るメソッド
        // Method for parameter consistency
        public void consistency(BattlePlayerData p)
        {
            var gs = catalog.getGameSettings();

            // ほかのパラメータも0未満にはならない
            // No other parameter can be less than 0
            if (p.MagicPoint < 0)
                p.MagicPoint = 0;
            if (p.MaxHitPointBase < 1)
                p.MaxHitPointBase = 1;
            if (p.MaxMagicPointBase < 0)
                p.MaxMagicPointBase = 0;
            if (p.AttackBase < 0)
                p.AttackBase = 0;
            if (p.MagicBase < 0)
                p.MagicBase = 0;
            if (p.DefenseBase < 0)
                p.DefenseBase = 0;
            if (p.SpeedBase < 0)
                p.SpeedBase = 0;

            p.HitPoint = Math.Min(p.HitPoint, Common.GameData.Hero.MAX_STATUS);
            p.MagicPoint = Math.Min(p.MagicPoint, Common.GameData.Hero.MAX_STATUS);
            p.HitPoint = Math.Min(p.HitPoint, p.MaxHitPointBase);
            p.MagicPoint = Math.Min(p.MagicPoint, p.MaxMagicPointBase);
            p.AttackBase = Math.Min(p.AttackBase, Common.GameData.Hero.MAX_STATUS);
            p.MagicBase = Math.Min(p.MagicBase, Common.GameData.Hero.MAX_STATUS);
            p.DefenseBase = Math.Min(p.DefenseBase, Common.GameData.Hero.MAX_STATUS);
            p.SpeedBase = Math.Min(p.SpeedBase, Common.GameData.Hero.MAX_STATUS);

            // HPが0以下なら死亡にする
            // If your HP is 0 or less, you will die.
            if (p.HitPoint <= 0)
            {
                p.HitPoint = 0;
                p.consumptionStatusValue.SetStatus(gs.maxHPStatusID, 0);
                p.Down(catalog, this);
            }
            else if (p.IsDeadCondition())
            {
                p.Resurrection(this);
            }
        }

        public override MemberChangeData addRemoveMember(Script.Command curCommand, Guid evGuid)
        {
            int curAttr = 0;
            var type = (Common.Rom.Script.Command.ChangePartyMemberType)curCommand.attrList[curAttr++].GetInt();
            var id = curCommand.attrList[curAttr++].GetGuid();
            var idxAttr = curCommand.attrList[curAttr++];
            var idx = (int)ScriptRunner.GetNumOrVariable(owner, evGuid, idxAttr, false);
            if (!(idxAttr is Common.Rom.Script.IntAttr))
                idx--;
            MemberChangeData lastItem = new MemberChangeData() { finished = true };

            var add = !curCommand.attrList[curAttr++].GetBool();

            bool searchFromOthers = false;
            bool addToOthers = false;
            bool addNew = false;

            // BAKIN新引数
            // BAKIN new argument
            if (curCommand.attrList.Count > 2)
            {
                if (curCommand.attrList.Count > curAttr)
                    searchFromOthers = curCommand.attrList[curAttr++].GetBool();// othersから外すか？ / Should I remove it from others?
                if (curCommand.attrList.Count > curAttr)
                    addToOthers = curCommand.attrList[curAttr++].GetBool();// othersに入れるか？ / Can I put it in others?
                if (curCommand.attrList.Count > curAttr)
                    addNew = curCommand.attrList[curAttr++].GetBool();// othersに入れるか？ / Can I put it in others?
            }

            if (add)
            {
                // 控えに追加するはバトルでは何もしない
                // Adding a copy does not do anything in battle
                if (addToOthers)
                    return lastItem;

                switch (type)
                {
                    case Script.Command.ChangePartyMemberType.Cast:
                        memberChangeQueue.Enqueue(new MemberChangeData() { cmd = addNew ? MemberChangeData.Command.ADD_NEW : MemberChangeData.Command.ADD, id = id });
                        lastItem = new MemberChangeData() { cmd = MemberChangeData.Command.SET_WAIT, id = id };
                        memberChangeQueue.Enqueue(lastItem);
                        break;
                    case Script.Command.ChangePartyMemberType.Index:
                        break;
					case Script.Command.ChangePartyMemberType.ReservesIndex:
                        if ((0 <= idx) && (idx < battle.ReserveHeros.Count))
                        {
							memberChangeQueue.Enqueue(new MemberChangeData() { cmd = MemberChangeData.Command.ADD, id = battle.ReserveHeros[idx] });
							lastItem = new MemberChangeData() { cmd = MemberChangeData.Command.SET_WAIT, id = id };
							memberChangeQueue.Enqueue(lastItem);
						}
						break;
					default:
                        break;
                }
            }
            else
            {
                switch (type)
                {
                    case Script.Command.ChangePartyMemberType.Cast:
                        memberChangeQueue.Enqueue(new MemberChangeData() { cmd = MemberChangeData.Command.FADE_OUT, id = id });
                        lastItem = new MemberChangeData() { cmd = MemberChangeData.Command.REMOVE, id = id };
                        memberChangeQueue.Enqueue(lastItem);
                        break;
                    case Script.Command.ChangePartyMemberType.Index:
                        memberChangeQueue.Enqueue(new MemberChangeData() { cmd = MemberChangeData.Command.FADE_OUT, id = idx });
                        lastItem = new MemberChangeData() { cmd = MemberChangeData.Command.REMOVE, id = idx };
                        memberChangeQueue.Enqueue(lastItem);
                        break;
                    default:
                        break;
                }
            }

            return lastItem;
        }

        public override void start(Guid commonExec)
        {
            var runner = GetScriptRunner(commonExec);
            if (runner != null)
            {
                // MapSceneから借りてきたrunnerは明示的にupdateするリストに入れてやる
                // Runners borrowed from MapScene are explicitly added to the list to be updated
                if (!mapRunnerBorrowed.Contains(runner))
                {
                    mapRunnerBorrowed.Add(runner);
                    runner.owner = this;
                }

                runner.Run();
            }
        }

        public override ScriptRunner GetScriptRunner(Guid guid)
        {
            return owner.mapScene.runnerDic.getList()
                .Where(x => x.mapChr != null && x.mapChr.rom != null && x.mapChr.rom.guId == guid)
                .FirstOrDefault();
        }

        public override void RefreshHeroMapChr(Cast rom)
        {
            var viewer = battle.battleViewer as BattleViewer3D;
            if (viewer == null)
            {
                refreshFace();
                return;
            }

            // グラフィックが変わってたら適用する
            // Apply if graphics change
            foreach (var friend in viewer.friends)
            {
                if (friend == null)
                    break;

                var pl = friend.source as BattlePlayerData;
                if (pl?.player?.rom == null) continue;
                var guid = owner.data.party.getMemberGraphic(pl.player.rom);
                var nowGuid = Guid.Empty;
                if (friend.mapChr.getGraphic() != null)
                    nowGuid = friend.mapChr.getGraphic().guId;
                if (pl != null && pl.player.rom == rom &&
                    guid != nowGuid)
                {
                    var res = catalog.getItemFromGuid<Common.Resource.GfxResourceBase>(guid);
                    friend.mapChr.ChangeGraphic(res, viewer.mapDrawer);
                }
            }
        }

        public override void RefreshHeroJoint(Guid heroGuid)
        {
            var viewer = battle.battleViewer as BattleViewer3D;
            if (viewer == null)
                return;

            var data = searchPartyFromId(heroGuid) as BattlePlayerData;
            if (data == null)
                return;

            var actor = viewer.searchFromActors(data);
            actor?.mapChr.setJointInfoFromGameData(catalog, data.player, owner.data.party);
        }

        public override MapCharacter GetHeroForBattle(Cast rom = null)
        {
            var viewer = battle?.battleViewer as BattleViewer3D;
            if (viewer != null)
            {
                if (rom != null)
                {
                    foreach (var friend in viewer.friends)
                    {
                        if (friend?.mapChr == null)
                            continue;

                        var pl = friend.source as BattlePlayerData;
                        if (pl?.player?.rom == rom)
                            return friend.mapChr;
                    }
                }

                var firstFriend = viewer.friends.FirstOrDefault(x => x?.mapChr != null);
                if (firstFriend != null)
                    return firstFriend.mapChr;
            }

            return base.GetHeroForBattle(rom);
        }

        private void refreshFace()
        {
            // グラフィックが変わってたら適用する
            // Apply if graphics change
            foreach (var friend in battle.playerData)
            {
                if (friend == null)
                    break;

                var pl = friend as BattlePlayerData;
                var guid = owner.data.party.getMemberFace(pl.player.rom);
                if (guid != pl.currentFace)
                {
                    var res = catalog.getItemFromGuid<Common.Resource.SliceAnimationSet>(guid);
                    pl.setFaceImage(res);
                }
            }
        }

        public override void setHeroName(Guid hero, string nextHeroName)
        {
            base.setHeroName(hero, nextHeroName);

            var tgt = searchPartyFromId(hero);
            if (tgt != null)
                tgt.Name = nextHeroName;
        }

        public override int getBattleStatus(Script.Command.VarHeroSourceType srcTypePlus, Guid option, Common.GameData.Hero hero)
        {
            var battleStatus = searchPartyFromId(hero.rom.guId);
            if (battleStatus == null)
                return 0;

            return ScriptRunner.getBattleStatus(battleStatus, srcTypePlus, option, battle.playerData);
        }

        public override int getPartyStatus(Script.Command.VarHeroSourceType srcTypePlus, Guid option, int index)
        {
            if (index < 0 || battle.playerData.Count <= index)
                return 0;

            var battleStatus = battle.playerData[index];
            if (battleStatus == null)
                return 0;

            return ScriptRunner.getBattleStatus(battleStatus, srcTypePlus, option, battle.playerData);
        }

        public override int getPartyStatus(Guid statusId, int index)
        {
            if (index < 0 || battle?.playerData == null || battle.playerData.Count <= index)
                return 0;

            var battleStatus = battle.playerData[index];
            if (battleStatus == null)
                return 0;

            return ScriptRunner.getBattleStatus(battleStatus, statusId);
        }

        public override int getPartyNumber()
        {
            return battle.playerData.Count;
        }

        /// <summary>
        /// モンスターのステータスを取得する 旧仕様 1オリジン
        /// Get monster status Old specification 1 Origin
        /// </summary>
        /// <param name="srcTypePlus"></param>
        /// <param name="option"></param>
        /// <param name="index"></param>
        /// <returns></returns>
        public override int getEnemyStatus(Script.Command.VarHeroSourceType srcTypePlus, Guid option, int index)
        {
            var battleStatus = battle.enemyData.FirstOrDefault(x => x.UniqueID == index);
            if (battleStatus == null)
                return 0;

            return ScriptRunner.getBattleStatus(battleStatus, srcTypePlus, option, battle.playerData);
        }

        /// <summary>
        /// モンスターのステータスを取得する 0オリジン
        /// Get monster status 0 Origin
        /// </summary>
        /// <param name="statusId"></param>
        /// <param name="index"></param>
        /// <returns></returns>
        public override int getEnemyStatus(Guid statusId, int index)
        {
            var battleStatus = battle.enemyData.FirstOrDefault(x => x.UniqueID == index + 1);
            if (battleStatus == null)
                return 0;

            return ScriptRunner.getBattleStatus(battleStatus, statusId);
        }

        public override Guid getEnemyGuid(int index)
        {
            var battleStatus = battle.enemyData.FirstOrDefault(x => x.UniqueID == index);
            if (battleStatus == null)
                return Guid.Empty;

            return battleStatus.monster.guId;
        }

        internal void setLastSkillUserIndex(BattleCharacterBase user)
        {
            var index = 0;

            foreach (var pl in battle.playerData)
            {
                if (pl == user)
                {
                    lastSkillUseCampType = CampType.Party;
                    lastSkillUserIndex = index;

                    return;
                }

                index++;
            }

            foreach (var pl in battle.enemyData)
            {
                if (pl == user)
                {
                    lastSkillUseCampType = CampType.Enemy;
                    lastSkillUserIndex = pl.UniqueID - 1;

                    return;
                }
            }
        }

        internal void setLastSkillTargetIndex(BattleCharacterBase[] targets)
        {
            if (targets.Length > 0)
            {
                int index = 0;
                foreach (var pl in battle.playerData)
                {
                    if (pl == targets[0])
                    {
                        lastSkillTargetIndex = index;
                        return;
                    }
                    index++;
                }

                foreach (var pl in battle.enemyData)
                {
                    if (pl == targets[0])
                    {
                        lastSkillTargetIndex = pl.UniqueID - 1;
                        return;
                    }
                }
            }
        }

        public override bool existMember(Guid heroGuid)
        {
            return searchPartyFromId(heroGuid) != null;
        }

        public override void ReservationChangeScene(GameMain.Scenes scene)
        {
            if (scene == GameMain.Scenes.TITLE)
            {
                battle.battleState = BattleState.BattleFinishCheck1;
                reservedResult = BattleResultState.Escape_ToTitle;
            }
        }

        public override bool DoGameOver()
        {
            battle.battleState = BattleState.BattleFinishCheck1;
            reservedResult = BattleResultState.Lose_GameOver;

            return true;
        }

        internal void checkForceBattleFinish(ref BattleResultState resultState)
        {
            if (reservedResult != BattleResultState.NonFinish)
            {
                resultState = reservedResult;
            }
        }

        public override void refreshEquipmentEffect(Common.GameData.Hero hero)
        {
            var data = searchPartyFromId(hero) as BattlePlayerData;

            if (data == null)
                return;

            battle.ApplyPlayerDataToGameData(data);
            data.SetParameters(hero, owner.debugSettings.battleHpAndMpMax, owner.debugSettings.battleStatusMax, owner.data.party);

            var viewer = battle.battleViewer as BattleViewer3D;
            if (viewer == null)
                return;

            var actor = viewer.searchFromActors(data);
            if (actor == null)
                return;

            BattleActor.createWeaponModel(ref actor, catalog);
        }

        public override void refreshEquipmentAttachCondition(Common.GameData.Hero hero, int setEquipmentIdx, Common.Rom.NItem prevEquipment)
        {
			var setEquipmentId = Catalog.sInstance.getGameSettings().GetEquipParamInfoId(setEquipmentIdx);

			refreshEquipmentAttachCondition(hero, (setEquipmentId), prevEquipment);
		}

        public override void refreshEquipmentAttachCondition(Common.GameData.Hero hero, Guid setEquipmentId, Common.Rom.NItem prevEquipment)
        {
			var data = searchPartyFromId(hero) as BattlePlayerData;

			if (data == null)
			{
				return;
			}

			var equipments = data.Hero.equipments;

			if (equipments[setEquipmentId] == prevEquipment)
			{
				return;
			}

			var catalog = Catalog.sInstance;
			var attachConditionIdList = new List<Guid>();

            // 変化の無い装備での状態付与をリスト化
            // List of status grants with equipment that does not change

            foreach (var info in catalog.getGameSettings().EquipParamInfoList)
            {
				if ((info.guId == setEquipmentId) || (equipments[info.guId] == null))
				{
					continue;
				}

				attachConditionIdList.AddRange(equipments[info.guId].EffectParamSettings.AttachConditionIdList());
			}

			if (prevEquipment != null)
			{
				// 外した装備のみ付与していた状態を解除
				// Removed the condition where only removed equipment was given.
				foreach (var id in prevEquipment.EffectParamSettings.AttachConditionIdList())
				{
					if (!attachConditionIdList.Contains(id))
					{
						data.RecoveryCondition(id, this, Common.Rom.Condition.RecoveryType.Normal);
					}
				}
			}

			if (equipments[setEquipmentId] != null)
			{
				// 新しい装備のみの状態を付与
				// Adds new equipment only status
				foreach (var id in equipments[setEquipmentId].EffectParamSettings.AttachConditionIdList())
				{
					if (!attachConditionIdList.Contains(id))
					{
						data.SetCondition(catalog, id, this, false);
					}
				}
			}
		}

		public override void changeJobAvailableCondition(Common.GameData.Hero hero, Job sourceJob, int sourceLevel, Job job, int level, bool isSideJob)
		{
            var data = searchPartyFromId(hero.rom.guId) as BattlePlayerData;

            if (data == null)
            {
                return;
            }

            var sourceJobAvailableConditionList = sourceJob?.GetAvailableConditionList(catalog, sourceLevel, true);
            var jobAvailableConditionList = job?.GetAvailableConditionList(catalog, level, true);
            var otherAvailableConditionList = data.Hero.rom.GetAvailableConditionList(catalog, level, true);
            var otherJob = (isSideJob ? data.Hero.jobCast : data.Hero.sideJobCast)?.rom;

            if (otherJob != null)
            {
                otherAvailableConditionList.AddRange(otherJob.GetAvailableConditionList(catalog, otherJob.level, true));
            }

            if (sourceJobAvailableConditionList != null)
            {
                foreach (var condition in sourceJobAvailableConditionList)
                {
                    if (!otherAvailableConditionList.Contains(condition))
                    {
                        data.RecoveryCondition(condition.guId, this, Common.Rom.Condition.RecoveryType.Invalidate);
                    }
                }
            }

            if (jobAvailableConditionList != null)
            {
                foreach (var condition in jobAvailableConditionList)
                {
                    data.SetCondition(catalog, condition.guId, this, false);
                }
            }
        }

        internal void setBattleResult(BattleResultState battleResult)
        {
            switch (battleResult)
            {
                case BattleResultState.Win:
                    lastBattleResult = 1;
                    break;
                case BattleResultState.Lose_Advanced_GameOver:
                case BattleResultState.Lose_Continue:
                case BattleResultState.Lose_GameOver:
                    lastBattleResult = 2;
                    break;
                case BattleResultState.Escape:
                case BattleResultState.Escape_ToTitle:
                    lastBattleResult = 3;
                    break;
            }
        }

        public override void StopCameraAnimation()
        {
            var viewer = battle.battleViewer as BattleViewer3D;
            if (viewer == null)
                return;

            viewer.StopCameraAnimation();
        }

        public override BattleCharacterBase getActiveCharacter()
        {
            var active = battle.commandSelectPlayer as BattleCharacterBase;
            if (active == null)
                active = battle.activeCharacter;
            return active;
        }

        public override int getActiveCharacterCampType()
        {
            var active = getActiveCharacter();
            if (active is BattlePlayerData)
                return 0;
            else if (active is BattleEnemyData)
                return 1;
            return -1;
        }

        public override int getActiveCharacterIndex()
        {
            return battle.SearchIndex(getActiveCharacter());
        }

        public override int getActiveCharacterActionType()
        {
            var active = getActiveCharacter();
            if (active == null)
                return -1;

            var cmd = active.selectedBattleCommandType;
            if (active.recentBattleCommandType != null)
                cmd = active.recentBattleCommandType.Value;

            switch (cmd)
            {
                case BattleCommandType.Attack:
                    return 0;
                case BattleCommandType.Guard:
                    return 1;
                case BattleCommandType.Charge:
                    return 2;
                case BattleCommandType.Nothing:
                case BattleCommandType.Nothing_Down:
                case BattleCommandType.Skip:
                    // パーティの逃げるコマンド選択は常に一人目のメンバーにセットされるので、それも考慮する
                    // The party's escape command selection is always set to the first member, so consider that as well.
                    if (battle.playerData.Contains(active) && battle.playerData[0].selectedBattleCommandType == BattleCommandType.PlayerEscape)
                        return 6;

                    return 3;
                case BattleCommandType.Skill:
                case BattleCommandType.SameSkillEffect:
                    return 4;
                case BattleCommandType.Item:
                    return 5;
                case BattleCommandType.MonsterEscape:
                case BattleCommandType.PlayerEscape:
                case BattleCommandType.Leave:
                    return 6;
                case BattleCommandType.Position:
                    return 7;
                case BattleCommandType.Critical:
                case BattleCommandType.ForceCritical:
                    return 8;
				case BattleCommandType.Member:
					return 9;
				default:
                    return -1;
            }
        }

        public override int getActiveCharacterTargetCamp()
        {
            var active = getActiveCharacter();
            if ((active?.targetCharacter?.Length ?? 0) == 0)
                return -1;
            return (active.targetCharacter[0] is BattlePlayerData) ? 0 : 1;
        }

        public override string getActiveCharacterSelectedSkillOrItemName()
        {
            var active = getActiveCharacter();
            if (active == null)
                return "";

            var cmd = active.selectedBattleCommandType;
            if (active.recentBattleCommandType != null)
                cmd = active.recentBattleCommandType.Value;

            if (cmd != BattleCommandType.Skill && cmd != BattleCommandType.SameSkillEffect && cmd != BattleCommandType.Item)
                return "";

            if (active.selectedItem?.item != null)
                return active.selectedItem.item.name;
            else if (active.selectedSkill != null)
                return active.selectedSkill.name;

            return "";
        }

        public override string getActiveCharacterSelectedSkillOrItemTag()
        {
            var active = getActiveCharacter();
            if (active == null)
                return "";

            var cmd = active.selectedBattleCommandType;
            if (active.recentBattleCommandType != null)
                cmd = active.recentBattleCommandType.Value;

            if (cmd != BattleCommandType.Skill && cmd != BattleCommandType.SameSkillEffect && cmd != BattleCommandType.Item)
                return "";

            if (active.selectedItem?.item != null)
                return active.selectedItem.item.tags ?? "";
            else if (active.selectedSkill != null)
                return active.selectedSkill.tags ?? "";

            return "";
        }

        public override int getActiveCharacterTargetIndex()
        {
            var active = getActiveCharacter();
            if ((active?.targetCharacter?.Length ?? 0) == 0)
                return -1;
            var target = active.targetCharacter[0];
            if (target is BattlePlayerData)
            {
                return battle.playerData.IndexOf(target as BattlePlayerData);
            }
            else
            {
                return target.UniqueID - 1;
            }
        }

        internal bool isBusy(Guid eventGuid)
        {
            if(runnerDic.ContainsKey(eventGuid))
                return runnerDic[eventGuid].state == ScriptRunner.ScriptState.Running;

            var runner = mapRunnerBorrowed.FirstOrDefault(x => x.mapChr.rom?.guId == eventGuid);
            if (runner != null)
                return runner.state == ScriptRunner.ScriptState.Running;

            return false;
        }

        internal void SetCurrentCameraMatrix(SharpKmyMath.Matrix4 p, SharpKmyMath.Matrix4 v)
        {
            pp = p;
            vv = v;
        }

        internal void clearCurrentProcessingTrigger()
        {
            currentProcessingTrigger = -1;
        }

        public override void CalcHitCheckResult()
        {
            if ((battle.battleState == BattleState.ReadyExecuteCommand ||
                battle.battleState == BattleState.SetStatusMessageText) &&
                battle.activeCharacter?.lastHitCheckResult == BattleCharacterBase.HitCheckResult.NONE)
            {
                battle.CalcHitCheckResult();
            }
        }

        /// <summary>
        /// バトルメンバーの交代画面のレイアウトを指定する
        /// Specify the layout of the battle member replacement screen
        /// </summary>
        /// <param name="layoutDrawerController"></param>
        public void SetBattleMemberLayout(BattleViewer3D.BattleUI.LayoutDrawerController layoutDrawerController)
        {
            overridedMemberChangeUi = layoutDrawerController;
        }

        /// <summary>
        /// バトルコマンドからの交代用 誰か一人を選んで交代するモード
        /// For substitution from battle command Mode in which you select one person and substitute
        /// </summary>
        /// <param name="commandSelectPlayer"></param>
        /// <param name="waiter"></param>
        internal void ShowBattleMemberChangeScreenSingleMode(BattlePlayerData commandSelectPlayer, out Func<bool> waiter)
        {
            ShowBattleMemberChangeScreen(out waiter);

            var state = (LayoutStateBattleMember)memberChangeUi.LayoutState;
            state.SetBattleMemberChangeSingleMode(commandSelectPlayer);
        }

        /// <summary>
        /// バトルメンバーの交代画面を表示する
        /// Display the battle member replacement screen
        /// </summary>
        /// <param name="waiter"></param>
        public override void ShowBattleMemberChangeScreen(out Func<bool> waiter)
        {
            waiter = null;

            // 未生成であればまずは生成する
            // If it is not generated, generate it first.
            InitializeMemberChangeIfNeeded();

            // 外部からレイアウトが指定されていればそれを適用する
            // If a layout is specified from outside, apply it
            LayoutDrawer origin = memberChangeUi.LayoutDrawer;
            if (overridedMemberChangeUi != null)
            {
                memberChangeUi.LayoutDrawer = overridedMemberChangeUi.drawer;
                overridedMemberChangeUi = null;
            }

            // メンバーの状態をUIに反映させる
            // Reflect member status on UI
            var state = (LayoutStateBattleMember)memberChangeUi.LayoutState;
            state.CollectMemberData();
            state.ConfigureContentProperty();

            memberChangeUi.Show();

            waiter = () =>
            {
                var visible = !memberChangeUi.Hidding;
                if (visible)
                    return true;

                // 非表示になったら入れ替え結果を反映させる
                // When it becomes hidden, reflect the replacement result
                state.PushMemberChangeQueue(memberChangeQueue);

                memberChangeUi.LayoutDrawer = origin;
                return false;
            };
        }

        /// <summary>
        /// メンバーチェンジUIの初期化が必要なら行う
        /// Initialize the member change UI if necessary
        /// </summary>
        private void InitializeMemberChangeIfNeeded()
        {
            var layouts = catalog.getLayoutProperties();
            var layout = owner?.data.system.GetChangedLayout(layouts, LayoutProperties.LayoutNode.UsageInGame.Member);
            if (layout == null)
                return;

            if (memberChangeUi != null && memberChangeUi.LayoutGuid == layout.Guid)
                return;

            memberChangeUi?.Release();
            memberChangeUi = new LayoutManager(owner, catalog, layout, (mng) => new LayoutStateBattleMember(mng, battle, originalBattleParty, originalReserves));
        }

        /// <summary>
        /// 控えが生存しているかどうかを取得する
        /// Get whether a copy is alive or not
        /// </summary>
        /// <returns></returns>
        internal bool IsSurvivingReserveExists()
        {
            InitializeMemberChangeIfNeeded();

            if (memberChangeUi == null)
                return false;

            var state = (LayoutStateBattleMember)memberChangeUi.LayoutState;
            state.CollectMemberData();
            return state.IsSurvivingReserveExists();
        }

        /// <summary>
        /// 控えとの入れ替えを行う
        /// replace with substitute
        /// </summary>
        internal void ReplaceToReserve()
        {
            InitializeMemberChangeIfNeeded();

            var state = (LayoutStateBattleMember)memberChangeUi.LayoutState;
            state.CollectMemberData();
            state.ReplaceToReserve(memberChangeQueue);
        }

        /// <summary>
        /// 控えメンバーをBattleCharacterBaseで取得する
        /// Get backup members with BattleCharacterBase
        /// </summary>
        /// <returns></returns>
        internal IEnumerable<BattleCharacterBase> GetReserves()
        {
            IsSurvivingReserveExists();// 控えのリストを更新する / Update the list of copies

            var state = (LayoutStateBattleMember)memberChangeUi.LayoutState;
            return state.BattleReserve.Select(x => battle.createPlayerData(x, false));
        }

        /// <summary>
        /// イベントから呼び出せるC#メソッドのサンプル
        /// Sample C# method that can be called from an event
        /// </summary>
        /// <returns></returns>
        [BakinFunction(Description ="パーティの先頭のHP割合を取得 / Get the HP rate of the front of the party")]
        public float GetPartyFrontHPRate()
        {
            if (battle.playerData.Count == 0)
                return 0;
            var front = battle.playerData[0];
            return (float)front.HitPoint / front.MaxHitPoint;
        }
    }
}
