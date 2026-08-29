using System;
using System.Collections.Generic;
using System.Linq;
using Yukar.Common;
using Yukar.Engine;
using static Yukar.Engine.BattleEnum;
using Rom = Yukar.Common.Rom;

namespace Yukar.Battle
{
    /// <summary>
    /// 採点によって選ばれたスキルと、単体スキルで優先する対象を保持する。
    /// 
    /// 全体スキルなど対象選択が不要な場合、Target は null になる。
    /// 
    /// </summary>
    internal sealed class LearnedSkillSelectionResult
    {
        public Rom.NSkill Skill { get; set; }
        public BattleCharacterBase Target { get; set; }
        public double Score { get; set; }
    }

    /// <summary>
    /// 習得済みスキルを効果内容と現在の戦況から分類・採点する。
    /// 
    /// 新しい評価方針や重みを追加する場合は、主に Evaluate 以下を変更する。
    /// 
    /// </summary>
    internal sealed class LearnedSkillSelector
    {
        private readonly Rom.GameSettings gameSettings;
        private readonly Func<string, BattleCharacterBase, BattleCharacterBase, Guid, float> evaluateFormula;
        private readonly Func<Rom.NSkill, bool> canPayItemCost;

        public LearnedSkillSelector(
            Rom.GameSettings gameSettings,
            Func<string, BattleCharacterBase, BattleCharacterBase, Guid, float> evaluateFormula,
            Func<Rom.NSkill, bool> canPayItemCost)
        {
            this.gameSettings = gameSettings;
            this.evaluateFormula = evaluateFormula;
            this.canPayItemCost = canPayItemCost;
        }

        public LearnedSkillSelectionResult Select(
            BattleCharacterBase user,
            IEnumerable<Rom.NSkill> learnedSkills,
            Rom.LearnedSkillSelectionType selectionType)
        {
            LearnedSkillSelectionResult best = null;

            // 戦闘中に実際に使用可能な習得済みスキルだけを採点し、最高得点を採用する。
            // 
            foreach (var skill in learnedSkills.Where(skill => IsUsable(user, skill)))
            {
                var result = Evaluate(user, skill, selectionType);
                if (result != null && (best == null || result.Score > best.Score))
                {
                    best = result;
                }
            }

            return best;
        }

        private bool IsUsable(BattleCharacterBase user, Rom.NSkill skill)
        {
            // 消費ステータス・消費アイテム・対象人数まで含め、選択後に発動失敗する候補を除外する。
            // 
            return skill != null &&
                skill.option != null &&
                skill.option.availableInBattle &&
                BattleSequenceManagerBase.IsQualifiedSkillCostStatus(user, skill) &&
                canPayItemCost(skill) &&
                !(skill.option.target == Rom.TargetType.OTHER_ONE && user.FriendPartyRefMember.Count <= 1);
        }

        private LearnedSkillSelectionResult Evaluate(
            BattleCharacterBase user,
            Rom.NSkill skill,
            Rom.LearnedSkillSelectionType selectionType)
        {
            // GUIで指定されたメタ種別を、対応する採点処理へ振り分ける。
            // 
            switch (selectionType)
            {
                case Rom.LearnedSkillSelectionType.ATTACK_POWER:
                    return EvaluateAttack(user, skill, false);
                case Rom.LearnedSkillSelectionType.ATTACK_COST:
                    return EvaluateAttack(user, skill, true);
                case Rom.LearnedSkillSelectionType.DEBUFF:
                    return EvaluateSupport(user, skill, skill.EnemyEffectParamSettings, user.EnemyPartyRefMember, false);
                case Rom.LearnedSkillSelectionType.BUFF:
                    return EvaluateSupport(user, skill, skill.FriendEffectParamSettings, GetFriendTargets(user, skill, false), true);
                case Rom.LearnedSkillSelectionType.RECOVERY:
                    return EvaluateRecovery(user, skill);
                default:
                    return null;
            }
        }

