namespace REBUSS.Pure.Core.Models
{
    /// <summary>
    /// Information extracted from the last PR iteration:
    /// the iteration ID and the two commit SHAs used to build the diff.
    /// </summary>
    public sealed record IterationInfo(int Id, string BaseCommit, string TargetCommit)
    {
        public static readonly IterationInfo Empty = new(0, string.Empty, string.Empty);
    }
}
