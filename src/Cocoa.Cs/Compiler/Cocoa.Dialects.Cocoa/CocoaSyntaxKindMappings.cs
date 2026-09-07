using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis
{
    /// <summary>
    /// Cocoa 渚?RawKind 鈫?<see cref="CocoaSyntaxKind"/> 鏄惧紡鏄犲皠锛圥1-E-2b 鍗曚竴鐪熺浉鐐癸級銆?
    /// 褰撳墠鍊煎煙涓庡叡浜?<see cref="SyntaxKind"/> 瀹屽叏瀵归綈锛? 缁挎爲 <see cref="GreenNode.RawKind"/>锛夛紝
    /// 鏁呮槧灏勫嵆寮鸿浆锛涙湭鏉ュ€煎煙鍒嗗弶锛圕O 鏂板涓撳睘 kind锛夋椂浠呴渶淇敼鏈锛岃皟鐢ㄦ柟闆舵敼鍔ㄣ€?
    /// </summary>
    public static class CocoaSyntaxKindMappings
    {
        /// <summary>RawKind(int) 鈫?Cocoa 璇硶绫诲瀷锛堟湭鐭ュ€艰繑鍥?BadToken 鍝ㄥ叺锛夈€?/summary>
        public static CocoaSyntaxKind ToCocoaSyntaxKind(int rawKind)
        {
            return rawKind >= 0 && rawKind <= (int)CocoaSyntaxKind.LocalFunctionDeclaration
                ? (CocoaSyntaxKind)rawKind
                : CocoaSyntaxKind.BadToken;
        }

        /// <summary>鍏变韩鑱斿悎鏋氫妇锛堣繃娓℃€侊級鈫?Cocoa 璇硶绫诲瀷銆?/summary>
        public static CocoaSyntaxKind ToCocoaSyntaxKind(SyntaxKind kind) => ToCocoaSyntaxKind((int)kind);

        /// <summary>Cocoa 璇硶绫诲瀷 鈫?RawKind(int)锛? 缁挎爲瀛樺偍鍊硷級銆?/summary>
        public static int ToRawKind(CocoaSyntaxKind kind) => (int)kind;
    }
}