        private LearnedSkillSelectionResult EvaluateAttack(BattleCharacterBase user, Rom.NSkill skill, bool costPriority)
        {
            // 敵に効果がなく、ダメージ式も直接ダメージも持たないスキルは攻撃候補にしない。
            // 
            if (!HasEnemyTarget(skill.option.target))
            {
                return null;
            }

            var damageFormulas = GetFormulas(skill.EnemyEffectParamSettings,
                Rom.ConsumptionValueFormulaEffectParam.FormulaEffectType.Damage).ToList();
            // 敵の消費ステータスへの効果は「正の ChangeParam ＝ ダメージ」が実戦の符号規約
            // 
            // (BattleSequenceManager の敵効果適用が effectValue > 0 を消費ダメージとして扱う)。
            // 
            // 味方回復側(EvaluateRecovery)が > 0 を回復として拾うのと対称に、敵ダメージも > 0 で拾う。
            // 
            var damageParams = skill.EnemyEffectParamSettings.GetConsumptionStatusValueChangeEffectParamList()
                .Where(param => param.ChangeParam > 0).ToList();
            if (damageFormulas.Count == 0 && damageParams.Count == 0)
            {
                return null;
            }

            var targets = user.EnemyPartyRefMember
                .Where(target => target.HitPoint > 0 && target.GetReflectionParam(skill, false) == null)
                .ToList();
            if (targets.Count == 0)
            {
                return null;
            }

            // 全体スキルは反射対象だけを外せないため、一人でも反射状態ならスキル自体を避ける。
            // 
            if (IsEnemyAllTarget(skill.option.target) &&
                user.EnemyPartyRefMember.Any(target => target.HitPoint > 0 && target.GetReflectionParam(skill, false) != null))
            {
                return null;
            }

            var attribute = ResolveAttribute(user, skill.EnemyEffectParamSettings);
            BattleCharacterBase bestTarget = null;
            double bestTargetScore = double.MinValue;
            double totalScore = 0;

            // 各対象への期待ダメージを求め、単体用の最高値と全体用の合計値を同時に計算する。
            // 
            foreach (var target in targets)
            {
                double score = 0;
                // 計算式ダメージは実際のバトル式で評価し、HPダメージには属性耐性と防御状態を反映する。
                // 
                foreach (var formula in damageFormulas)
                {
                    var value = Math.Abs(evaluateFormula(formula.Formula, user, target, attribute));
                    if (formula.TargetId == gameSettings.maxHPStatusID)
                    {
                        value *= target.ResistanceAttackAttributePercent(attribute) * target.DamageRate;
                    }
                    score += value;
                }

                // 計算式を使わない直接値・割合ダメージも同じ尺度へ加算する。
                // 
                foreach (var param in damageParams)
                {
                    var value = param.ChangeType == Util.StatusChangeType.Percent
                        ? Math.Abs(target.GetSystemStatus(gameSettings, param.BaseStatusId) * param.ChangeParam / 100.0)
                        : Math.Abs(param.ChangeParam);
                    if (param.StatusId == gameSettings.maxHPStatusID)
                    {
                        value *= target.ResistanceAttackAttributePercent(attribute) * target.DamageRate;
                    }
                    score += value;
                }

                // 命中率を掛け、表示上の威力ではなく実際に命中する期待値として比較する。
                // 
                score *= Math.Max(skill.HitRate, 0) / 100.0;
                totalScore += score;
                if (score > bestTargetScore)
                {
                    bestTargetScore = score;
                    bestTarget = target;
                }
            }

            // 全体攻撃は全対象の合計、単体攻撃は最も有効な一体への値をスキル得点とする。
            // 
            var scoreForTargetType = IsEnemyAllTarget(skill.option.target) ? totalScore : bestTargetScore;
            if (scoreForTargetType <= 0)
            {
                return null;
            }

            if (costPriority)
            {
                // コスト重視では期待ダメージを総コストで割り、低消費で効率のよいスキルを優先する。
                // 
                scoreForTargetType /= 1.0 + GetCost(skill);
            }

            return new LearnedSkillSelectionResult
            {
                Skill = skill,
                Target = IsEnemyOneTarget(skill.option.target) ? bestTarget : null,
                Score = scoreForTargetType,
            };
        }

