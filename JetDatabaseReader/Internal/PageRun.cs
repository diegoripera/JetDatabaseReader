namespace JetDatabaseReader
{
    /// <summary>
    /// A contiguous span of data pages belonging to one table.
    ///
    /// JET allocates table pages in extents, so a table's pages arrive in long consecutive runs.
    /// Storing runs instead of one entry per page turns the index for a 2 GB database from
    /// megabytes into a few kilobytes, which matters when a process keeps several databases open.
    /// </summary>
    internal struct PageRun
    {
        public long Start;
        public int Count;

        public long End => Start + Count - 1;
    }
}
