using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    public class EvaluationTests
    {
        [Theory]
        [InlineData("1", 1)]
        [InlineData("+1", 1)]
        [InlineData("-1", -1)]
        [InlineData("~1", -2)]
        [InlineData("12 + 34", 46)]
        [InlineData("12 - 3", 9)]
        [InlineData("4 * 2", 8)]
        [InlineData("9 / 3", 3)]
        [InlineData("(10)", 10)]
        [InlineData("12 == 11", false)]
        [InlineData("3 == 3", true)]
        [InlineData("12 != 11", true)]
        [InlineData("3 != 3", false)]
        [InlineData("3 < 4", true)]
        [InlineData("5 < 4", false)]
        [InlineData("4 <= 4", true)]
        [InlineData("4 <= 5", true)]
        [InlineData("5 <= 4", false)]
        [InlineData("4 > 3", true)]
        [InlineData("4 > 5", false)]
        [InlineData("4 >= 4", true)]
        [InlineData("5 >= 4", true)]
        [InlineData("4 >= 5", false)]
        [InlineData("1 | 2", 3)]
        [InlineData("1 | 0", 1)]
        [InlineData("1 & 2", 0)]
        [InlineData("1 & 0", 0)]
        [InlineData("1 ^ 0", 1)]
        [InlineData("0 ^ 1", 1)]
        [InlineData("1 ^ 3", 2)]
        [InlineData("false == false", true)]
        [InlineData("true == false", false)]
        [InlineData("false != false", false)]
        [InlineData("true != false", true)]
        [InlineData("true && true", true)]
        [InlineData("false || false", false)]
        [InlineData("false | false", false)]
        [InlineData("false | true", true)]
        [InlineData("true | false", true)]
        [InlineData("true | true", true)]
        [InlineData("false & false", false)]
        [InlineData("false & true", false)]
        [InlineData("true & false", false)]
        [InlineData("true & true", true)]
        [InlineData("false ^ false", false)]
        [InlineData("true ^ false", true)]
        [InlineData("false ^ true", true)]
        [InlineData("true ^ true", false)]
        [InlineData("true", true)]
        [InlineData("false", false)]
        [InlineData("!true", false)]
        [InlineData("!false", true)]
        [InlineData("var a = 10 return 10", 10)]
        [InlineData("\"test\"", "test")]
        [InlineData("\"te\"\"st\"", "te\"st")]
        [InlineData("\"test\" == \"test\"", true)]
        [InlineData("\"test\" != \"test\"", false)]
        [InlineData("\"test\" == \"abc\"", false)]
        [InlineData("\"test\" != \"abc\"", true)]
        [InlineData("\"test\" + \"abc\"", "testabc")]
        [InlineData("{ var a : any = 0 var b : any = \"b\" return a == b }", false)]
        [InlineData("{ var a : any = 0 var b : any = \"b\" return a != b }", true)]
        [InlineData("{ var a : any = 0 var b : any = 0 return a == b }", true)]
        [InlineData("{ var a : any = 0 var b : any = 0 return a != b }", false)]
        [InlineData("{ var a = 10 return a * a }", 100)]
        [InlineData("{ var a = 0 return (a = 10) * a }", 100)]
        [InlineData("{ var a = 0 if a == 0 a = 10 return a }", 10)]
        [InlineData("{ var a = 0 if a == 4 a = 10 return a }", 0)]
        [InlineData("{ var a = 0 if a == 0 a = 10 else a = 5 return a }", 10)]
        [InlineData("{ var a = 0 if a == 4 a = 10 else a = 5 return a }", 5)]
        [InlineData("{ var i = 10 var result = 0 while i > 0 { result = result + i i = i - 1 } return result }", 55)]
        [InlineData("{ var result = 0 for var i = 1 to 10 { result = result + i } return result }", 55)]
        [InlineData("{ var a = 10 for var i = 1 to (a = a - 1) { } return a }", 9)]
        [InlineData("{ var a = 0 do a = a + 1 while a < 10 return a}", 10)]
        [InlineData("{ var i = 0 while i < 5 { i = i + 1 if i == 5 continue } return i }", 5)]
        [InlineData("{ var i = 0 do { i = i + 1 if i == 5 continue } while i < 5 return i }", 5)]
        [InlineData("{ var a = 1 a += (2 + 3) return a }", 6)]
        [InlineData("{ var a = 1 a -= (2 + 3) return a }", -4)]
        [InlineData("{ var a = 1 a *= (2 + 3) return a }", 5)]
        [InlineData("{ var a = 1 a /= (2 + 3) return a }", 0)]
        [InlineData("{ var a = true a &= (false) return a }", false)]
        [InlineData("{ var a = true a |= (false) return a }", true)]
        [InlineData("{ var a = true a ^= (true) return a }", false)]
        [InlineData("{ var a = 1 a |= 0 return a }", 1)]
        [InlineData("{ var a = 1 a &= 3 return a }", 1)]
        [InlineData("{ var a = 1 a &= 0 return a }", 0)]
        [InlineData("{ var a = 1 a ^= 0 return a }", 1)]
        [InlineData("{ var a = 1 var b = 2 var c = 3 a += b += c return a }", 6)]
        [InlineData("{ var a = 1 var b = 2 var c = 3 a += b += c return b }", 5)]
        public void Evaluator_Computes_CorrectValues(string text, object expectedValue)
        {
            AssertValue(text, expectedValue);
        }

        [Theory]
        [InlineData("var a: i32 return a", 0)]
        [InlineData("var a: i32 a = 5 return a", 5)]
        [InlineData("var a: i32 return a + 1", 1)]
        [InlineData("var a: i32 a = 5 a = a + 1 return a", 6)]
        [InlineData("var b: bool return b", false)]
        [InlineData("var d: f64 return d", 0.0)]
        [InlineData("var c: char return i32(c)", 0)]
        [InlineData("var b: u8 return i32(b)", 0)]
        [InlineData("public enum Color { Red, Green, Blue } var c: Color return i32(c)", 0)]
        [InlineData("var s: string return s == s", true)]
        [InlineData("var s: string s = \"abc\" return s", "abc")]
        [InlineData("const x = 5 return x", 5)]
        [InlineData("const x = 5 return x + 1", 6)]
        [InlineData("const x: i32 = 5 return x", 5)]
        public void Evaluator_DefaultInitialization_Computes_CorrectValues(string text, object expectedValue)
        {
            AssertValue(text, expectedValue);
        }

        [Theory]
        [InlineData("{ var result = 0; for (var i = 0; i < 10; i++) { result = result + i; } return result; }", 45)]
        [InlineData("{ var result = 0; for (var i = 0; i <= 10; i = i + 1) { result = result + i; } return result; }", 55)]
        [InlineData("{ var result = 0; for (var i = 10; i > 0; i--) { result = result + i; } return result; }", 55)]
        [InlineData("{ var result = 0; for (var i = 0; i < 5; i++) { if (i == 2) continue; result = result + i; } return result; }", 8)]
        [InlineData("{ var result = 0; for (var i = 10; i > 0; i--) { if (i == 5) continue; result = result + i; } return result; }", 50)]
        [InlineData("{ var result = 0; for (var i = 0;; i++) { result = result + 1; if (result == 5) break; } return result; }", 5)]
        [InlineData("{ var i = 0; for (; i < 5; i = i + 1) { } return i; }", 5)]
        [InlineData("{ var i = 0; for (; i < 5;) { i = i + 1; } return i; }", 5)]
        [InlineData("{ var result = 0; for (;;) { result = result + 1; if (result == 5) break; } return result; }", 5)]
        public void Evaluator_CSStyleFor_Computes_CorrectValues(string text, object expectedValue)
        {
            AssertValueCs(text, expectedValue);
        }

        [Theory]
        [InlineData("{ var result = 0 for (var i = 0 to 10 step 2) { result = result + i } return result }", 30)]
        [InlineData("{ var result = 0 for (var i = 1 to 9 step 2) { result = result + 1 } return result }", 5)]
        [InlineData("{ var result = 0 for (var i = 0 to 10 step 3) { result = result + i } return result }", 18)]
        public void Evaluator_ForStep_Computes_CorrectValues(string text, object expectedValue)
        {
            AssertValue(text, expectedValue);
        }

        [Fact]
        public void Evaluator_ForStep_NonConstant_ReportsError()
        {
            var text = @"
                var s = 2
                for (var i = 0 to 10 step [s]) { }
            ";

            var diagnostics = @"
                for 循环的 step 必须为常量正整数。
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_ForStep_Zero_ReportsError()
        {
            var text = "for (var i = 0 to 10 step [0]) { }";
            var diagnostics = "for 循环的 step 必须为常量正整数。";
            AssertDiagnostics(text, diagnostics);
        }

        [Theory]
        [InlineData("{ var arr = new i32[] {1, 2, 3} var sum = 0 foreach (var x in arr) { sum = sum + x } return sum }", 6)]
        [InlineData("{ var arr = new i32[] {1, 2, 3, 4} var sum = 0 foreach (var x in arr) { if x == 3 continue sum = sum + x } return sum }", 7)]
        [InlineData("{ var arr = new i32[] {1, 2, 3, 4} var sum = 0 foreach (var x in arr) { if x == 3 break sum = sum + x } return sum }", 3)]
        [InlineData("{ var arr = new i32[] {1, 2, 3} var count = 0 foreach (var x in arr) { count = count + 1 } return count }", 3)]
        [InlineData("{ var s = \"abc\" var count = 0 foreach (var c in s) { count = count + 1 } return count }", 3)]
        [InlineData("{ var arr = new i32[] {1, 2} var result = 0 foreach (var x in arr) { foreach (var y in arr) { result = result + x * y } } return result }", 9)]
        public void Evaluator_Foreach_Computes_CorrectValues(string text, object expectedValue)
        {
            AssertValue(text, expectedValue);
        }

        [Fact]
        public void Evaluator_Foreach_LoopVariable_IsReadOnly()
        {
            var text = @"
                var arr = new i32[[]] {1, 2, 3}
                foreach (var x in arr)
                {
                    x [=] 5
                }
            ";

            var diagnostics = @"
                Variable 'x' is read-only and cannot be assigned to.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_Foreach_CollectionNotArrayOrString_ReportsCannotIterate()
        {
            var text = @"using System

                var n = 10
                foreach (var x in [n])
                {
                    Runtime.WriteLine(x)
                }
            ";

            var diagnostics = @"
                foreach 只能遍历数组或字符串，不能遍历 'int'。
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Theory]
        [InlineData("{ var x = 2 switch (x) { case 1: { return 10 } case 2: { return 20 } default: { return 30 } } }", 20)]
        [InlineData("{ var x = 5 switch (x) { case 1: { return 10 } case 2: { return 20 } default: { return 30 } } }", 30)]
        [InlineData("{ var x = 1 switch (x) { case 1: case 2: { return 10 } default: { return 30 } } }", 10)]
        [InlineData("{ var x = 2 switch (x) { case 1: case 2: { return 10 } default: { return 30 } } }", 10)]
        [InlineData("{ var x = 3 switch (x) { case 1: case 2: { return 10 } default: { return 30 } } }", 30)]
        [InlineData("{ var x = 1 switch (x) { case 1, 2, 3: { return 7 } default: { return 0 } } }", 7)]
        [InlineData("{ var x = 2 switch (x) { case 1: { return 1 } case 2 when true: { return 2 } default: { return 3 } } }", 2)]
        [InlineData("{ var x = 2 switch (x) { case 1: { return 1 } case 2 when false: { return 2 } default: { return 3 } } }", 3)]
        [InlineData("{ var s = \"b\" switch (s) { case \"a\": { return 1 } case \"b\": { return 2 } default: { return 3 } } }", 2)]
        public void Evaluator_Switch_Computes_CorrectValues(string text, object expectedValue)
        {
            AssertValue(text, expectedValue);
        }

        [Fact]
        public void Evaluator_Switch_CaseValueMustBeConstant()
        {
            var text = @"using System

                var x = 1
                var y = 2
                switch (x)
                {
                    case [y]:
                    {
                        Console.WriteLine(""one"")
                        break
                    }
                }
            ";

            var diagnostics = @"
                case 值必须是常量。
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_Switch_MissingBreak_ReportsFallThrough()
        {
            var text = @"using System

                var x = 1
                switch (x)
                {
                    case 1:
                    {
                        [Console.WriteLine(""one"")]
                    }
                }
            ";

            var diagnostics = @"
                switch 节体必须以 break/return/continue 结尾（不支持 fall-through）。
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_Switch_ContinueInsideSwitch_ReportsError()
        {
            var text = @"
                var x = 1
                switch (x)
                {
                    case 1:
                    {
                        [continue]
                    }
                }
            ";

            var diagnostics = @"
                continue 只能出现在循环内（不能用于 switch 节）。
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_Switch_MultipleDefault_ReportsError()
        {
            var text = @"
                var x = 1
                [switch] (x)
                {
                    case 1:
                    {
                        break
                    }
                    default:
                    {
                        break
                    }
                    default:
                    {
                        break
                    }
                }
            ";

            var diagnostics = @"
                switch 不能有多个 default 子句。
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Theory]
        [InlineData("{ var i = 1 i++ return i }", 2)]
        [InlineData("{ var i = 5 i-- return i }", 4)]
        [InlineData("{ var i = 1 i++ i++ return i }", 3)]
        [InlineData("{ var i = 1 var r = i++ return r }", 2)]
        [InlineData("{ var d: f64 = 1.5 d++ return d }", 2.5)]
        public void Evaluator_PostfixIncrement_Computes_CorrectValues(string text, object expectedValue)
        {
            AssertValue(text, expectedValue);
        }

        [Theory]
        [InlineData("{ var i = 1 return ++i }", 2)]
        [InlineData("{ var i = 5 return --i }", 4)]
        [InlineData("{ var i = 1 var r = ++i return r }", 2)]
        [InlineData("{ var i = 1 var r = ++i + 10 return r }", 12)]
        [InlineData("{ var i = 3 i = ++i return i }", 4)]
        [InlineData("{ var d: f64 = 1.5 return ++d }", 2.5)]
        public void Evaluator_PrefixIncrement_Computes_CorrectValues(string text, object expectedValue)
        {
            AssertValue(text, expectedValue);
        }

        [Theory]
        [InlineData("true ? 1 : 2", 1)]
        [InlineData("false ? 1 : 2", 2)]
        [InlineData("1 < 2 ? 10 : 20", 10)]
        [InlineData("3 > 4 ? 10 : 20", 20)]
        [InlineData("{ var a = 5 var b = 10 return a > b ? a : b }", 10)]
        [InlineData("{ var x = 0 return true ? (x = 1) : (x = 2) }", 1)]
        [InlineData("{ var x = 0 false ? (x = 1) : (x = 2) return x }", 2)]
        [InlineData("true ? 1.5 : 2", 1.5)]
        [InlineData("1 < 2 ? 3 + 4 : 5 + 6", 7)]
        [InlineData("{ var n = 7 return n % 2 == 0 ? \"even\" : \"odd\" }", "odd")]
        [InlineData("false ? 1 : (true ? 2 : 3)", 2)]
        [InlineData("true ? (false ? 1 : 2) : 3", 2)]
        public void Evaluator_Conditional_Computes_CorrectValues(string text, object expectedValue)
        {
            AssertValue(text, expectedValue);
        }

        [Fact]
        public void Evaluator_Conditional_ConditionNotBool_ReportsCannotConvert()
        {
            var text = @"
                var n = 10
                return [n] ? 1 : 2
            ";

            var diagnostics = @"
                Cannot convert type 'int' to 'bool'.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Theory]
        [InlineData("7 % 3", 1)]
        [InlineData("10 % 2", 0)]
        [InlineData("7 % 3 + 1", 2)]
        [InlineData("-7 % 3", -1)]
        [InlineData("7 % -3", 1)]
        [InlineData("0 % 5", 0)]
        [InlineData("(u8)5 % 2", 1)]
        [InlineData("1 << 4", 16)]
        [InlineData("8 >> 1", 4)]
        [InlineData("-8 >> 1", -4)]
        [InlineData("1 << 10", 1024)]
        [InlineData("16 >> 2", 4)]
        [InlineData("var x = 10 x %= 3 return x", 1)]
        [InlineData("var x = 1 x <<= 4 return x", 16)]
        [InlineData("var x = -16 x >>= 2 return x", -4)]
        [InlineData("var x = 8 x %= 2 return x", 0)]
        public void Evaluator_ModuloAndShift_Computes_CorrectValues(string text, object expectedValue)
        {
            AssertValue(text, expectedValue);
        }

        [Fact]
        public void Evaluator_Var_WithType_NoInitializer_ReportsNoDiagnostics()
        {
            var text = @"
                var a: i32
            ";

            AssertDiagnostics(text, "");
        }

        [Fact]
        public void Evaluator_Let_WithoutInitializer_ReportsError()
        {
            var text = @"
                [let x: i32]
            ";

            var diagnostics = @"
                let 变量必须提供初始值。
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_Const_WithoutInitializer_ReportsError()
        {
            var text = @"
                [const x: i32]
            ";

            var diagnostics = @"
                const 变量必须提供初始值。
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_Var_WithoutTypeAndInitializer_ReportsError()
        {
            var text = @"
                [var x]
            ";

            var diagnostics = @"
                变量声明必须指定类型或初始值。
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_Const_Reassignment_ReportsCannotAssign()
        {
            var text = @"
                const x = 10
                x [=] 0
            ";

            var diagnostics = @"
                Variable 'x' is read-only and cannot be assigned to.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_Array_Initializers()
        {
            AssertValue("var a = new i32[3] {10, 20, 30} return a[1]", 20);
        }

        [Fact]
        public void Evaluator_Array_Assignment()
        {
            AssertValue("var a = new i32[2] a[0] = 5 return a[0]", 5);
        }

        [Fact]
        public void Evaluator_Array_ElementArithmetic()
        {
            AssertValue("var a = new i32[2] {7, 8} return a[0] + a[1]", 15);
        }

        [Fact]
        public void Evaluator_Array_Length()
        {
            AssertValue("var a = new i32[3] return a.Length", 3);
        }

        [Fact]
        public void Evaluator_Indexing_NonArray_ReportsError()
        {
            var text = @"
                var a = 10
                return a[[[0]]]
            ";

            var diagnostics = @"
                Cannot index a value of type 'int'. Indexing requires an array type.
            ";

            AssertDiagnostics(text, diagnostics, false);
        }

        [Fact]
        public void Evaluator_MemberAccess_UnknownMember_ReportsError()
        {
            var text = @"
                var a = 10
                return a.[Length]
            ";

            var diagnostics = @"
                Type 'int' doesn't have a member named 'Length' (only array/string 'Length' is supported).
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_String_Index_Returns_Char()
        {
            AssertValue("var s = \"hello\" return s[1]", 'e');
        }

        [Fact]
        public void Evaluator_String_Length()
        {
            AssertValue("var s = \"hello\" return s.Length", 5);
        }

        [Fact]
        public void Evaluator_String_Substring()
        {
            AssertValue("var s = \"hello\" return s.substring(1, 3)", "ell");
        }

        [Fact]
        public void Evaluator_Char_ConvertToInt()
        {
            AssertValue("var c = 'a' return i32(c)", 97);
        }

        [Fact]
        public void Evaluator_Int_ConvertToChar()
        {
            AssertValue("var i = 98 return char(i)", 'b');
        }

        [Fact]
        public void Evaluator_Char_Equality()
        {
            AssertValue("var c = 'a' return c == 'a'", true);
        }

        [Fact]
        public void Evaluator_Char_Array()
        {
            AssertValue("var a = new char[2] {'x', 'y'} a[0] = 'z' return a[0]", 'z');
        }

        [Fact]
        public void Evaluator_String_IndexAssignment_ReportsError()
        {
            var text = @"
                var s = ""abc""
                s[[[0]]] = 'x'
            ";

            var diagnostics = @"
                A string index is read-only and cannot be assigned to.
            ";

            AssertDiagnostics(text, diagnostics, false);
        }

        [Fact]
        public void Evaluator_Substring_WrongArgumentCount_ReportsError()
        {
            var text = @"
                var s = ""abc""
                return s.[substring](1)
            ";

            var diagnostics = @"
                Function 'substring' requires 2 arguments but was given 1.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_Substring_UnknownMember_ReportsError()
        {
            var text = @"
                var n = 10
                return n.[substring](1, 2)
            ";

            var diagnostics = @"
                Type 'int' doesn't have a member named 'substring' (only array/string 'Length' is supported).
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_Array_InvalidLengthType_ReportsError()
        {
            var text = @"
                var n = ""x""
                var a = new i32[[[n]]] return a
            ";

            var diagnostics = @"
                Cannot convert type 'string' to 'int'.
            ";

            AssertDiagnostics(text, diagnostics, false);
        }

        [Fact]
        public void Evaluator_Enum_ImplicitValues()
        {
            AssertValue("public enum Color { Red, Green, Blue } return i32(Color.Red)", 0);
            AssertValue("public enum Color { Red, Green, Blue } return i32(Color.Green)", 1);
            AssertValue("public enum Color { Red, Green, Blue } return i32(Color.Blue)", 2);
        }

        [Fact]
        public void Evaluator_Enum_ExplicitValues()
        {
            AssertValue("public enum HttpStatus { OK = 200, NotFound = 404, InternalServerError = 500 } return i32(HttpStatus.NotFound)", 404);
            AssertValue("public enum HttpStatus { OK = 200, NotFound = 404, InternalServerError = 500 } return i32(HttpStatus.InternalServerError)", 500);
        }

        [Fact]
        public void Evaluator_Enum_MixedValues()
        {
            AssertValue("public enum E { A, B = 10, C, D = 20, E } return i32(E.C)", 11);
            AssertValue("public enum E { A, B = 10, C, D = 20, E } return i32(E.E)", 21);
        }

        [Fact]
        public void Evaluator_Enum_Equality()
        {
            AssertValue("public enum Color { Red, Green, Blue } var c = Color.Green return c == Color.Green", true);
            AssertValue("public enum Color { Red, Green, Blue } var c = Color.Green return c == Color.Red", false);
            AssertValue("public enum Color { Red, Green, Blue } var c = Color.Green return c != Color.Red", true);
        }

        [Fact]
        public void Evaluator_Enum_ExplicitConversions()
        {
            AssertValue("public enum Color { Red, Green, Blue } return i32(Color(5))", 5);
            AssertValue("public enum Color { Red, Green, Blue } return i32(Color(5)) == 5", true);
        }

        [Fact]
        public void Evaluator_Enum_FunctionParameterAndReturn()
        {
            AssertValue(@"
public enum Color { Red, Green, Blue }
function f(c: Color): i32 { return i32(c) }
function g(): Color { return Color.Blue }
return i32(f(g()))", 2);
        }

        [Fact]
        public void Evaluator_Enum_Array()
        {
            AssertValue(@"
public enum Color { Red, Green, Blue }
var a = new Color[2] {Color.Red, Color.Green}
return i32(a[1])", 1);
        }

        [Fact]
        public void Evaluator_Enum_UnknownMember_ReportsError()
        {
            var text = @"
public enum Color { Red, Green, Blue }
return Color.[Purple]
            ";

            var diagnostics = @"
                Enum 'Color' doesn't have a member named 'Purple'.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_Enum_DuplicateMember_ReportsError()
        {
            var text = @"
public enum Color { Red, [Red], Blue }
            ";

            var diagnostics = @"
                'Red' is already declared.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_Enum_NonIntValue_ReportsError()
        {
            var text = @"
public enum Bad { A = [""x""] }
            ";

            var diagnostics = @"
                The value of enum member 'A' must be an int constant.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_Enum_NameConflict_ReportsError()
        {
            var text = @"
public enum Foo { Red }
function [Foo]() { }
            ";

            var diagnostics = @"
                'Foo' is already declared.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_Enum_IntToEnumImplicit_ReportsError()
        {
            var text = @"
public enum Color { Red, Green }
function f(c: Color) { }
function Main()
{
    f([1])
}";

            var diagnostics = @"
                Cannot convert type 'int' to 'Color'. An explicit conversion exists (are you missing a cast?)
            ";

            AssertDiagnostics(text, diagnostics, false);
        }

        [Fact]
        public void Evaluator_VariableDeclaration_Reports_Redeclaration()
        {
            var text = @"
                {
                    var x = 10
                    var y = 100
                    {
                        var x = 10
                    }
                    var [x] = 5
                }
            ";

            var diagnostics = @"
                'x' is already declared.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_BlockStatement_NoInfiniteLoop()
        {
            var text = @"
                {
                [)][]
            ";

            var diagnostics = @"
                Unexpected token <CloseParenthesisToken>, expected <IdentifierToken>.
                Unexpected token <EndOfFileToken>, expected <CloseBraceToken>.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_InvokeFunctionArguments_Missing()
        {
            var text = @"using System

                Console.[ReadKey]()
            ";

            var diagnostics = @"
                Function 'ReadKey' requires 1 arguments but was given 0.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_InvokeFunctionArguments_Exceeding()
        {
            var text = @"using System

                Console.[WriteLine](""Hello"", "" "", "" world!"")
            ";

            var diagnostics = @"
                Function 'WriteLine' has no overload that matches the argument types.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_InvokeFunctionArguments_NoInfiniteLoop()
        {
            // 赋值运算符出现在实参位置的坏输入：不得无限循环，诊断保持有限数量
            var text = @"using System

                Console.WriteLine(""Hi""=)
            ";

            var syntaxTree = SyntaxTree.Parse(text);
            var compilation = Compilation.CreateScript(null, syntaxTree);
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());

            Assert.True(result.Diagnostics.HasErrors());
            Assert.InRange(result.Diagnostics.Length, 1, 20);
        }

        [Fact]
        public void Evaluator_FunctionParameters_NoInfiniteLoop()
        {
            // 坏类型子句 + 函数体外壳的坏输入：不得无限循环，诊断保持有限数量
            var text = @"using System

                function hi(name: string=)
                {
                    Console.WriteLine(""Hi "" + name + ""!"" )
                }
            ";

            var syntaxTree = SyntaxTree.Parse(text);
            var compilation = Compilation.CreateScript(null, syntaxTree);
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());

            Assert.True(result.Diagnostics.HasErrors());
            Assert.InRange(result.Diagnostics.Length, 1, 20);
        }

        [Fact]
        public void Evaluator_FunctionReturn_Missing()
        {
            var text = @"
                function [add](a: i32, b: i32): i32
                {
                }
            ";

            var diagnostics = @"
                Not all code paths return a value.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_IfStatement_Reports_CannotConvert()
        {
            var text = @"
                {
                    var x = 0
                    if [10]
                        x = 10
                }
            ";

            var diagnostics = @"
                Cannot convert type 'int' to 'bool'.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_WhileStatement_Reports_CannotConvert()
        {
            var text = @"
                {
                    var x = 0
                    while [10]
                        x = 10
                }
            ";

            var diagnostics = @"
                Cannot convert type 'int' to 'bool'.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_DoWhileStatement_Reports_CannotConvert()
        {
            var text = @"
                {
                    var x = 0
                    do
                        x = 10
                    while [10]
                }
            ";

            var diagnostics = @"
                Cannot convert type 'int' to 'bool'.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_ForStatement_Reports_CannotConvert_LowerBound()
        {
            var text = @"
                {
                    var result = 0
                    for var i = [false] to 10
                        result = result + i
                }
            ";

            var diagnostics = @"
                Cannot convert type 'bool' to 'int'.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_ForStatement_Reports_CannotConvert_UpperBound()
        {
            var text = @"
                {
                    var result = 0
                    for var i = 1 to [true]
                        result = result + i
                }
            ";

            var diagnostics = @"
                Cannot convert type 'bool' to 'int'.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Theory]
        [InlineData("{ var result = 0 for (var i = 1 to 5) { result = result + i } return result }", 15)]
        [InlineData("{ var result = 0 for var i = 1 to 5 { result = result + i } return result }", 15)]
        [InlineData("{ var result = 0 for (var i = 0 to 9) { result = result + 1 } return result }", 10)]
        [InlineData("{ var result = 0 for (1 to 5) { result = result + 1 } return result }", 5)]
        [InlineData("{ var result = 0 for 1 to 5 { result = result + 1 } return result }", 5)]
        [InlineData("{ var i = 0 for (i = 1 to 3) { } return i }", 4)]
        [InlineData("{ var i = 10 for i = 1 to 3 { } return i }", 4)]
        [InlineData("{ var sum = 0 for var i = 1 to 4 { if i == 2 continue sum = sum + i } return sum }", 8)]
        [InlineData("{ var result = 0 for (var i = 5 to 5) { result = result + 1 } return result }", 1)]
        [InlineData("{ var total = 0 for var i = 1 to 3 { total = total + i i = i + 1 } return total }", 4)]
        [InlineData("{ var result = 0 for (var i = 10 to 1) { result = result + 1 } return result }", 0)]
        public void Evaluator_RangeForForms_Computes_CorrectValues(string text, object expectedValue)
        {
            AssertValue(text, expectedValue);
        }

        [Fact]
        public void Evaluator_RangeFor_UndefinedVariable_ReportsError()
        {
            var text = @"
                for [i] = 1 to 10
                {
                }
            ";

            var diagnostics = @"
                循环变量 'i' 未定义。省略 var 时循环变量必须在外部作用域已声明。
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_RangeFor_ReadOnlyVariable_ReportsError()
        {
            var text = @"
                let i = 0
                for [i] = 1 to 10
                {
                }
            ";

            var diagnostics = @"
                循环变量 'i' 是只读的，for 循环需要可写变量。
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_RangeFor_LetKeyword_ReportsError()
        {
            var text = @"
                for [let] i = 1 to 10
                {
                }
            ";

            var diagnostics = @"
                for 循环变量只能用 var 声明（不能用 let）。
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_RangeFor_ConstKeyword_ReportsError()
        {
            var text = @"
                for [const] i = 1 to 10
                {
                }
            ";

            var diagnostics = @"
                for 循环变量只能用 var 声明（不能用 const）。
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_CSStyleForStatement_Reports_CannotConvert_Condition()
        {
            var text = @"
                for (var i = 0; [10]; i++)
                {
                }
            ";

            var diagnostics = @"
                Cannot convert type 'int' to 'bool'.
            ";

            AssertDiagnosticsCs(text, diagnostics);
        }

        [Fact]
        public void Evaluator_PostfixIncrement_ReadOnly_ReportsCannotAssign()
        {
            var text = @"
                let x = 10
                x[++]
            ";

            var diagnostics = @"
                Variable 'x' is read-only and cannot be assigned to.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_PostfixIncrement_NotAVariable_ReportsCannotAssign()
        {
            var text = @"
                1[++]
            ";

            var diagnostics = @"
                Variable 'int' is read-only and cannot be assigned to.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_NameExpression_Reports_Undefined()
        {
            var text = @"[x] * 10";

            var diagnostics = @"
                Variable 'x' doesn't exist.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_NameExpression_Reports_NoErrorForInsertedToken()
        {
            var text = @"1 + []";

            var diagnostics = @"
                Unexpected token <EndOfFileToken>, expected <IdentifierToken>.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_UnaryExpression_Reports_Undefined()
        {
            var text = @"[+]true";

            var diagnostics = @"
                Unary operator '+' is not defined for type 'bool'.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_BinaryExpression_Reports_Undefined()
        {
            var text = @"10 [*] false";

            var diagnostics = @"
                Binary operator '*' is not defined for types 'int' and 'bool'.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_CompoundExpression_Reports_Undefined()
        {
            var text = @"var x = 10
                         x [+=] false";

            var diagnostics = @"
                Binary operator '+=' is not defined for types 'int' and 'bool'.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_AssignmentExpression_Reports_Undefined()
        {
            var text = @"[x] = 10";

            var diagnostics = @"
                Variable 'x' doesn't exist.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_CompoundExpression_Assignemnt_NonDefinedVariable_Reports_Undefined()
        {
            var text = @"[x] += 10";

            var diagnostics = @"
                Variable 'x' doesn't exist.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_AssignmentExpression_Reports_NotAVariable()
        {
            var text = @"[Print] = 42";
            var diagnostics = @"
                Variable 'Print' doesn't exist.
            ";
            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_AssignmentExpression_Reports_CannotAssign()
        {
            var text = @"
                {
                    let x = 10
                    x [=] 0
                }
            ";

            var diagnostics = @"
                Variable 'x' is read-only and cannot be assigned to.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_CompoundDeclarationExpression_Reports_CannotAssign()
        {
            var text = @"
                {
                    let x = 10
                    x [+=] 1
                }
            ";

            var diagnostics = @"
                Variable 'x' is read-only and cannot be assigned to.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_AssignmentExpression_Reports_CannotConvert()
        {
            var text = @"
                {
                    var x = 10
                    x = [true]
                }
            ";

            var diagnostics = @"
                Cannot convert type 'bool' to 'int'.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_CallExpression_Reports_Undefined()
        {
            var text = @"[foo](42)";

            var diagnostics = @"
                Function 'foo' doesn't exist.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_CallExpression_Reports_NotAFunction()
        {
            var text = @"
                {
                    let foo = 42
                    [foo](42)
                }
            ";

            var diagnostics = @"
                'foo' is not a function.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_Variables_Can_Shadow_Functions()
        {
            var text = @"
                {
                    let print = 96
                    [print](""test"")
                }
            ";

            var diagnostics = @"
                'print' is not a function.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_Void_Function_Should_Not_Return_Value()
        {
            var text = @"
                function test()
                {
                    return [1]
                }
            ";

            var diagnostics = @"
                Since the function 'test' does not return a value the 'return' keyword cannot be followed by an expression.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_Function_With_ReturnValue_Should_Not_Return_Void()
        {
            var text = @"
                function test(): i32
                {
                    [return]
                }
            ";

            var diagnostics = @"
                An expression of type 'int' is expected.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_Not_All_Code_Paths_Return_Value()
        {
            var text = @"
                function [test](n: i32): bool
                {
                    if (n > 10)
                       return true
                }
            ";

            var diagnostics = @"
                Not all code paths return a value.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_Expression_Must_Have_Value()
        {
            var text = @"
                function test(n: i32)
                {
                    return
                }
                let value = [test(100)]
            ";

            var diagnostics = @"
                Expression must have a value.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_IfStatement_Reports_NotReachableCode_Warning()
        {
            var text = @"using System

                function test()
                {
                    let x = 4 * 3
                    if x > 12
                    {
                        Console.[WriteLine](""x"")
                    }
                    else
                    {
                        Console.WriteLine(""x"")
                    }
                }
            ";

            var diagnostics = @"
                Unreachable code detected.
            ";
            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_ElseStatement_Reports_NotReachableCode_Warning()
        {
            var text = @"
                function test(): i32
                {
                    if true
                    {
                        return 1
                    }
                    else
                    {
                        [return] 0
                    }
                }
            ";

            var diagnostics = @"
                Unreachable code detected.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_WhileStatement_Reports_NotReachableCode_Warning()
        {
            var text = @"
                function test()
                {
                    while false
                    {
                        [continue]
                    }
                }
            ";

            var diagnostics = @"
                Unreachable code detected.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Theory]
        [InlineData("[break]", "break")]
        [InlineData("[continue]", "continue")]
        public void Evaluator_Invalid_Break_Or_Continue(string text, string keyword)
        {
            var diagnostics = $@"
                The keyword '{keyword}' can only be used inside of loops.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_Script_Return()
        {
            var text = @"
                return
            ";

            AssertValue(text, "");
        }

        [Fact]
        public void Evaluator_Parameter_Already_Declared()
        {
            var text = @"
                function sum(a: i32, b: i32, [a: i32]): i32
                {
                    return a + b + c
                }
            ";

            var diagnostics = @"
                A parameter with the name 'a' already exists.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_Function_Must_Have_Name()
        {
            var text = @"
                function [(]a: i32, b: i32): i32
                {
                    return a + b
                }
            ";

            var diagnostics = @"
                Unexpected token <OpenParenthesisToken>, expected <IdentifierToken>.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_Wrong_Argument_Type()
        {
            var text = @"
                function test(n: i32): bool
                {
                    return n > 10
                }
                let testValue = ""string""
                test([testValue])
            ";

            var diagnostics = @"
                Cannot convert type 'string' to 'int'. An explicit conversion exists (are you missing a cast?)
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_Bad_Type()
        {
            var text = @"
                function test(n: [invalidtype])
                {
                }
            ";

            var diagnostics = @"
                Type 'invalidtype' doesn't exist.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        private static void AssertValue(string text, object expectedValue)
        {
            var syntaxTree = SyntaxTree.Parse(text);
            var compilation = Compilation.CreateScript(null, syntaxTree);
            var variables = new Dictionary<VariableSymbol, object>();
            var result = compilation.Evaluate(variables);

            Assert.False(result.Diagnostics.HasErrors());
            Assert.Equal(expectedValue, result.Value);
        }

        private static void AssertValueCs(string text, object expectedValue)
        {
            var syntaxTree = SyntaxTree.ParseCs(text);
            var compilation = Compilation.CreateScript(null, syntaxTree);
            var variables = new Dictionary<VariableSymbol, object>();
            var result = compilation.Evaluate(variables);

            Assert.False(result.Diagnostics.HasErrors());
            Assert.Equal(expectedValue, result.Value);
        }

        [Fact]
        public void Evaluator_StringInterpolation()
        {
            AssertValue("var name = \"Cocoa\" return $\"Hello {name}\"", "Hello Cocoa");
            AssertValue("var a = 10 return $\"{a} + {5} = {a + 5}\"", "10 + 5 = 15");
            AssertValue("$\"{{escaped}} and {2}\"", "{escaped} and 2");
        }

        [Fact]
        public void Evaluator_Byte_Cast_Truncates_To_Unsigned_Byte()
        {
            AssertValue("(u8)300", (byte)44);
        }


        [Fact]
        public void Evaluator_Double_Literal()
        {
            AssertValue("3.14", 3.14);
            AssertValue("0.5", 0.5);
            AssertValue("2.0", 2.0);
        }

        [Fact]
        public void Evaluator_Double_Arithmetic()
        {
            AssertValue("1.5 + 2.25", 3.75);
            AssertValue("3.5 - 1.25", 2.25);
            AssertValue("2.5 * 2", 5.0);
            AssertValue("10.0 / 4", 2.5);
        }

        [Fact]
        public void Evaluator_Double_Comparison()
        {
            AssertValue("1.5 < 2.5", true);
            AssertValue("2.5 <= 2.5", true);
            AssertValue("3.0 > 2.5", true);
            AssertValue("3.0 >= 3.5", false);
            AssertValue("1.5 == 1.5", true);
            AssertValue("1.5 != 2.0", true);
        }

        [Fact]
        public void Evaluator_Double_Conversions()
        {
            AssertValue("(f64)3", 3.0);
            AssertValue("(i32)3.9", 3);
            AssertValue("(u8)3.9", (byte)3);
            AssertValue("(i32)3.14 + 1", 4);
        }

        [Fact]
        public void Evaluator_Double_Byte_EndToEnd()
        {
            AssertValue("(f64)255 == 255.0", true);
            AssertValue("(i32)(3.5 + 0.5)", 4);
        }

        [Fact]
        public void Evaluator_Narrow_Integer_Arithmetic_Promotion()
        {
            // 6e-M21：<32 位同符号二元升 32 位（C# 同构）；混符号按无损规则提升
            AssertValue("(i8)-100 == -100", true);
            AssertValue("(i16)300 * (i16)300 == 90000", true);
            AssertValue("(u16)60000 + (u16)50000 == 110000", true);           // u16+u16→u32
            AssertValue("(i8)-100 + (u8)200 == 100", true);                   // i8+u8→i16
            AssertValue("(i32)2000000000 + (u32)2000000000 == 4000000000", true); // i32+u32→i64
        }

        [Fact]
        public void Evaluator_Unsigned_Division_Remainder_Shift()
        {
            AssertValue("(u32)4000000000 / (u32)2 == 2000000000", true);
            AssertValue("(i64)((u64)18000000000 / (u64)3) == 6000000000", true);
            AssertValue("(u32)0x80000000 >> 1 == 1073741824", true);          // 无符号逻辑右移
            AssertValue("-8 >> 1 == -4", true);                               // 有符号算术右移
            AssertValue("(u32)4000000000 > (u32)3999999999", true);           // 无符号比较
            AssertValue("(u32)-1 > 10", true);                                // u32 位模式 0xFFFFFFFF
        }

        [Fact]
        public void Evaluator_Float32_Arithmetic_And_Conversions()
        {
            AssertValue("(f32)1.5 * (f32)4.0 == (f32)6.0", true);
            AssertValue("(f64)((f32)1.5 + (f32)0.25) == 1.75", true);
            AssertValue("i32(3.9f)", 3);
            AssertValue("(f64)f32(2.75) == 2.75", true);
            AssertValue("i64((f32)1.5 * (f32)4.0) == 6", true);
        }

        [Fact]
        public void Evaluator_Long_Plus_ULong_Reports_Undefined()
        {
            var text = @"
var x: i64 = 1
var y: u64 = 2
var z = x [+] y
            ";

            var diagnostics = @"
                Binary operator '+' is not defined for types 'long' and 'ulong'.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_I128_NotSupported_ReportsError()
        {
            var text = @"var x: [i128] = 0";

            var diagnostics = @"
                Type 'i128' (128-bit) is not supported yet.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_UShort_Constant_OutOfRange_ReportsError()
        {
            var text = @"var x: u16 = [70000]";

            var diagnostics = @"
                Constant value '70000' is out of range for 'ushort'. Use an explicit cast.
            ";

            AssertDiagnostics(text, diagnostics);
        }



        [Fact]
        public void Evaluator_Byte_Cast_From_Int_To_Byte()
        {
            AssertValue("(u8)42", (byte)42);
        }

        [Fact]
        public void Evaluator_Byte_ExplicitCast_To_Int()
        {
            AssertValue("(i32)(u8)255", 255);
        }

        [Fact]
        public void Evaluator_Byte_Implicit_Int_Constant_Comparison()
        {
            AssertValue("(u8)200 == (u8)200", true);
            AssertValue("(u8)200 != (u8)201", true);
        }

        [Fact]
        public void Evaluator_HexLiteral_Int32()
        {
            AssertValue("0xFF", 255);
            AssertValue("0x10", 16);
        }

        [Fact]
        public void Evaluator_Byte_Constant_OutOfRange_ReportsError()
        {
            var text = @"
                let b: u8 = [300]
            ";

            var diagnostics = @"
                Constant value '300' is out of range for 'byte' (0-255). Use an explicit cast.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_Byte_ConstantInRange_NoError()
        {
            var text = @"
                let b: u8 = 255
            ";

            var syntaxTree = SyntaxTree.Parse(text);
            var compilation = Compilation.CreateScript(null, syntaxTree);
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());

            Assert.False(result.Diagnostics.HasErrors());
        }

        [Fact]
        public void Evaluator_Byte_Assignment_OutOfRange_ReportsError()
        {
            var text = @"
                var b: u8 = 1
                b = [300]
            ";

            var diagnostics = @"
                Constant value '300' is out of range for 'byte' (0-255). Use an explicit cast.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_Byte_ExplicitCast_Allows_OutOfRange()
        {
            var text = @"
                let b: u8 = (u8)300
            ";

            var syntaxTree = SyntaxTree.Parse(text);
            var compilation = Compilation.CreateScript(null, syntaxTree);
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());

            Assert.False(result.Diagnostics.HasErrors());
        }

        [Fact]
        public void Evaluator_Byte_UndefinedType_ReportsError()
        {
            var text = @"
                let b: [btye] = 1
            ";

            var diagnostics = @"
                Type 'btye' doesn't exist.
            ";

            AssertDiagnostics(text, diagnostics);
        }

        [Fact]
        public void Evaluator_ExternFunction_WithoutImport_ReportsError()
        {
            var text = @"
stdcall function [GetTickCount](): i32";

            AssertDiagnostics(text, "Top-level extern declarations are deprecated: declare extern functions inside a class import block (e.g. `class Kernel32 { import kernel32.dll { static extern ... } }`).");
        }

        [Fact]
        public void Evaluator_ExternFunction_WithBody_ReportsError()
        {
            var text = @"
class Kernel32
{
    import kernel32.dll
    {
        static stdcall function GetTickCount(): i32
        [{
            return 0
        }]
    }
}";

            AssertDiagnostics(text, "An extern function declaration cannot have a body.");
        }

        [Fact]
        public void Evaluator_ExternFunction_WithImport_ReportsNoDiagnostics()
        {
            var text = @"
class Kernel32
{
    import kernel32.dll
    {
        static stdcall function GetTickCount(): i32
    }
}";

            AssertDiagnostics(text, "");
        }

        [Fact]
        public void Evaluator_ImportBlock_ExternNotStatic_ReportsError()
        {
            var text = @"
class Kernel32
{
    import kernel32.dll
    {
        stdcall function [GetTickCount](): i32
    }
}";

            AssertDiagnostics(text, "An extern function must be declared static.");
        }

        [Fact]
        public void Evaluator_ImportBlock_NonExternFunction_ReportsError()
        {
            var text = @"
class Kernel32
{
    import kernel32.dll
    {
        static function [GetTickCount](): i32
        {
            return 0
        }
    }
}";

            AssertDiagnostics(text, "An import block may only contain extern function declarations (e.g. `static stdcall function GetTickCount(): int`).");
        }

        [Fact]
        public void Evaluator_ImportBlock_SameDllTwoBlocks_ReportsNoDiagnostics()
        {
            var text = @"
class Kernel32
{
    import kernel32.dll
    {
        static stdcall function GetTickCount(): i32
    }

    import kernel32.dll
    {
        static stdcall function GetCurrentProcessId(): i32
    }
}";

            AssertDiagnostics(text, "");
        }

        [Fact]
        public void Evaluator_ExternClassMethod_CallsClassQualified_NoDiagnostics()
        {
            var text = @"
class Kernel32
{
    import kernel32.dll
    {
        static stdcall function GetTickCount(): i32
    }
}

function Main(): i32
{
    return Kernel32.GetTickCount() > 0 ? 1 : 0
}";

            AssertDiagnostics(text, "");
        }

        [Fact]
        public void Evaluator_TopLevelImport_ReportsDeprecationError()
        {
            var text = @"
[import] kernel32.dll";

            AssertDiagnostics(text, "顶层 `import` 声明已废弃：请改用类内 import 块 `class Kernel32 { import kernel32.dll { static extern ... } }`。");
        }

        [Fact]
        public void Evaluator_ExternMetadata_EntryAndCharset_NoDiagnostics()
        {
            var text = @"
class User32
{
    import user32.dll
    {
        static stdcall function MessageBoxW(hWnd: i32, text: i32, caption: i32, type: i32): i32
            extern(entry = MessageBoxA, charset = unicode)
    }
}

function Main(): i32
{
    return User32.MessageBoxW(0, 0, 0, 0)
}";

            AssertDiagnostics(text, "");
        }

        [Fact]
        public void Evaluator_ImportBlock_BlockLevelCharset_NoDiagnostics()
        {
            var text = @"
class Kernel32
{
    import kernel32.dll charset = unicode
    {
        static stdcall function GetTickCount(): i32
    }
}";

            AssertDiagnostics(text, "");
        }

        [Fact]
        public void Evaluator_ExternMetadata_UnknownCharsetValue_ReportsError()
        {
            var text = @"
class Kernel32
{
    import kernel32.dll
    {
        static stdcall function GetTickCount(): i32
            extern(charset = [utf16])
    }
}";

            AssertDiagnostics(text, "未知 charset 值 'utf16'（支持 ansi / unicode / auto）。");
        }

        [Fact]
        public void Evaluator_ExternMetadata_UnknownKey_ReportsError()
        {
            var text = @"
class Kernel32
{
    import kernel32.dll
    {
        static stdcall function GetTickCount(): i32
            extern([exactspelling] = trueValue)
    }
}";

            AssertDiagnostics(text, "未知 extern 元数据键 'exactspelling'（支持 entry / charset，未来 setlasterror/exactspelling 预留）。");
        }

        [Fact]
        public void Evaluator_SyscallFunction_ClassMethod_NoDiagnostics()
        {
            var text = @"using System

class Runtime
{
    syscall function Random(max: i32): i32
}

function Main(): i32
{
    return Runtime.Random(100) < 100 ? 1 : 0
}";

            AssertDiagnostics(text, "");
        }

        [Fact]
        public void Evaluator_SyscallFunction_UnknownName_ReportsError()
        {
            var text = @"
class Runtime
{
    syscall function [NoSuchPrimitive](): i32
}";

            AssertDiagnostics(text, "Syscall function 'NoSuchPrimitive' does not match any built-in primitive.");
        }

        [Fact]
        public void Evaluator_SyscallFunction_WithBody_ReportsError()
        {
            var text = @"
class Runtime
{
    syscall function Random(max: i32): i32
    [{
        return 1
    }]
}";

            AssertDiagnostics(text, "A syscall function declaration cannot have a body.");
        }

        [Fact]
        public void Evaluator_SyscallFunction_TopLevel_ReportsError()
        {
            var text = @"
syscall function [Random](): i32";

            AssertDiagnostics(text, "A syscall function must be declared inside a class (e.g. `class Runtime { syscall function ... }`).");
        }

        [Fact]
        public void Evaluator_Builtin_Sleep_Now_Exit()
        {
            var text = @"using System

function Main(): i32
{
    var t0 = Runtime.TickCount()
    Runtime.Sleep(1)
    var t1 = Runtime.TickCount()
    if t1 < t0
    {
        return 1
    }
    return 0
}";

            var syntaxTree = SyntaxTree.Parse(text);
            var compilation = Compilation.Create("Main", syntaxTree);
            var variables = new Dictionary<VariableSymbol, object>();
            var result = compilation.Evaluate(variables);

            Assert.False(result.Diagnostics.HasErrors());
            Assert.Equal(0, result.Value);
        }

        [Fact]
        public void Evaluator_Builtin_Beep()
        {
            var text = @"using System

function Main(): i32
{
    Runtime.Beep(800, 50)
    return 0
}";

            var syntaxTree = SyntaxTree.Parse(text);
            var compilation = Compilation.Create("Main", syntaxTree);
            var variables = new Dictionary<VariableSymbol, object>();
            var result = compilation.Evaluate(variables);

            Assert.False(result.Diagnostics.HasErrors());
            Assert.Equal(0, result.Value);
        }

        [Fact]
        public void Evaluator_Builtin_MathPrimitives()
        {
            var text = @"using System

function Main(): i32
{
    if Runtime.Sqrt(4.0) != 2.0 return 1
    if Runtime.Floor(2.7) != 2.0 return 2
    if Runtime.Floor(-2.7) != -3.0 return 3
    if Runtime.Ceiling(2.1) != 3.0 return 4
    if Runtime.Ceiling(-2.1) != -2.0 return 5
    if Runtime.Truncate(2.7) != 2.0 return 6
    if Runtime.Truncate(-2.7) != -2.0 return 7
    if Runtime.Round(2.5) != 2.0 return 8
    if Runtime.Round(3.5) != 4.0 return 9
    if Runtime.Round(-2.5) != -2.0 return 10
    return 0
}";

            var syntaxTree = SyntaxTree.Parse(text);
            var compilation = Compilation.Create("Main", syntaxTree);
            var variables = new Dictionary<VariableSymbol, object>();
            var result = compilation.Evaluate(variables);

            Assert.False(result.Diagnostics.HasErrors());
            Assert.Equal(0, result.Value);
        }

        [Fact]
        public void Evaluator_Object_Static_Equals_ReferenceEquals()
        {
            var text = @"using System

function Main(): i32
{
    if !Object.Equals(1, 1) return 1
    if Object.Equals(1, 2) return 2
    if !System.Object.Equals(""a"", ""a"") return 3
    // 值类型实参各自装箱 → ReferenceEquals 恒 false（CLR 语义，与 IL 一致）
    if Object.ReferenceEquals(3, 3) return 4
    // 字面量驻留属实现细节（Evaluator/IL 不一致），同一性断言一律走同变量
    var s = ""a""
    if !Object.ReferenceEquals(s, s) return 5
    return 0
}";

            var syntaxTree = SyntaxTree.Parse(text);
            var compilation = Compilation.Create("Main", syntaxTree);
            var variables = new Dictionary<VariableSymbol, object>();
            var result = compilation.Evaluate(variables);

            Assert.False(result.Diagnostics.HasErrors());
            Assert.Equal(0, result.Value);
        }

        [Fact]
        public void Evaluator_Object_Instance_ToString_GetHashCode()
        {
            var text = @"using System

function Main(): i32
{
    var s = ""abc""
    if s.ToString() != ""abc"" return 1
    if s.GetHashCode() != s.GetHashCode() return 2
    if !(bool)Object.Equals(s, s) return 3
    return 0
}";

            var syntaxTree = SyntaxTree.Parse(text);
            var compilation = Compilation.Create("Main", syntaxTree);
            var variables = new Dictionary<VariableSymbol, object>();
            var result = compilation.Evaluate(variables);

            Assert.False(result.Diagnostics.HasErrors());
            Assert.Equal(0, result.Value);
        }

        [Fact]
        public void Evaluator_Object_GetType_SystemType()
        {
            // 6e-M19 M3-b：GetType() → System.Type；Name 属性 CLR 直通
            var text = @"using System

function Main(): i32
{
    var t = 42.GetType()
    if t.Name != ""Int32"" return 1
    var s = ""abc""
    if s.GetType().Name != ""String"" return 2
    if t.ToString() != ""System.Int32"" return 3
    return 0
}";

            var syntaxTree = SyntaxTree.Parse(text);
            var compilation = Compilation.Create("Main", syntaxTree);
            var variables = new Dictionary<VariableSymbol, object>();
            var result = compilation.Evaluate(variables);

            Assert.False(result.Diagnostics.HasErrors());
            Assert.Equal(0, result.Value);
        }

        [Fact]
        public void Evaluator_Oop_Creation_Fields_InstanceMethods()
        {
            // 6e-M19 M3-c：new + 实例字段读写 + 实例方法 this 环境
            var text = @"using System

public class Counter
{
    private _count: i32
    private _delta: i32

    public constructor(delta: i32)
    {
        _delta = delta
    }

    public function Bump(): i32
    {
        _count = _count + _delta
        return _count
    }
}

function Main(): i32
{
    var c = new Counter(2)
    if c.Bump() != 2 return 1
    if c.Bump() != 4 return 2
    return 0
}";

            AssertRunsClean(text);
        }

        [Fact]
        public void Evaluator_Oop_Inheritance_Polymorphism_VirtualDispatch()
        {
            // 6e-M19 M3-c：经基类引用调用派生 override（运行时链分派）
            var text = @"using System

public class Shape
{
    public virtual function Area(): i32
    {
        return 0
    }
}

public class Square extends Shape
{
    private _side: i32

    public constructor(side: i32)
    {
        _side = side
    }

    public override function Area(): i32
    {
        return _side * _side
    }
}

function Main(): i32
{
    var s: Shape = new Square(3)
    if s.Area() != 9 return 1
    var plain = new Shape()
    if plain.Area() != 0 return 2
    return 0
}";

            AssertRunsClean(text);
        }

        [Fact]
        public void Evaluator_Oop_BaseChain_BaseCall_OverrideToString()
        {
            // 6e-M19 M3-c：extends base(...) 构造链 + base.Method() 直调 + Object.ToString override
            var text = @"using System

public class Animal
{
    protected _name: string

    public constructor(name: string)
    {
        _name = name
    }

    public function Describe(): string
    {
        return _name
    }
}

public class Dog extends Animal
{
    private _legs: i32

    public constructor(name: string, legs: i32) extends base(""dog:"" + name)
    {
        _legs = legs
    }

    public function Tag(): string
    {
        return base.Describe() + ""/"" + (string)_legs
    }

    public override function ToString(): string
    {
        return ""Dog("" + _name + "")""
    }
}

function Main(): i32
{
    var d = new Dog(""rex"", 4)
    if d.Tag() != ""dog:rex/4"" return 1
    var o: object = d
    if o.ToString() != ""Dog(dog:rex)"" return 2
    if d.GetType().Name != ""Dog"" return 3
    if !Object.ReferenceEquals(o, d) return 4
    return 0
}";

            AssertRunsClean(text);
        }

        [Fact]
        public void Evaluator_Oop_StaticField_And_Cctor_LazyInit()
        {
            // 6e-M19 M3-c：静态字段初始化器（.cctor）惰性触发 + 静态方法/静态字段读写
            var text = @"using System

public class Registry
{
    public static _total: i32 = 5

    public static function Add(v: i32): i32
    {
        _total = _total + v
        return _total
    }
}

function Main(): i32
{
    if Registry.Add(3) != 8 return 1
    if Registry._total != 8 return 2
    return 0
}";

            AssertRunsClean(text);
        }

        [Fact]
        public void Evaluator_Oop_ScriptMode_ClassDeclaration()
        {
            // 6e-M19 M3-c：REPL（CreateScript）同管线——类定义/实例化/实例方法在交互模式可用
            var text = @"
public class Point
{
    private _x: i32

    public constructor(x: i32)
    {
        _x = x
    }

    public function Get(): i32
    {
        return _x
    }
}
var p = new Point(3)
return p.Get() * 2";

            var syntaxTree = SyntaxTree.Parse(text);
            var compilation = Compilation.CreateScript(null, syntaxTree);
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());

            Assert.False(result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics));
            Assert.Equal(6, result.Value);
        }

        // ── 6e-M19 M5-a：null 字面量 ──────────────────────────────────────────

        [Theory]
        [InlineData("return null == null", true)]
        [InlineData("return null != null", false)]
        [InlineData("var s: string = null return s == null", true)]
        [InlineData("var s: string = null return s != null", false)]
        [InlineData("var s: string = \"x\" return s == null", false)]
        [InlineData("var s: string return s == null", true)]
        [InlineData("var a: any = null return a == null", true)]
        public void Evaluator_NullLiteral_Comparisons(string text, bool expected)
        {
            AssertValue(text, expected);
        }

        [Fact]
        public void Evaluator_NullLiteral_ClassComparison_Ternary_Concat()
        {
            var text = @"
public class Box
{
    public function Tag(): string
    {
        return ""b""
    }
}
var b = new Box()
if b == null return 1
if !(b != null) return 2
var n: Box = null
if n != null return 3
var picked = true ? b : null
if picked == null return 4
var other = false ? b : null
if other != null return 5
var s: string = null
return s + ""x"" == ""x"" ? 0 : 6";

            var syntaxTree = SyntaxTree.Parse(text);
            var compilation = Compilation.CreateScript(null, syntaxTree);
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());

            Assert.False(result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics));
            Assert.Equal(0, result.Value);
        }

        [Fact]
        public void Evaluator_VarFromNull_ReportsCannotInfer()
        {
            var text = @"[var x = null]";

            AssertDiagnostics(text,
                "Cannot infer the type of 'null'. Use an explicit type declaration instead.");
        }

        [Fact]
        public void Evaluator_Null_ToNonReference_ReportsCannotConvert()
        {
            var text = @"var n: i32 = [null]";

            AssertDiagnostics(text,
                "Cannot convert type 'null' to 'int'.");
        }

        [Fact]
        public void Evaluator_NullCast_ToNonReference_ReportsCannotConvert()
        {
            var text = @"var n: i32 = [(i32)null]";

            AssertDiagnostics(text,
                "Cannot convert type 'null' to 'int'.", assertLocation: false);
        }

        [Fact]
        public void Evaluator_Null_Plus_Int_ReportsUndefinedOperator()
        {
            var text = @"return [null + 1]";

            AssertDiagnostics(text,
                "Binary operator '+' is not defined for types 'null' and 'int'.", assertLocation: false);
        }

        private void AssertRunsClean(string text)
        {
            var syntaxTree = SyntaxTree.Parse(text);
            var compilation = Compilation.Create("Main", syntaxTree);
            var variables = new Dictionary<VariableSymbol, object>();
            var result = compilation.Evaluate(variables);

            Assert.False(result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics));
            Assert.Equal(0, result.Value);
        }

        private void AssertDiagnostics(string text, string diagnosticText)
        {
            AssertDiagnostics(text, diagnosticText, true);
        }

        private void AssertDiagnostics(string text, string diagnosticText, bool assertLocation)
        {
            var annotatedText = AnnotatedText.Parse(text);
            var syntaxTree = SyntaxTree.Parse(annotatedText.Text);
            var compilation = Compilation.CreateScript(null, syntaxTree);
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());

            var expectedDiagnostics = AnnotatedText.UnindentLines(diagnosticText);

            if (annotatedText.Spans.Length != expectedDiagnostics.Length)
            {
                throw new Exception("ERROR: Must mark as many spans as there are expected diagnostics");
            }

            var diagnostics = result.Diagnostics;
            Assert.Equal(expectedDiagnostics.Length, diagnostics.Length);

            for (var i = 0; i < expectedDiagnostics.Length; i++)
            {
                var expectedMessage = expectedDiagnostics[i];
                var actualMessage = diagnostics[i].Message;
                Assert.Equal(expectedMessage, actualMessage);

                if (assertLocation)
                {
                    var expectedSpan = annotatedText.Spans[i];
                    var actualSpan = diagnostics[i].Location.Span;
                    Assert.Equal(expectedSpan, actualSpan);
                }
            }
        }

        private void AssertDiagnosticsCs(string text, string diagnosticText)
        {
            AssertDiagnosticsCs(text, diagnosticText, true);
        }

        private void AssertDiagnosticsCs(string text, string diagnosticText, bool assertLocation)
        {
            var annotatedText = AnnotatedText.Parse(text);
            var syntaxTree = SyntaxTree.ParseCs(annotatedText.Text);
            var compilation = Compilation.CreateScript(null, syntaxTree);
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());

            var expectedDiagnostics = AnnotatedText.UnindentLines(diagnosticText);

            if (annotatedText.Spans.Length != expectedDiagnostics.Length)
            {
                throw new Exception("ERROR: Must mark as many spans as there are expected diagnostics");
            }

            var diagnostics = result.Diagnostics;
            Assert.Equal(expectedDiagnostics.Length, diagnostics.Length);

            for (var i = 0; i < expectedDiagnostics.Length; i++)
            {
                var expectedMessage = expectedDiagnostics[i];
                var actualMessage = diagnostics[i].Message;
                Assert.Equal(expectedMessage, actualMessage);

                if (assertLocation)
                {
                    var expectedSpan = annotatedText.Spans[i];
                    var actualSpan = diagnostics[i].Location.Span;
                    Assert.Equal(expectedSpan, actualSpan);
                }
            }
        }
    }
}