        private LearnedSkillSelectionResult EvaluateRecovery(BattleCharacterBase user, Rom.NSkill skill)
        {
            // 味方対象を持ち、回復式・直接回復・状態解除のいずれかを持つスキルだけを候補にする。
            // 
            if (!HasFriendTarget(skill.option.target))
            {
                return null;
            }

            var settings = skill.FriendEffectParamSettings;
            var healFormulas = GetFormulas(settings,
                Rom.ConsumptionValueFormulaEffectParam.FormulaEffectType.Heal).ToList();
            var recoveryParams = settings.GetConsumptionStatusValueChangeEffectParamList()
                .Where(param => param.ChangeParam > 0).ToList();
            var detachConditions = settings.GetDetachConditionList()
                .Where(info => info.condition != Rom.DetachConditionEffectParam.OnlyForDownId).ToList();
            if (healFormulas.Count == 0 && recoveryParams.Count == 0 && detachConditions.Count == 0)
            {
                return null;
            }

            // 戦闘不能を解除できるスキルだけ、戦闘不能者を通常の回復対象へ含める。
            // 
            var canRevive = detachConditions.Any(info =>
                Catalog.sInstance.getItemFromGuid<Rom.Condition>(info.condition)?.IsDeadCondition ?? false);
            var targets = GetRecoveryTargets(user, skill, canRevive).ToList();
            BattleCharacterBase bestTarget = null;
            double bestTargetScore = 0;
            double totalScore = 0;

            // 回復可能量ではなく、現在失われている量までを有効回復量として採点する。
            // 
            foreach (var target in targets)
            {
                double score = 0;
                foreach (var formula in healFormulas)
                {
                    var expected = Math.Max(evaluateFormula(formula.Formula, user, target, Guid.Empty), 0);
                    score += Math.Min(expected, GetMissingStatus(user, target, formula.TargetId));
                }

                foreach (var param in recoveryParams)
                {
                    var expected = param.ChangeType == Util.StatusChangeType.Percent
                        ? target.GetSystemStatus(gameSettings, param.BaseStatusId) * param.ChangeParam / 100.0
                        : param.ChangeParam;
                    score += Math.Min(expected, GetMissingStatus(user, target, param.StatusId));
                }

                // 解除対象の状態が実際に付いている場合だけ加点し、無駄な状態回復を避ける。
                // 
                foreach (var condition in detachConditions)
                {
                    if (target.conditionInfoDic.ContainsKey(condition.condition))
                    {
                        score += target.IsDeadCondition() ? target.MaxHitPoint : Math.Max(target.MaxHitPoint * 0.25, 1);
                    }
                }

                totalScore += score;
                if (score > bestTargetScore)
                {
                    bestTargetScore = score;
                    bestTarget = target;
                }
            }

            // 全体回復は全員分の有効量、単体回復は最も必要としている一人の有効量で比較する。
            // 
            var finalScore = IsFriendAllTarget(skill.option.target) ? totalScore : bestTargetScore;
            return finalScore > 0
                ? new LearnedSkillSelectionResult { Skill = skill, Target = bestTarget, Score = finalScore }
                : null;
        }

