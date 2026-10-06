using System;
using System.Collections.Generic;
using System.Text;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// 设置搜索用的拼音首字母。只收录设置界面真正出现过的汉字：
    /// GB2312 里同一字母下的字本来就按拼音排序，所以这张表是离线按码位推出来的，
    /// 运行时不联网、不引第三方库；表里没有的字就当它没有首字母（不影响别的匹配）。
    /// </summary>
    internal static class SettingsSearchInitials
    {
        /// <summary>每个字母一段：字母开头，后面跟首字母是它的字。</summary>
        private const string Grouped = "a安按案|b不便保倍别剥办包半变备宝布并必把报搬摆播本杯板标步比版白百笔绑编背薄补表被败边遍避部闭|c丑串从仓侧充出创初参吃场垂处存尺层差常床成戳才抄抽持插搐撤操材查楚次此池沉测础称程窗策粗纯茶藏触词超车迟采错长除|d东代但低兜典到动单叠地堆多大定对导带底度弹当待得懂打抖挡掉断档段淡点独登的短端第等答订读调达道都钉顶|e二儿尔而额|f付份分反发否复峰放方服法浮符翻范覆费赋辅返防非风|g个估公共关刚功勾各告固国官工归惯感挂搞改攻故更构果根格滚盖管给观规该谷跟过隔高|h互会何候划化号合后含和喝回坏好很恢或户护换核横毫汇活混环画簧糊绘缓航花获话还韩|j久九交仅今介件价佳假具兼决几击剪加即及句叫基境夹将就尽局己建径截技拒挤据捷接旧景机架检焦界监睛矩禁积究简精紧级经结绝继聚胶节荐见觉角解计记距辑近进金键镜间际集静|k克况刻卡口可块壳客宽库开快扣括控框看空考靠馈|l两临乐乱了亮令例六列力历另哩录律拉拎拦来栏流滤漏率理留略立类络老聊联落蓝览论赖路轮连逻里量链陆露|m么们免冒名命密慢描敏明某模母每没沫目码秒锚门面默|n你内匿哪囊奶弄您拟拿挪能那钮|o偶|p判匹平拼排旁派片牌盘碰谱跑配|q七全其切前劝区千却去取启器嵌强情抢擎期权求浅清确签缺请起趋轻齐|r人仍任入如容日染然绕若认让软|s三上事什使删刷势十升受史四塞声失始实宿少尚属市式所手扫搜收数时是树死深生省示社稍竖筛算素索缩署色视设识试说谁身输述速释锁随顺首鼠|t他体停偷台同吞听图填天太头套它态托投拓拖挑推提替条椭添统贴跳退透途通题|w万为五位务唯喂围外威完尾往微我文无未歪物稳维网误问|x下习些享信修像先写协卸向响型学小序形心性息悬想携效新星显析校汐消现相系线细绪续胁虚行西讯许询详象选醒限需项须|y一与义也于云亚亿以优余依允元压原又右员因圆域娱已应延异引影意月有样永沿游源用由眼研移约英营要言议译语越运远钥阅隐雨音页预颜验|z专中主之住作值做再准则制助占只周哔噪在址坐增子字展左张志总执找折择拽指摘撞支整早暂最柱桌止正注浏渲照状症直真着知种站粘糟组缀置者自至致装证诊账质资走转轴载这逐造遭重钟闸阈";

        private static Dictionary<char, char> _map;

        /// <summary>把文案转成首字母串：汉字取声母，英数字原样保留。</summary>
        internal static string Build(string text)
        {
            if (String.IsNullOrEmpty(text))
            {
                return String.Empty;
            }

            EnsureMap();
            StringBuilder builder = new StringBuilder(text.Length);
            for (int index = 0; index < text.Length; index++)
            {
                char ch = text[index];
                if (ch >= '0' && ch <= '9')
                {
                    builder.Append(ch);
                    continue;
                }

                if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z'))
                {
                    builder.Append(Char.ToLowerInvariant(ch));
                    continue;
                }

                if (_map.TryGetValue(ch, out char initial))
                {
                    builder.Append(initial);
                }
            }

            return builder.ToString();
        }

        private static void EnsureMap()
        {
            if (_map != null)
            {
                return;
            }

            Dictionary<char, char> map = new Dictionary<char, char>();
            char current = '\0';
            for (int index = 0; index < Grouped.Length; index++)
            {
                char ch = Grouped[index];
                if (ch == '|')
                {
                    current = '\0';
                    continue;
                }

                if (current == '\0' && ch >= 'a' && ch <= 'z')
                {
                    current = ch;
                    continue;
                }

                if (current != '\0')
                {
                    map[ch] = current;
                }
            }

            _map = map;
        }
    }
}