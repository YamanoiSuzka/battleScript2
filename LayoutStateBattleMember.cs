using Yukar.Engine;
using Yukar.Common.GameData;
using System.Collections.Generic;
using System.Linq;
using System;
using System.Windows.Forms;

namespace Yukar.Battle
{
    /// <summary>
    /// レイアウトのメンバー変更の動作
    /// Layout Member Change Behavior
    /// </summary>
    public class LayoutStateBattleMember : AbstractLayoutState
    {
        /// <summary>
        /// パーティメンバーに関する情報を格納する構造体
        /// A structure that stores information about party members
        /// </summary>
        public class MemberProperty
        {
            /// <summary>
            /// メンバーか控えか
            /// member or reserve
            /// </summary>
            public enum Group
            {
                None = -1,
                PARTY,
                RESERVE,
            }

            public Hero Hero { get; set; }
            public int Index { get; set; }
            public Group GroupInfomation { get; set; }
            public int ContainerIndex { get; set; }

            /// <summary>
            /// MemberPropertyクラスのデフォルトコンストラクタ
            /// Default constructor for MemberProperty class
            /// </summary>
            public MemberProperty()
            {
                Hero = null;
                Index = -1;
                GroupInfomation = Group.None;
                ContainerIndex = 0;
            }

            /// <summary>
            /// MemberPropertyクラスのパラメータ付きコンストラクタ
            /// Constructor with parameters for MemberProperty class
            /// </summary>
            /// <param name="hero">ヒーロー</param>
            /// <param name="hero">hero</param>
            /// <param name="index">インデックス</param>
            /// <param name="index">index</param>
            /// <param name="groupInfomation">グループ情報</param>
            /// <param name="groupInfomation">Group information</param>
            /// <param name="containerIndex">コンテナインデックス</param>
            /// <param name="containerIndex">Container index</param>
            public MemberProperty(Hero hero, int index, Group groupInfomation, int containerIndex)
                : base()
            {
                Hero = hero;
                Index = index;
                GroupInfomation = groupInfomation;
                ContainerIndex = containerIndex;
            }
        }

        private List<MemberProperty> memberProperties { get; set; }
        public List<BattlePlayerData> OriginalBattleParty { get; set; }
        public List<Hero> OriginalReserves { get; set; }
        public List<BattlePlayerData> PreviousBattleParty { get; set; }
        public List<Hero> BattleParty { get; set; }
        public List<Hero> BattleReserve { get; set; }
        public int AliveMemberCount { get => BattleParty.Count(x => !x.IsDeadCondition()); }

        private BattleSequenceManager battle;
        private bool closeWhenNextCommit;

        /// <summary>
        /// LayoutStateBattleMemberクラスのコンストラクタ
        /// Constructor for LayoutStateBattleMember class
        /// </summary>
        /// <param name="layoutManager">レイアウトマネージャー</param>
        /// <param name="layoutManager">layout manager</param>
        /// <param name="battle">バトルシーケンスマネージャー</param>
        /// <param name="battle"></param>
        /// <param name="originalBattleParty">バトル開始時のパーティ（レイアウト再生成時も上書きされない）</param>
        /// <param name="originalBattleParty"></param>
        /// <param name="originalReserves">バトル開始時の控えメンバー（レイアウト再生成時も上書きされない）</param>
        /// <param name="originalReserves"></param>
        public LayoutStateBattleMember(LayoutManager layoutManager, BattleSequenceManager battle,
            List<BattlePlayerData> originalBattleParty, List<Hero> originalReserves)
            : base(layoutManager)
        {
            this.layoutManager = layoutManager;
            memberProperties = new List<MemberProperty>();

            this.battle = battle;
            OriginalBattleParty = originalBattleParty;
            OriginalReserves = originalReserves;
        }

        protected override void InitializeCallback()
        {
            // 戦闘パーティ情報を取得
            // Get battle party information
            CollectMemberData();

            //var res = GameMain.mapScene.menuWindow.Res;
            //res.partyChars = GameMain.mapScene.menuWindow.RefreshPartyChr(GameMain.data.party, res.partyChars);
            //res.reserveChars = GameMain.mapScene.menuWindow.RefreshReserveChr(GameMain.data.party, res.reserveChars);

            ConfigureContentProperty();
            //res.partyChars = GameMain.mapScene.menuWindow.RefreshPartyChr(GameMain.data.party, res.partyChars);

            if (BattleParty.Count > 0) SelectProperty.SelectHero = BattleParty[0];
            GameContent.Hero = SelectProperty.SelectHero;
            UpdateGameContent();
            LayoutDrawer.EnableLoopPage(false);
        }