        private LearnedSkillSelectionResult EvaluateSupport(
            BattleCharacterBase user,
            Rom.NSkill skill,
            Rom.EffectParamSettings settings,
            IEnumerable<BattleCharacterBase> targets,
            bool buff)
        {
            // 強化は味方対象、弱体化は敵対象を持つ場合だけ評価する。
            // 
            if ((buff && !HasFriendTarget(skill.option.target)) || (!buff && !HasEnemyTarget(skill.option.target)))
            {
                return null;
            }

            // 能力変化・属性耐性・状態耐性・状態付与を共通の補助効果として集計する。
            // 
            var statusEffects = settings.EffectParamList
                .OfType<Rom.StatusChangeEffectParam>()
                .Where(param => param.Enabled && !(param is Rom.ConsumptionStatusValueChangeEffectParam))
                .ToList();
            var conditions = settings.GetAttachConditionEffectParamList();
            var defenceEffects = settings.GetAttributeDefenceList().Sum(info => Math.Abs(info.value)) +
                settings.GetConditionDefenceList().Sum(info => Math.Abs(info.value));
            if (statusEffects.Count == 0 && conditions.Count == 0 && defenceEffects == 0)
            {
                return null;
            }

            BattleCharacterBase bestTarget = null;
            double bestTargetScore = 0;
            double totalScore = 0;

            // 対象ごとに効果量を採点し、付与済みの状態変化には重複加点しない。
            // 
            foreach (var target in targets.Where(target => target.HitPoint > 0))
            {
                double score = statusEffects.Sum(param => Math.Abs(param.ChangeParam));
                score += defenceEffects;
                foreach (var condition in conditions)
                {
                    if (!target.conditionInfoDic.ContainsKey(condition.Id))
                    {
                        score += 100;
                    }
                }

                if (buff)
                {
                    // 強化対象が競合した場合は、やや危険な味方を優先して支援する。
                    // 
                    score *= Math.Max(0.25, 1.0 - target.HitPointPercent * 0.25);
                }

                totalScore += score;
                if (score > bestTargetScore)
                {
                    bestTargetScore = score;
                    bestTarget = target;
                }
            }

            var finalScore = buff
                ? (IsFriendAllTarget(skill.option.target) ? totalScore : bestTargetScore)
                : (IsEnemyAllTarget(skill.option.target) ? totalScore : bestTargetScore);
            return finalScore > 0
                ? new LearnedSkillSelectionResult { Skill = skill, Target = bestTarget, Score = finalScore }
                : null;
        }

        private IEnumerable<Rom.ConsumptionValueFormulaEffectParam> GetFormulas(
            Rom.EffectParamSettings settings,
            Rom.ConsumptionValueFormulaEffectParam.FormulaEffectType type)
        {
            // 通常時とクリティカル時の式を同じ効果種別として取得する。
            // 
            return settings.GetEtcEffectParamList()
                .OfType<Rom.ConsumptionValueFormulaEffectParam>()
                .Where(formula => formula.Enabled && formula.FormulaType == type && !string.IsNullOrEmpty(formula.Formula));
        }

        private IEnumerable<BattleCharacterBase> GetRecoveryTargets(BattleCharacterBase user, Rom.NSkill skill, bool canRevive)
        {
            // 「戦闘不能時のみ」の指定を優先し、それ以外では蘇生可能な場合だけ戦闘不能者を残す。
            // 
            var onlyForDown = skill.option.OnlyForDown;
            return GetFriendTargets(user, skill, true).Where(target =>
                onlyForDown ? target.IsDeadCondition() : (!target.IsDeadCondition() || canRevive));
        }

        private IEnumerable<BattleCharacterBase> GetFriendTargets(BattleCharacterBase user, Rom.NSkill skill, bool includeDown)
        {
            // 自分・自分以外・味方全体など、スキルの対象設定に沿って採点対象を組み立てる。
            // 
            IEnumerable<BattleCharacterBase> targets;
            switch (skill.option.target)
            {
                case Rom.TargetType.SELF:
                case Rom.TargetType.SELF_ENEMY_ONE:
                case Rom.TargetType.SELF_ENEMY_ALL:
                    targets = new[] { user };
                    break;
                case Rom.TargetType.OTHER_ONE:
                case Rom.TargetType.OTHERS:
                case Rom.TargetType.OTHERS_ALL:
                case Rom.TargetType.OTHERS_ENEMY_ONE:
                    targets = user.FriendPartyRefMember.Where(target => target != user);
                    break;
                default:
                    targets = user.FriendPartyRefMember;
                    break;
            }

            return includeDown ? targets : targets.Where(target => target.HitPoint > 0);
        }

