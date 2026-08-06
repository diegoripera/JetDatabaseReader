namespace JetDatabaseReader
{
    // Result type for the internal LVAL chain reader.
    //
    // Data may be longer than Length: the chain is read into a single buffer sized from the
    // memo header, and a chain that ends early leaves the tail unused. Callers must honour
    // Length rather than Data.Length — that is what lets the reader skip a second copy.
    internal sealed class LvalChainResult
    {
        public byte[] Data { get; }
        public int Length { get; }
        public string Error { get; }

        private LvalChainResult(byte[] data, int length, string error)
        {
            Data = data;
            Length = length;
            Error = error;
        }

        public static LvalChainResult Success(byte[] data, int length) => new LvalChainResult(data, length, null);
        public static LvalChainResult Failure(string error) => new LvalChainResult(null, 0, error);
    }
}
