// 这些类型只用于让测试程序集自身充当“假 Assembly-CSharp.dll”，
// 验证扫描器能识别 TMP 字段、set_text、DOText 打字机 sink 与 4 个 UIText 入口。
// 不含任何游戏台词或游戏资源。

namespace TMPro
{
    public class TMP_Text
    {
        public string text { get; set; } = string.Empty;
    }

    public class TextMeshProUGUI : TMP_Text
    {
    }
}

namespace DG.Tweening
{
    public static class ShortcutExtensionsTMPText
    {
        public static void DOText(TMPro.TMP_Text target, string text)
        {
        }
    }
}

namespace FakeGame
{
    public sealed class ChoiceAsset
    {
        public string Id { get; set; } = string.Empty;
    }

    public sealed class UIText
    {
        private TMPro.TMP_Text _label = new TMPro.TextMeshProUGUI();

        public struct CharaStatus
        {
            public int Num;
        }

        public void AnimateText(TMPro.TextMeshProUGUI targetText, string text, float duration, CharaStatus charaStatus, int Num)
        {
            DG.Tweening.ShortcutExtensionsTMPText.DOText(targetText, text);
            _label.text = text;
        }

        public void DOText(CharaStatus charaStatus)
        {
            AnimateText((TMPro.TextMeshProUGUI)_label, "sample", 0.1f, charaStatus, 1);
        }

        public void Choice(ChoiceAsset cs, int hideMask)
        {
            _label.text = "sample";
        }

        public void HandleMessageText(CharaStatus charaStatus, float valueTime)
        {
            AnimateText((TMPro.TextMeshProUGUI)_label, "sample", valueTime, charaStatus, 2);
        }
    }
}