        private double GetMissingStatus(BattleCharacterBase user, BattleCharacterBase target, Guid statusId)
        {
            // 最大値と現在値の差を返し、過剰回復分が得点にならないようにする。
            // 
            // 同ターン内で他の味方が既に決定済みの回復分は差し引き、複数の回復役が
            // 
            // 同じ対象へお見合いして回復過多になるのを避ける。
            // 
            var info = gameSettings.GetCastStatusParamInfo(statusId);
            if (info == null || !info.Consumption)
            {
                return 0;
            }

            var rawMissing = target.GetSystemStatus(gameSettings, info.guId) - target.consumptionStatusValue.GetStatus(info.guId);
            var reserved = GetPendingRecoveryAmount(user, target, info.guId, rawMissing);
            return Math.Max(rawMissing - reserved, 0);
        }

        /// <summary>
        /// 同ターン内で既に行動決定済み(selectedBattleCommandType == Skill)の味方が、
        /// 
        /// 対象の同じステータスへ見込んでいる回復量を、決定順(パーティ順)を再現して積算する。
        /// 
        /// 状態は持たず、都度キャストの決定結果(selectedSkill/commandTargetList)から動的に導出する
        /// 
        /// ため、ターン開始・終了時のクリア処理は不要。
        /// 
        /// </summary>
        private double GetPendingRecoveryAmount(BattleCharacterBase user, BattleCharacterBase target, Guid statusId, double rawMissing)
        {
            double reserved = 0;
            double remaining = rawMissing;

            foreach (var other in user.FriendPartyRefMember)
            {
                if (other == user || other.selectedBattleCommandType != BattleCommandType.Skill)
                {
                    continue;
                }

                var otherSkill = other.selectedSkill;

                // 単体対象へ絞られた後の commandTargetList だけを見るため、全体回復は対象に含まれない
                // 
                // (対象ごとの内訳が一意でなく、二重カウントを避けられないため意図的に対象外)。
                // 
                if (otherSkill == null || !HasFriendTarget(otherSkill.option.target) || !other.commandTargetList.Contains(target))
                {
                    continue;
                }

                var settings = otherSkill.FriendEffectParamSettings;

                // formula.TargetId / param.StatusId は ConsumptionId 等の生IDのことがあり、statusId(正規化済み
                // 
                // guId)と直接比較すると一致しない。GetCastStatusParamInfo で同じ土俵に正規化してから比較する。
                // 
                double expected = GetFormulas(settings, Rom.ConsumptionValueFormulaEffectParam.FormulaEffectType.Heal)
                    .Where(formula => gameSettings.GetCastStatusParamInfo(formula.TargetId)?.guId == statusId)
                    .Sum(formula => Math.Max(evaluateFormula(formula.Formula, other, target, Guid.Empty), 0));

                expected += settings.GetConsumptionStatusValueChangeEffectParamList()
                    .Where(param => param.ChangeParam > 0 && gameSettings.GetCastStatusParamInfo(param.StatusId)?.guId == statusId)
                    .Sum(param => param.ChangeType == Util.StatusChangeType.Percent
                        ? target.GetSystemStatus(gameSettings, param.BaseStatusId) * param.ChangeParam / 100.0
                        : param.ChangeParam);

                var effective = Math.Min(expected, remaining);
                reserved += effective;
                remaining -= effective;
            }

            return reserved;
        }

