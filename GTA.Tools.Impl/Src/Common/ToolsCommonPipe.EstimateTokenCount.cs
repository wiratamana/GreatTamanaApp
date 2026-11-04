namespace GTA.Tools.Impl.Common
{
    public static partial class ToolsCommonPipe
    {
        public static int EstimateTokenCount(string? text)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            long asciiChars    = 0;
            long nonAsciiChars = 0;

            foreach (char c in text)
            {
                if (c < 128) asciiChars++;
                else         nonAsciiChars++;
            }

            double estimated = (asciiChars / 4.0) + nonAsciiChars;
            return (int)System.Math.Ceiling(estimated);
        }
    }
}