        public void CollectMemberData()
        {
            // 戦闘パーティ情報を取得
            // Get battle party information
            PreviousBattleParty = battle.playerData.ToList();
            BattleParty = battle.playerData.Select(x => x.Hero).ToList();
            GameMain.data.party.Members.Clear();
            GameMain.data.party.Members.AddRange(BattleParty);

            // 控え情報はメンバー情報から再生成する
            // Backup information is regenerated from member information
            GameMain.data.party.ReservesRaw.Clear();
            GameMain.data.party.ReservesRaw.AddRange(OriginalReserves.Union(OriginalBattleParty.Select(x => x.player)).Where(x => !BattleParty.Contains(x)));
            BattleReserve = GameMain.data.party.Reserves;

            // シングルモードを解除する
            // Cancel single mode
            closeWhenNextCommit = false;
        }

        /// <summary>
        /// パーティに関するコンテンツが入ったメニューコンテナのインデックスを取得する
        /// Get the index of the menu container containing content about the party
        /// </summary>
        /// <returns>menuContainers リスト上のインデックス</returns>
        /// <returns></returns>
        private int GetPartyContainerIndex()
        {
            // GetMostMajorContent は renderObjects のインデックスを期待するため renderObjects を走査するが、
            // 
            // 戻り値は menuContainers 上のインデックスでなければならない (描画コンテナ等が混在する場合に
            // 
            // renderObjects のインデックスでは下流の API と整合しないため)。
            // 
            int menuContainerIndex = 0;
            for (int i = 0; i < LayoutDrawer.renderObjects.Count; i++)
            {
                if (!(LayoutDrawer.renderObjects[i] is MenuContainer))
                    continue;

                var content = LayoutDrawer.GetMostMajorContent(i);
                if (content == SpecialTextRenderer.SpecialTexts.Party)
                    return menuContainerIndex;
                menuContainerIndex++;
            }
            return 0;
        }

        /// <summary>
        /// 選択処理を実行します
        /// Execute selection process
        /// </summary>
        /// <returns>処理が成功した場合はtrue、失敗した場合はfalse</returns>
        /// <returns>true if the process was successful, false if it failed</returns>
        public override bool Select()
        {
            if (!base.Select()) return false;

            // マウスで非フォーカス側のコンテナ(メンバー/控え)をクリックした場合、そちらへフォーカスとカーソルを移す
            // 
            // (フォーカスの無いメニューコンテナは通常マウス入力を受け付けないため、ここで明示的に処理する)
            // 
            if (Touch.IsDown())
            {
                var clickedFocusIndex = LayoutDrawer.GetFocusedMenuContainerIndex();
                var otherContainerIndex = clickedFocusIndex == 0 ? 1 : 0;
                var hovering = LayoutDrawer.GetHoveringIndexIgnoringFocus(otherContainerIndex);
                if (hovering.Item2 && ChangeFocusMenuContainer())
                {
                    LayoutDrawer.SetSelectIndex(hovering.Item1, otherContainerIndex);
                    UpdateSelect();
                    Audio.PlaySound(GameMain.se.select);
                    return true;
                }
            }

            // ページ移動の際にメンバーと控えの選択のフォーカスを変更可能にする
            // Allow the focus of member and copy selection to be changed when moving pages
            var focusContainerIndex = LayoutDrawer.GetFocusedMenuContainerIndex();
            if (LayoutDrawer.PressNextPage(focusContainerIndex))
            {
                // ページ移動したフレームは処理を行わない
                // Frames whose pages are moved are not processed.
                if (LayoutDrawer.MovedPage(focusContainerIndex))
                {
                    UpdateSelect();
                    return true;
                }
                if (!LayoutDrawer.IsSelectedLastPage(focusContainerIndex))
                {
                    UpdateSelect();
                    return true;
                }
                if (ChangeFocusMenuContainer())
                {
                    UpdateSelect();
                    Audio.PlaySound(GameMain.se.select);
                }
                return true;
            }
            if (LayoutDrawer.PressPreviousPage(focusContainerIndex))
            {
                if (LayoutDrawer.MovedPage(focusContainerIndex))
                {
                    UpdateSelect();
                    return true;
                }
                if (!LayoutDrawer.IsSelectedFirstPage(focusContainerIndex))
                {
                    UpdateSelect();
                    return true;
                }
                if (ChangeFocusMenuContainer())
                {
                    UpdateSelect();
                    Audio.PlaySound(GameMain.se.select);
                }
                return true;
            }

            // DASHキーによるフォーカス切替。タッチのダブルクリック決定(HaveDecidedByTouch)はここに含めない:
            // 
            // 含めてしまうとダブルクリックした瞬間にフォーカスが次のコンテナへ切り替わり、直後に呼ばれる
            // 
            // Decide()がクリックした項目とは別の(切替後の)コンテナに対して実行されてしまう。
            // 
            if (Input.KeyTest(Input.StateType.TRIGGER, Input.KeyStates.DASH, Input.GameState.MENU))
            {
                ChangeFocusMenuContainer();
                UpdateSelect();
                return true;
            }

            UpdateSelect();
            return true;
        }