        private static double GetCost(Rom.NSkill skill)
        {
            // 各消費ステータスを合算し、消費アイテム1個を暫定的に10ポイントとして換算する。
            // 
            // ゲーム全体の資源価値を変える場合は、この換算係数を調整する。
            // 
            return skill.consumptionSPDic.Where(cost => cost.Value > 0).Sum(cost => cost.Value) +
                Math.Max(skill.option.consumptionItemAmount, 0) * 10.0;
        }

        private static Guid ResolveAttribute(BattleCharacterBase user, Rom.EffectParamSettings settings)
        {
            // 「武器の属性」が指定されている場合は、使用者の現在の武器属性へ置き換える。
            // 
            var attribute = settings.GetEtcEffectParamList()
                .OfType<Rom.SkillAttributeEffectParam>()
                .FirstOrDefault(param => param.Enabled)?.Id ?? Guid.Empty;
            if (attribute == Rom.SkillAttributeEffectParam.WeaponAttributeId)
            {
                return user.Hero?.equipments[Yukar.Common.GameData.Hero.WEAPON_INDEX]?.AttackAttribute ?? Guid.Empty;
            }
            return attribute;
        }

        private static bool IsEnemyOneTarget(Rom.TargetType target)
        {
            // 複合対象も、敵側が単体かどうかで分類する。
            // 
            return target == Rom.TargetType.ENEMY_ONE ||
                target == Rom.TargetType.PARTY_ALL_ENEMY_ONE ||
                target == Rom.TargetType.SELF_ENEMY_ONE ||
                target == Rom.TargetType.OTHERS_ENEMY_ONE;
        }

        private static bool IsEnemyAllTarget(Rom.TargetType target)
        {
            // 複合対象も、敵側が全体かどうかで分類する。
            // 
            return target == Rom.TargetType.ENEMY_ALL ||
                target == Rom.TargetType.ALL ||
                target == Rom.TargetType.OTHERS_ALL ||
                target == Rom.TargetType.PARTY_ONE_ENEMY_ALL ||
                target == Rom.TargetType.SELF_ENEMY_ALL;
        }

        private static bool IsFriendAllTarget(Rom.TargetType target)
        {
            // 回復・強化の合計得点を使うべき味方全体対象を分類する。
            // 
            return target == Rom.TargetType.PARTY_ALL ||
                target == Rom.TargetType.PARTY_RESERVE_ALL ||
                target == Rom.TargetType.ALL ||
                target == Rom.TargetType.OTHERS ||
                target == Rom.TargetType.OTHERS_ALL ||
                target == Rom.TargetType.OTHERS_ENEMY_ONE ||
                target == Rom.TargetType.PARTY_ALL_ENEMY_ONE;
        }

        private static bool HasEnemyTarget(Rom.TargetType target)
        {
            return IsEnemyOneTarget(target) || IsEnemyAllTarget(target);
        }

        private static bool HasFriendTarget(Rom.TargetType target)
        {
            // 敵味方の複合対象を含め、味方側に効果が届く対象種別を列挙する。
            // 
            switch (target)
            {
                case Rom.TargetType.PARTY_ONE:
                case Rom.TargetType.PARTY_ALL:
                case Rom.TargetType.SELF:
                case Rom.TargetType.OTHERS:
                case Rom.TargetType.ALL:
                case Rom.TargetType.SELF_ENEMY_ONE:
                case Rom.TargetType.SELF_ENEMY_ALL:
                case Rom.TargetType.OTHERS_ALL:
                case Rom.TargetType.PARTY_ONE_ENEMY_ALL:
                case Rom.TargetType.PARTY_ALL_ENEMY_ONE:
                case Rom.TargetType.OTHERS_ENEMY_ONE:
                case Rom.TargetType.PARTY_RESERVE_ALL:
                case Rom.TargetType.OTHER_ONE:
                    return true;
                default:
                    return false;
            }
        }
    }
}
