using SharpKmyGfx;
using System.Linq;
using Yukar.Engine;

namespace Yukar.Battle
{
    /// <summary>
    /// バトル中のダメージ数値のポップを管理するクラス
    /// A class that manages pops of damage numbers during battle
    /// </summary>
    public class BattleDamageTextInfo
    {
        /// <summary>
        /// 表示対象と経過時間
        /// Display target and elapsed time
        /// </summary>
        public class CharacterInfo
        {
            public char c;
            public float timer;
        }

        /// <summary>
        /// 表示タイプ
        /// Display type
        /// </summary>
        public enum TextType
        {
            HitPointDamage,
            MagicPointDamage,
            CriticalDamage,
            HitPointHeal,
            MagicPointHeal,
            Miss,
            Damage,
            Heal,
        }

        public BattleDamageTextInfo(TextType textType, BattleCharacterBase target)
        {
            type = textType;
            targetCharacter = target;

            switch (textType)
            {
                case TextType.HitPointDamage:
                case TextType.CriticalDamage:
                case TextType.HitPointHeal:
                    text = "0";
                    break;

                case TextType.Miss:
                    var gs = Common.Catalog.sInstance.getGameSettings();
                    text = gs.glossary.battle_miss;
                    font = Graphics.LoadFont(Graphics.sInstance.mLayoutFont, gs.systemGraphics.battleMissedFont);
                    break;
            }
        }

        public BattleDamageTextInfo(TextType textType, BattleCharacterBase target, string text, System.Guid statusId, Microsoft.Xna.Framework.Color textColor)
        {
            type = textType;
            this.text = text;
            IsNumberOnlyText = (text.Count(c => char.IsNumber(c)) == text.Length);
            targetCharacter = target;
            this.statusId = statusId;
            this.textColor = textColor;

            var gs = Common.Catalog.sInstance.getGameSettings();
            if (type == TextType.Miss)
                font = Graphics.LoadFont(Graphics.sInstance.mLayoutFont, gs.systemGraphics.battleMissedFont);
        }

        public BattleDamageTextInfo(TextType textType, BattleCharacterBase target, string text)
            : this(textType, target, text, System.Guid.Empty, Microsoft.Xna.Framework.Color.White)
        {
        }

        public readonly TextType type;
        public System.Guid statusId;
        public Microsoft.Xna.Framework.Color textColor;
        public CharacterInfo[] textInfo;
        public readonly string text;
        public Font font;
        public bool IsNumberOnlyText;
        public readonly BattleCharacterBase targetCharacter;
        public int id = -1;// 重なっているときにずらすための目安 / Guidelines for shifting when they overlap
    }
}