        /// <summary>
        /// 決定ボタンが押された時の処理を実行します
        /// Execute the process when the OK button is pressed
        /// </summary>
        /// <returns>処理が成功した場合はtrue、失敗した場合はfalse</returns>
        /// <returns>true if the process was successful, false if it failed</returns>
        public override bool Decide()
        {
            if (!base.Decide()) return false;

            //var party = GameMain.data.party;
            var action = LayoutDrawer.GetSelectAction(LayoutDrawer.GetFocusedMenuContainerIndex(true));
            if (action != Common.Rom.MenuSettings.MenuItem.ActionType.MEMBER_SELECT && action != Common.Rom.MenuSettings.MenuItem.ActionType.RESERVE_SELECT)
            {
                return true;
            }
            var focusIndex = LayoutDrawer.GetFocusedMenuContainerIndex();
            var group = MemberProperty.Group.PARTY;
            if (action == Common.Rom.MenuSettings.MenuItem.ActionType.RESERVE_SELECT)
            {
                group = MemberProperty.Group.RESERVE;
            }
            var selectIndex = LayoutDrawer.GetSelectIndex(focusIndex);
            Hero hero = null;
            if (group == MemberProperty.Group.PARTY && BattleParty.Count > selectIndex && selectIndex > -1) hero = BattleParty[selectIndex];
            if (group == MemberProperty.Group.RESERVE && BattleReserve.Count > selectIndex && selectIndex > -1) hero = BattleReserve[selectIndex];

            // プレイヤーが一人の場合に控えに移動はできない
            // If there is only one player, it is not possible to move to the reserve.
            if (hero == null && group == MemberProperty.Group.RESERVE && BattleParty.Count == 1)
            {
                Audio.PlaySound(GameMain.se.cancel);
                return true;
            }

            // 選んだキャストが交代不可の場合は選択できない
            // Cannot be selected if the selected cast cannot be replaced.
            if (hero != null && hero.rom.prohibitReplacement)
            {
                Audio.PlaySound(GameMain.se.cancel);
                return true;
            }

            // 選択メンバーの情報を作成
            // Create information for selected members
            var selectedMemberProperty = new MemberProperty(hero, selectIndex, group, focusIndex);

            // まだメンバーが選択されていない場合は選択状態にする
            // If a member is not selected yet, select it
            if (memberProperties.Count == 0)
            {
                SetSelected(selectedMemberProperty);
                Audio.PlaySound(GameMain.se.decide);
                return true;
            }

            // どちらもnull(nullの方に移動するはありえない)
            // Both are null (it is impossible to move towards null)
            if (memberProperties[0].Hero is null && hero is null)
            {
                Audio.PlaySound(GameMain.se.cancel);
                return true;
            }
            // 同じグループのときにどちらかが null は同じ場所への移動なのでありえない
            // If both are null when they are in the same group, it is impossible because they move to the same location.
            if (group == memberProperties[0].GroupInfomation)
            {
                if (memberProperties[0].Hero is null || hero is null)
                {
                    Audio.PlaySound(GameMain.se.cancel);
                    return true;
                }
            }

            // パーティが残り一人の場合、選択中の控えが死んでいる場合は交代できない
            // 
            if (AliveMemberCount <= 1 &&
                memberProperties[0].GroupInfomation != selectedMemberProperty.GroupInfomation &&
                memberProperties[0].GroupInfomation == MemberProperty.Group.RESERVE &&
                memberProperties[0].Hero is object &&
                memberProperties[0].Hero.IsDeadCondition() &&
                hero != null)
            {
                Audio.PlaySound(GameMain.se.cancel);
                return true;
            }

            // パーティが残り一人の場合、死んでいるメンバーとの交代や控えへの移動はできない
            // 
            if (AliveMemberCount <= 1 &&
                memberProperties[0].GroupInfomation != selectedMemberProperty.GroupInfomation &&
                memberProperties[0].GroupInfomation == MemberProperty.Group.PARTY &&
                memberProperties[0].Hero is object &&
                !memberProperties[0].Hero.IsDeadCondition() &&
                hero.IsDeadCondition())
            {
                Audio.PlaySound(GameMain.se.cancel);
                return true;
            }

            if (ChangeMember(memberProperties[0], selectedMemberProperty))
            {
                Commit();

                //var res = GameMain.mapScene.menuWindow.Res;
                //res.partyChars = GameMain.mapScene.menuWindow.RefreshPartyChr(GameMain.data.party, res.partyChars);
                //res.reserveChars = GameMain.mapScene.menuWindow.RefreshReserveChr(GameMain.data.party, res.reserveChars);
                //GameMain.mapScene.RefreshHeroMapChr(true);

                // 前の選択状態を解除する
                // Deselect previous selection
                var firstFocusIndex = memberProperties[0].ContainerIndex;
                if (memberProperties[0].ContainerIndex != selectedMemberProperty.ContainerIndex)
                {
                    if (ChangeFocusMenuContainer())
                    {
                    }
                }
                LayoutDrawer.ForceBlinkSubContainer(false, memberProperties[0].Index, firstFocusIndex);
                LayoutDrawer.EnableSubContainerPageIndex(true, memberProperties[0].Index, firstFocusIndex);
                //LayoutDrawer.ChangeFocusMenuContainer(false, firstFocusIndex);
                LayoutDrawer.SetSelectIndex(memberProperties[0].Index, firstFocusIndex);
                LayoutDrawer.ResetSubMenuContainerRenderStatus();
                UpdateGameContent();
                memberProperties.Clear();
                ConfigureContentProperty();
                ChangeRenderStatus();
                GameMain.data.party.PartyType = Common.Rom.LayoutProperties.LayoutNode.PartyTypes.Party;
                LayoutDrawer.UpdateSpecialRenderObject();
                GameMain.data.party.RestorePartyType();

                Audio.PlaySound(GameMain.se.decide);

                // パーティから入れ替えコマンドを実行した場合は1回入れ替えたらウィンドウを閉じる
                // If you execute the swap command from the party, close the window after swapping once.
                if (closeWhenNextCommit)
                {
                    BackToPrevious();
                }
            }
            else
            {
                Audio.PlaySound(GameMain.se.cancel);
            }

            return true;
        }

