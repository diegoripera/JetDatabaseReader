namespace JetDatabaseReader
{
    /// <summary>Controls how OLE Object columns are rendered.</summary>
    public enum OleObjectMode
    {
        /// <summary>
        /// Decode the blob and return a <c>data:</c> URI. Costs the blob plus a base64 string
        /// roughly 1.33x its size, so a row holding a 10 MB image peaks at over 20 MB.
        /// </summary>
        DataUri = 0,

        /// <summary>
        /// Return the literal <c>"(OLE)"</c> without reading or encoding the payload. The LVAL
        /// pages holding the blob are never read, which is what makes scanning a table of
        /// attachments affordable on a memory-constrained host.
        /// </summary>
        Placeholder = 1
    }
}
