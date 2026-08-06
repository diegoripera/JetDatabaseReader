namespace JetDatabaseReader
{
    /// <summary>
    /// Reusable scratch for locating row boundaries inside a data page. One instance per read
    /// operation, reused across every page it visits.
    ///
    /// Replaces a per-page <c>int[]</c> plus a four-stage LINQ chain
    /// (<c>Select().Where().OrderBy().ToArray()</c>) and an O(rows²) linear probe for each row's
    /// end offset.
    /// </summary>
    internal sealed class RowScanner
    {
        /// <summary>Raw offset entries, including the deleted and overflow flag bits.</summary>
        public int[] Raw = new int[64];

        /// <summary>Physical row positions, ascending. Only [0, <see cref="SortedCount"/>) is valid.</summary>
        public int[] Sorted = new int[64];

        public int SortedCount;

        public void EnsureCapacity(int n)
        {
            if (Raw.Length >= n) return;
            Raw = new int[n];
            Sorted = new int[n];
        }
    }
}