        private void SetSelected(MemberProperty selectedMemberProperty)
        {
            memberProperties.Add(selectedMemberProperty);
            LayoutDrawer.ForceBlinkSubContainer(true, selectedMemberProperty.Index, selectedMemberProperty.ContainerIndex);
            LayoutDrawer.EnableSubContainerPageIndex(false, selectedMemberProperty.Index, selectedMemberProperty.ContainerIndex);
            LayoutDrawer.GoNextSubContainer(selectedMemberProperty.ContainerIndex);
            ConfigureContentProperty();
            ChangeRenderStatus();
            var renderStatus = new AbstractRenderObject.RenderStatus(true);
            renderStatus.indices.Add(selectedMemberProperty.Index);
            renderStatus.alwaysShowCursor = true;
            LayoutDrawer.ChangeSubMenuContainerRenderStatus(renderStatus, false, selectedMemberProperty.ContainerIndex);
            GameMain.data.party.PartyType = Common.Rom.LayoutProperties.LayoutNode.PartyTypes.Party;
            LayoutDrawer.UpdateSpecialRenderObject();
            GameMain.data.party.RestorePartyType();
        }

        /// <summary>
        /// メンバーチェンジをバトルに反映
        /// Reflect member changes in battle
        /// </summary>
        private void Commit()
        {
            var prevParty = battle.PlayerViewDataList.ToList();

            // 控えの更新
            // Update copy
            var reserves = GameMain.data.party.ReservesRaw;
            reserves.Clear();
            foreach (var p in BattleReserve)
            {
                // 直前までにパーティにいたかどうか？
                // Were you at the party just before?
                var data = prevParty.FirstOrDefault(x => x.Hero == p);
                if (data != null)
                {
                    // GameDataに状態を反映する
                    // Reflect state in GameData
                    battle.ApplyPlayerDataToGameData(data);
                }

                reserves.Add(p);
            }

            // パーティの更新
            // party update
            battle.PlayerViewDataList.Clear();
            foreach (var p in BattleParty)
            {
                // 直前までに控えにいたかどうか？
                // Was he there just before?
                var data = prevParty.FirstOrDefault(x => x.Hero == p);
                if (data == null)
                {
                    // 新規にパーティに加わった場合は新規作成する
                    // If you newly join the party, create a new one.
                    data = battle.createPlayerData(p);
                    battle.UpdateBattleStatusData(data);
                }

                battle.PlayerViewDataList.Add(data);
            }

            // MapのPartyデータにも反映する
            // Also reflected in Party data of Map
            GameMain.data.party.Members.Clear();
            GameMain.data.party.Members.AddRange(BattleParty);
        }

