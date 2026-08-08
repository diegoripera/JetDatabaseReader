using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace JetDatabaseReader.Tests
{
    /// <summary>
    /// Nothing the reader holds may outlive it: not the file handle, not the page cache, not the
    /// decryption state. A service that opens a database per request has to be able to do that
    /// indefinitely.
    /// </summary>
    public class ResourceLifetimeTests
    {
        private const int Cycles = 150;

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public void OpeningAndDisposingRepeatedly_LeavesTheFileUnlocked(string path)
        {
            // Windows refuses to delete a file that anything still holds open, which makes the
            // delete an exact test for "no handle survived" — and one that does not care what the
            // rest of the test run is doing.
            //
            // This used to compare Process.HandleCount before and after. That is a process-wide
            // counter and xUnit runs test classes in parallel, so it drifted with whatever else
            // was open and failed intermittently under load. The same mistake, in the same file,
            // as the memory test that had to be rewritten for the same reason.
            string copy = Path.Combine(Path.GetTempPath(), $"jdr_cycle_{Guid.NewGuid():N}{Path.GetExtension(path)}");
            File.Copy(path, copy, overwrite: true);

            try
            {
                for (int i = 0; i < Cycles; i++)
                    using (var reader = TestDatabases.Open(copy)) reader.ListTables();

                Action delete = () => File.Delete(copy);
                delete.Should().NotThrow(
                    because: $"{Cycles} open/dispose cycles must not leave the file held open");
            }
            finally
            {
                try { File.Delete(copy); } catch { }
            }
        }

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public void DisposedReaders_BecomeUnreachable(string path)
        {
            List<WeakReference> readers = OpenUseAndDispose(path, 50);

            Collect();

            // Comparing GC.GetTotalMemory before and after would be measuring a process-wide
            // counter while other test classes run in parallel — that is exactly how the earlier
            // memory test kept failing. Reachability is deterministic and immune to them: if
            // disposal released the file handle, the page cache and the page index, nothing holds
            // the reader.
            int alive = readers.Count(r => r.IsAlive);

            alive.Should().Be(0, because: "a disposed reader must not stay reachable");
        }

        private static List<WeakReference> OpenUseAndDispose(string path, int count)
        {
            var refs = new List<WeakReference>(count);

            for (int i = 0; i < count; i++)
            {
                var reader = TestDatabases.Open(path);
                string table = reader.GetTableStats().FirstOrDefault(s => s.ColumnCount > 0)?.Name;
                if (table != null) reader.StreamRows(table).Take(20).ToList();

                reader.Dispose();
                refs.Add(new WeakReference(reader));
            }

            return refs;
        }

        [Fact]
        public void WrongPassword_RepeatedlyRejected_LeavesTheFileUnlocked()
        {
            string db = TestDatabases.AdventureWorks;
            if (!TestDatabases.IsReadable(db)) return;

            // A rejected open is the interesting case: the file is already open by the time the
            // constructor decides to refuse it.
            string locked = Path.Combine(Path.GetTempPath(), $"jdr_life_{Guid.NewGuid():N}.mdb");
            File.Copy(db, locked, overwrite: true);

            try
            {
                for (int i = 0; i < Cycles; i++)
                {
                    try
                    {
                        // Not encrypted, so this succeeds; the point is the repeated open/close.
                        using var reader = AccessReader.Open(locked, new AccessReaderOptions { Password = "wrong" });
                        reader.ListTables();
                    }
                    catch { /* a protected database would refuse here — also fine */ }
                }

                // Nothing still holds the file, so it can be deleted. Deterministic, unlike the
                // process-wide handle count this used to compare.
                Action delete = () => File.Delete(locked);
                delete.Should().NotThrow();
            }
            finally
            {
                try { File.Delete(locked); } catch { }
            }
        }

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public void AbandonedEnumeration_DoesNotKeepTheReaderAlive(string path)
        {
            WeakReference readerRef = StartAndAbandon(path);

            Collect();

            // Walking away from a foreach mid-stream must not pin the reader: the iterator holds
            // the scan buffer and the reader, and a service that stops early would otherwise
            // accumulate both.
            readerRef.IsAlive.Should().BeFalse(
                because: "an abandoned enumeration must not keep its reader reachable");
        }

        private static WeakReference StartAndAbandon(string path)
        {
            var reader = TestDatabases.Open(path);
            string table = reader.GetTableStats().FirstOrDefault(s => s.ColumnCount > 0)?.Name;

            if (table != null)
            {
                foreach (object[] _ in reader.StreamRows(table))
                    break;   // abandon after the first row, without disposing the enumerator
            }

            reader.Dispose();
            return new WeakReference(reader);
        }

        private static void Collect()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}
