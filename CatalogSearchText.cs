using System.Text;

namespace VoidEngine
{
    internal static class CatalogSearchText
    {
        internal static string Normalize(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            StringBuilder normalized = new(value.Length);
            foreach (char character in value)
            {
                if (char.IsLetterOrDigit(character))
                    normalized.Append(char.ToLowerInvariant(character));
            }

            return normalized.ToString();
        }

        internal static bool ContainsNormalized(string? value, string normalizedQuery)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(normalizedQuery))
                return false;

            return Normalize(value).IndexOf(normalizedQuery, System.StringComparison.Ordinal) >= 0;
        }

        internal static int LevenshteinDistance(string left, string right)
        {
            if (left.Length == 0)
                return right.Length;
            if (right.Length == 0)
                return left.Length;

            int[] previous = new int[right.Length + 1];
            int[] current = new int[right.Length + 1];
            for (int column = 0; column <= right.Length; column++)
                previous[column] = column;

            for (int row = 1; row <= left.Length; row++)
            {
                current[0] = row;
                for (int column = 1; column <= right.Length; column++)
                {
                    int substitutionCost = left[row - 1] == right[column - 1] ? 0 : 1;
                    current[column] = System.Math.Min(
                        System.Math.Min(current[column - 1] + 1, previous[column] + 1),
                        previous[column - 1] + substitutionCost
                    );
                }

                int[] swap = previous;
                previous = current;
                current = swap;
            }

            return previous[right.Length];
        }

        internal static int SimilarityThreshold(string normalizedQuery)
        {
            return System.Math.Min(3, System.Math.Max(1, normalizedQuery.Length / 5));
        }
    }
}