        /// <summary>
        /// キャンセルボタンが押された時の処理を実行します
        /// Execute the process when the cancel button is pressed
        /// </summary>
        /// <returns>処理が成功した場合はtrue、失敗した場合はfalse</returns>
        /// <returns>true if the process was successful, false if it failed</returns>
        public override bool Cancel()
        {
            if (!layoutManager.LayoutDrawer.IsAnytingVisible() && !layoutManager.LayoutDrawer.IsAnytingInOutAnimating())
            {
                return false;
            }

            if (memberProperties.Count > 0)
            {
                var firstFocusIndex = memberProperties[0].ContainerIndex;
                LayoutDrawer.ForceBlinkSubContainer(false, memberProperties[0].Index, firstFocusIndex);
                LayoutDrawer.EnableSubContainerPageIndex(true, memberProperties[0].Index, firstFocusIndex);
                LayoutDrawer.ChangeFocusMenuContainer(false, firstFocusIndex);
                LayoutDrawer.SetSelectIndex(memberProperties[0].Index, firstFocusIndex);
                memberProperties.Clear();
                ConfigureContentProperty();
                ChangeRenderStatus();
                LayoutDrawer.UpdateSpecialRenderObject();

                // 単体交代の場合はそのまま閉じる
                // If there is a single replacement, just close it.
                if (closeWhenNextCommit)
                {
                    BackToPrevious();
                    Audio.PlaySound(GameMain.se.cancel);
                }
                return true;
            }

            SetVariableValue(SetVariableSituation.CANCEL);
            BackToPrevious();
            Audio.PlaySound(GameMain.se.cancel);
            return true;
        }

        private void UpdateSelect()
        {
            var action = LayoutDrawer.GetSelectAction(LayoutDrawer.GetFocusedMenuContainerIndex(true));
            if (action != Common.Rom.MenuSettings.MenuItem.ActionType.MEMBER_SELECT && action != Common.Rom.MenuSettings.MenuItem.ActionType.RESERVE_SELECT)
            {
                return;
            }
            var group = MemberProperty.Group.PARTY;
            if (action == Common.Rom.MenuSettings.MenuItem.ActionType.RESERVE_SELECT)
            {
                group = MemberProperty.Group.RESERVE;
            }
            var focusContainerIndex = LayoutDrawer.GetFocusedMenuContainerIndex();
            var selectIndex = LayoutDrawer.GetSelectIndex(focusContainerIndex);

            Hero hero = null;
            var party = GameMain.data.party;
            if (group == MemberProperty.Group.PARTY && BattleParty.Count > selectIndex && selectIndex > -1) hero = BattleParty[selectIndex];
            if (group == MemberProperty.Group.RESERVE && BattleReserve.Count > selectIndex && selectIndex > -1) hero = BattleReserve[selectIndex];

            var changed = false;
            if (memberProperties.Count == 0)
            {
                if (GameContent.Hero != hero)
                {
                    GameContent.Hero = hero;
                    changed = true;
                }
                if (GameContent.Reserve != null)
                {
                    GameContent.Reserve = null;
                    changed = true;

                }
            }
            if (memberProperties.Count == 1)
            {
                if (GameContent.Reserve != hero)
                {
                    GameContent.Reserve = hero;
                    changed = true;
                }
            }
            if (changed)
            {
                UpdateGameContent();
            }
        }

        protected override void UpdateGameContentCallBack()
        {
            GameMain.data.party.PartyType = Common.Rom.LayoutProperties.LayoutNode.PartyTypes.Party;
        }

        protected override void UpdateGameContentEndCallBack()
        {
        }

        protected override void ConfigureContentPropertyCallBack()
        {
            var canAddReserve = !closeWhenNextCommit;// 単体交代モードの場合は控え追加不可(かならず交代) / In case of single substitution mode, backup cannot be added (must be substituted)
            if (BattleParty.Count < GameMain.catalog.getGameSettings().BattlePlayerMax && memberProperties.Count > 0 &&
                memberProperties[0].GroupInfomation == MemberProperty.Group.RESERVE)
            {
                LayoutDrawer.ConfigureMenuContainerContentProperty(BattleParty.Count + 1, 0);
            }
            else
            {
                LayoutDrawer.ConfigureMenuContainerContentProperty(BattleParty.Count, 0);
            }
            if (canAddReserve && memberProperties.Count > 0 &&
                memberProperties[0].GroupInfomation == MemberProperty.Group.PARTY &&
                (AliveMemberCount > 1 || (memberProperties[0].Hero?.IsDeadCondition() ?? false)))// パーティ内の生存者が残り一人の場合、死んでいるメンバーのみ控えに移動可能 / 
            {
                LayoutDrawer.ConfigureMenuContainerContentProperty(BattleReserve.Count + 1, 1);
            }
            else
            {
                LayoutDrawer.ConfigureMenuContainerContentProperty(BattleReserve.Count, 1);
            }
            LayoutDrawer.ConfigureContentProperty(BattleParty.Count, true, SpecialTextRenderer.SpecialTexts.Status);
        }

