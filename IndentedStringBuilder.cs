using System.Text;
using System;

namespace NodeGraphCodeGenerator
{
    public class IndentedStringBuilder
    {
        private StringBuilder sb = new StringBuilder();

        private int indentLevel;

        public void AppendLine(string value)
        {
            for (var i = 0; i < indentLevel; i++)
            {
                sb.Append("    ");
            }

            sb.AppendLine(value);
        }

        public IndentScope AppendBlock()
        {
            return new IndentScope(this);
        }

        public override string ToString()
        {
            return sb.ToString();
        }

        public struct IndentScope : IDisposable
        {
            private readonly IndentedStringBuilder sb;

            public IndentScope(IndentedStringBuilder sb)
            {
                this.sb = sb;
                sb.AppendLine("{");
                sb.indentLevel++;
            }

            public void Dispose()
            {
                // Decrease indent before closing bracked, or it will be indented incorrectly
                sb.indentLevel--;
                sb.AppendLine("}");
            }
        }
    }
}
