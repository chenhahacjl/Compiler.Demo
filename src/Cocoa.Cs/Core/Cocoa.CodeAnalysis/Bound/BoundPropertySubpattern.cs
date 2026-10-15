namespace Cocoa.CodeAnalysis.Bound
{
    /// <summary>
    /// 属性子模式绑定：NameToken: pattern
    /// </summary>
    public sealed class BoundPropertySubpattern
    {
        public BoundPropertySubpattern(string propertyName, BoundExpression pattern)
        {
            PropertyName = propertyName;
            Pattern = pattern;
        }

        public string PropertyName { get; }
        public BoundExpression Pattern { get; }
    }
}