        protected override void ChangeRenderStatusCallBack()
        {
            RenderStatus.indices.Clear();
            RenderStatusSub.indices.Clear();

            var index = 0;
            foreach (var hero in BattleParty)
            {
                if (hero.rom.prohibitReplacement) RenderStatus.indices.Add(index);
                ++index;
            }
            index = 0;
            foreach (var hero in BattleReserve)
            {
                if (hero.rom.prohibitReplacement) RenderStatusSub.indices.Add(index);
                ++index;
            }

            LayoutDrawer.ChangeSubMenuContainerRenderStatus(RenderStatus, false, 0);
            LayoutDrawer.ChangeSubMenuContainerRenderStatus(RenderStatusSub, false, 1);

            // 第1選択 (入れ替え元) には「グレーアウト + 常時カーソル」の RenderStatus を
            // 
            // 再適用する。AbstractLayoutState.Select() は currentPage が変化したフレームで
            // 
            // ChangeRenderStatus() を呼び直す (= ResetSubMenuContainerRenderStatus で
            // 
            // SetSelected が設定した状態を全消しする) ため、ここで再適用しないと
            // 
            // バトルコマンド「メンバー交代」のように Show() 直後に SetSelected を呼ぶ
            // 
            // 経路では、次フレーム最初の Select() で grey-out と常時カーソルが失われる。
            // 
            // (currentPage は private int = -1 の初期値で始まるため、レイアウトを開いて
            // 
            //  まだ一度も Select() が走っていない状態だと必ず page(0) と不一致になる。
            // 
            //  「初回のみ」症状はこの初期値起因。)
            // 
            if (memberProperties.Count > 0)
            {
                var selectedProp = memberProperties[0];
                var selectedRenderStatus = new AbstractRenderObject.RenderStatus(true);
                selectedRenderStatus.indices.Add(selectedProp.Index);
                selectedRenderStatus.alwaysShowCursor = true;
                LayoutDrawer.ChangeSubMenuContainerRenderStatus(selectedRenderStatus, false, selectedProp.ContainerIndex);
            }
        }

        private bool ChangeMember(MemberProperty memberPropertySource, MemberProperty memberPropertyDest)
        {
            if (memberPropertySource == null) return false;
            if (memberPropertyDest == null) return false;
            if (memberPropertySource.Hero is null && memberPropertyDest.Hero is null) return false;
            if (memberPropertySource.GroupInfomation == memberPropertyDest.GroupInfomation && memberPropertySource.Index == memberPropertyDest.Index) return false;

            // パーティーと控えから一人ずつ選択しているとき
            // When selecting one person from the party and reserve
            if (memberPropertySource.GroupInfomation != memberPropertyDest.GroupInfomation)
            {
                // 交代
                // replacement
                if (memberPropertySource.Hero is object && memberPropertyDest.Hero is object)
                {
                    if (memberPropertySource.GroupInfomation == MemberProperty.Group.PARTY)
                    {
                        BattleParty[memberPropertySource.Index] = memberPropertyDest.Hero;
                    }
                    if (memberPropertySource.GroupInfomation == MemberProperty.Group.RESERVE)
                    {
                        BattleReserve[memberPropertySource.Index] = memberPropertyDest.Hero;
                    }

                    if (memberPropertyDest.GroupInfomation == MemberProperty.Group.PARTY)
                    {
                        BattleParty[memberPropertyDest.Index] = memberPropertySource.Hero;
                    }
                    if (memberPropertyDest.GroupInfomation == MemberProperty.Group.RESERVE)
                    {
                        BattleReserve[memberPropertyDest.Index] = memberPropertySource.Hero;
                    }
                }
                // パーティーまたは控えに送る
                // Send to party or reserve
                else
                {
                    // 最初がnull
                    // The first is null
                    if (memberPropertySource.Hero is null)
                    {
                        // パーティーに追加
                        // add to party
                        if (memberPropertySource.GroupInfomation == MemberProperty.Group.PARTY)
                        {
                            if (BattleReserve.Count > 1)
                            {
                                BattleParty.Add(memberPropertyDest.Hero);
                                BattleReserve.Remove(memberPropertyDest.Hero);
                            }
                            else
                            {
                                return false;
                            }
                        }
                        // 控えに追加
                        // Add to reserve
                        else
                        {
                            if (BattleParty.Count > 1)
                            {
                                BattleReserve.Add(memberPropertyDest.Hero);
                                BattleParty.Remove(memberPropertyDest.Hero);
                            }
                            else
                            {
                                return false;
                            }
                        }
                    }
                    // 後がnull
                    // The rest is null
                    else
                    {
                        // パーティーに追加
                        // add to party
                        if (memberPropertyDest.GroupInfomation == MemberProperty.Group.PARTY)
                        {
                            if (BattleReserve.Count > 0)
                            {
                                BattleParty.Add(memberPropertySource.Hero);
                                BattleReserve.Remove(memberPropertySource.Hero);
                            }
                            else
                            {
                                return false;
                            }
                        }
                        // 控えに追加
                        // Add to reserve
                        else
                        {
                            if (BattleParty.Count > 1)
                            {
                                BattleReserve.Add(memberPropertySource.Hero);
                                BattleParty.Remove(memberPropertySource.Hero);
                            }
                            else
                            {
                                return false;
                            }
                        }
                    }
                }
            }
            // 順番の入れ替え
            // Reordering
            else
            {
                // パーティー
                // party
                if (memberPropertySource.GroupInfomation == MemberProperty.Group.PARTY)
                {
                    BattleParty[memberPropertyDest.Index] = memberPropertySource.Hero;
                    BattleParty[memberPropertySource.Index] = memberPropertyDest.Hero;
                }
                // 控え
                // reserve
                else
                {
                    BattleReserve[memberPropertyDest.Index] = memberPropertySource.Hero;
                    BattleReserve[memberPropertySource.Index] = memberPropertyDest.Hero;
                }
            }

            return true;
        }

