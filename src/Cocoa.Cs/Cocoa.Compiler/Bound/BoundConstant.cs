namespace Cocoa.CodeAnalysis.Binding
{
    public sealed class BoundConstant
    {
        public BoundConstant(object value)
        {
            Value = value;
        }

        public object Value { get; set; }
    }
}
