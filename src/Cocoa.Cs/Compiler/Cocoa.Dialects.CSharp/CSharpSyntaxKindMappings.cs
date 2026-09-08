using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis
{
    /// <summary>
    /// C# 娓?RawKind 閳?<see cref="CSharpSyntaxKind"/> 閺勬儳绱￠弰鐘茬殸閿涘湧1-E-2b 閸楁洑绔撮惇鐔烘祲閻愮櫢绱氶妴?
    /// 瑜版挸澧犻崐鐓庣厵娑撳骸鍙℃禍?<see cref="SyntaxKind"/> 鐎瑰苯鍙忕€靛綊缍堥敍? 缂佹寧鐖?<see cref="GreenNode.RawKind"/>閿涘绱?
    /// 閺佸懏妲х亸鍕祮瀵缚娴嗛敍娑欐弓閺夈儱鈧厧鐓欓崚鍡楀级閿涘湑# 閺傛澘顤冩稉鎾崇潣 kind閿涘妞傛禒鍛存付娣囶喗鏁奸張顒€顦╅敍宀冪殶閻劍鏌熼梿鑸垫暭閸斻劊鈧?
    /// </summary>
    public static class CSharpSyntaxKindMappings
    {
        /// <summary>RawKind(int) 閳?C# 鐠囶厽纭剁猾璇茬€烽敍鍫熸弓閻儱鈧壈绻戦崶?BadToken 閸濄劌鍙洪敍澶堚偓?/summary>
        public static CSharpSyntaxKind ToCSharpSyntaxKind(int rawKind)
        {
            return rawKind >= 0 && rawKind <= (int)CSharpSyntaxKind.QuestionQuestionEqualsToken
                ? (CSharpSyntaxKind)rawKind
                : CSharpSyntaxKind.BadToken;
        }

        /// <summary>閸忓彉闊╅懕鏂挎値閺嬫矮濡囬敍鍫ｇ箖濞撯剝鈧緤绱氶埆?C# 鐠囶厽纭剁猾璇茬€烽妴?/summary>
        public static CSharpSyntaxKind ToCSharpSyntaxKind(SyntaxKind kind) => ToCSharpSyntaxKind((int)kind);

        /// <summary>C# 鐠囶厽纭剁猾璇茬€?閳?RawKind(int)閿? 缂佹寧鐖茬€涙ê鍋嶉崐纭风礆閵?/summary>
        public static int ToRawKind(CSharpSyntaxKind kind) => (int)kind;
    }
}