        private bool ChangeFocusMenuContainer()
        {
            var containerAmount = LayoutDrawer.MenuContainersCount;
            var nextIndex = LayoutDrawer.GetFocusedMenuContainerIndex() + 1;
            if (nextIndex >= containerAmount)
            {
                nextIndex = 0;
            }
            var party = GameMain.data.party;
            // 控えに一人もいない場合はフォーカスチェンジできない
            // You cannot change focus if there is no one on standby.
            if (nextIndex == 1 && party.ReserveCount == 0)
            {
                // 控えが0人の場合は変更不可
                // Cannot be changed if there are no substitutes.
                if (GameMain.catalog.getGameSettings().careCenterMaxCount == 0)
                {
                    return false;
                }
                // まだ選んでいない場合は変更不可
                // Cannot be changed if not selected yet
                else if (memberProperties.Count == 0)
                {
                    return false;
                }
                // 単体交代モード (バトルコマンド「メンバー交代」) では「控えに追加」の
                // 
                // +1スロットを出さない (ConfigureContentPropertyCallBack の canAddReserve=false) ため、
                // 
                // 控えが0人だと控えコンテナのサブコンテナが0個になる。
                // 
                // そのまま控えへフォーカス移動すると SelectingSubContainer==null となり、
                // 
                // GoNextImpl が NG を返してコンテナ間移動も検出されず、キャンセル以外で
                // 
                // 戻ってこられなくなるので、この組み合わせの遷移自体を不可にする。
                // 
                else if (closeWhenNextCommit)
                {
                    return false;
                }
            }
            // 元々控えを選択してい控えが空の場合はフォーカスチェンジできない
            // If you originally selected a backup and the backup is empty, you cannot change focus.
            if (nextIndex == 1 && party.ReserveCount == 0 && memberProperties.Count == 1 && memberProperties[0].GroupInfomation == MemberProperty.Group.RESERVE)
            {
                return false;
            }

            LayoutDrawer.ChangeFocusMenuContainer(false, nextIndex);
            LayoutDrawer.UpdateSpecialRenderObject();
            return true;
        }

        internal void SetBattleMemberChangeSingleMode(BattlePlayerData commandSelectPlayer)
        {
            closeWhenNextCommit = true;

            // バトルコマンドからの呼び出しの場合、対象キャラクターを選択状態にする
            // If called from a battle command, select the target character.
            if (commandSelectPlayer != null)
            {
                var commandHero = commandSelectPlayer.Hero;
                var partyIndex = BattleParty.IndexOf(commandHero);
                if (partyIndex >= 0)
                {
                    var containerIndex = GetPartyContainerIndex();

                    // SetSelected は Decide() からの呼び出しを前提としており、
                    // 
                    // 「フォーカスが対象コンテナ上にあり、カーソルが選択対象の上にある」
                    // 
                    // 状態であることを暗黙的に要求している (内部の GoNextSubContainer は
                    // 
                    // カーソルを選択対象の隣へ「ずらす」処理であって、絶対位置への移動ではない)。
                    // 
                    // バトルコマンド経由の場合は Show() 直後で、フォーカスは先頭コンテナ・
                    // 
                    // カーソルは先頭サブコンテナという初期状態なので、ここで明示的に
                    // 
                    // パーティコンテナへフォーカスを移し、カーソルを対象に合わせる。
                    // 
                    LayoutDrawer.ChangeFocusMenuContainer(false, containerIndex);
                    LayoutDrawer.SetSelectIndex(partyIndex, containerIndex);

                    var selectedMemberProperty = new MemberProperty(commandHero, partyIndex, MemberProperty.Group.PARTY, containerIndex);
                    SetSelected(selectedMemberProperty);
                }
            }
        }

