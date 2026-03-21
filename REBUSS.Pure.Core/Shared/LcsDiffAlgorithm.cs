namespace REBUSS.Pure.Core.Shared;

/// <summary>
/// Classic O(m*n) LCS-based diff algorithm.
/// Produces a minimal edit list by computing the longest common subsequence.
/// </summary>
public class LcsDiffAlgorithm : IDiffAlgorithm
{
    public IReadOnlyList<DiffEdit> ComputeEdits(string[] oldLines, string[] newLines)
    {
        var dp = BuildLcsTable(oldLines, newLines);
        return TraceEdits(oldLines, newLines, dp);
    }

    private static int[,] BuildLcsTable(string[] a, string[] b)
    {
        int m = a.Length, n = b.Length;
        var dp = new int[m + 1, n + 1];

        for (int i = m - 1; i >= 0; i--)
        for (int j = n - 1; j >= 0; j--)
            dp[i, j] = a[i] == b[j]
                ? dp[i + 1, j + 1] + 1
                : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        return dp;
    }

    private static List<DiffEdit> TraceEdits(string[] a, string[] b, int[,] dp)
    {
        int m = a.Length, n = b.Length;
        var result = new List<DiffEdit>();
        int ai = 0, bi = 0;

        while (ai < m && bi < n)
        {
            if (a[ai] == b[bi])
                result.Add(new DiffEdit(' ', ai++, bi++));
            else if (dp[ai + 1, bi] >= dp[ai, bi + 1])
                result.Add(new DiffEdit('-', ai++, bi));
            else
                result.Add(new DiffEdit('+', ai, bi++));
        }

        while (ai < m) result.Add(new DiffEdit('-', ai++, bi));
        while (bi < n) result.Add(new DiffEdit('+', ai, bi++));

        return result;
    }
}