        internal void PushMemberChangeQueue(Queue<BattleEnum.MemberChangeData> memberChangeQueue)
        {
            // 表示データを一旦元に戻す
            // Restore display data
            battle.PlayerViewDataList.Clear();
            battle.PlayerViewDataList.AddRange(PreviousBattleParty);

            // 現在のバトルパーティとの差分を算出
            // Calculate the difference with the current battle party
            var currentParty = battle.playerData.Select(x => x.player).ToList();

            // currentParty(旧)→BattleParty(新)の順で列挙されるので、REMOVE→ADDの順でキューに入る
            // It is enumerated in the order of currentParty (old) → BattleParty (new), so it goes in the queue in the order of REMOVE → ADD.
            foreach (var hero in currentParty.Union(BattleParty))
            {
                // 増えたメンバーをADDアイテムとしてキューに入れる
                // Queue additional members as ADD items
                if (!currentParty.Contains(hero))
                {
                    memberChangeQueue.Enqueue(new BattleEnum.MemberChangeData() { cmd = BattleEnum.MemberChangeData.Command.ADD, id = hero, idx = BattleParty.IndexOf(hero) });
                }
                // 消えたメンバーをREMOVEアイテムとしてキューに入れる
                // Queue missing members as REMOVE items
                else if (!BattleParty.Contains(hero))
                {
                    if (closeWhenNextCommit)
                        memberChangeQueue.Enqueue(new BattleEnum.MemberChangeData() { cmd = BattleEnum.MemberChangeData.Command.ADD_CHECK });

                    memberChangeQueue.Enqueue(new BattleEnum.MemberChangeData() { cmd = BattleEnum.MemberChangeData.Command.FADE_OUT, id = hero });
                    memberChangeQueue.Enqueue(new BattleEnum.MemberChangeData() { cmd = BattleEnum.MemberChangeData.Command.REMOVE_NOPACK, id = hero, force = true });
                }
            }

            // 順番の入れ替えを処理
            // Handle reordering
            foreach (var hero in BattleParty)
            {
                var curIdx = currentParty.IndexOf(hero);
                var newIdx = BattleParty.IndexOf(hero);
                if (curIdx >= 0 && curIdx != newIdx)
                {
                    // 交換型
                    // Replaceable type
                    if (closeWhenNextCommit)
                    {
                        // 逆順のエントリも含めて既にキューに積まれていたらスキップ
                        // Skip if it is already in the queue, including entries in reverse order.
                        if (memberChangeQueue.Any(x => x.cmd == BattleEnum.MemberChangeData.Command.SET_IDX &&
                            ((x.idx == newIdx && x.level == curIdx) || (x.idx == curIdx && x.level == newIdx))))
                        {
                            continue;
                        }

                        memberChangeQueue.Enqueue(new BattleEnum.MemberChangeData() { cmd = BattleEnum.MemberChangeData.Command.SET_IDX, level = curIdx, idx = newIdx });
                    }
                    // 挿入型
                    // Insertion type
                    else
                    {
                        memberChangeQueue.Enqueue(new BattleEnum.MemberChangeData() { cmd = BattleEnum.MemberChangeData.Command.SET_IDX, id = hero, idx = newIdx });
                    }
                }
            }
        }

        internal bool IsSurvivingReserveExists()
        {
            return BattleReserve.Any(x => !x.IsDeadCondition());
        }

        internal void ReplaceToReserve(Queue<BattleEnum.MemberChangeData> memberChangeQueue)
        {
            // 現在のメンバーをすべて外す
            // Remove all current members
            foreach (var hero in BattleParty.Reverse<Hero>().ToList())
            {
                memberChangeQueue.Enqueue(new BattleEnum.MemberChangeData() { cmd = BattleEnum.MemberChangeData.Command.FADE_OUT, id = hero });
                memberChangeQueue.Enqueue(new BattleEnum.MemberChangeData() { cmd = BattleEnum.MemberChangeData.Command.REMOVE, id = hero, force = true });
            }

            // 控えのうち生存者をすべてメンバーに加える
            // Add all survivors from reserve to membership
            foreach (var hero in BattleReserve.Where(x => !x.IsDeadCondition()).Take(GameMain.catalog.getGameSettings().BattlePlayerMax).ToList())
            {
                memberChangeQueue.Enqueue(new BattleEnum.MemberChangeData() { cmd = BattleEnum.MemberChangeData.Command.ADD, id = hero });
            }
        }

        internal bool IsSingleMode()
        {
            return closeWhenNextCommit;
        }
    }
}
