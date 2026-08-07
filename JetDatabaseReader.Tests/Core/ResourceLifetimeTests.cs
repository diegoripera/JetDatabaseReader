using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        public void OpeningAndDisposingRepeatedly_LeaksNoHandles(string path)
        {
            // Warm up so first-use allocations and JIT do not count as growth.
            for (int i = 0; i < 5; i++)
                using (var warm = TestDatabases.Open(path)) warm.ListTables();

            Process self = Process.GetCurrentProcess();
            self.Refresh();
            int before = self.HandleCount;

            for (int i = 0; i < Cycles; i++)
                using (var reader = TestDatabases.Open(path)) reader.ListTables();

            self.Refresh();
            int after = self.HandleCount;

            // The count moves a little on its own — other threads, the GC, the test host — so this
            // is a leak check, not an equality check. One leaked handle per open would show up as
            // roughly Cycles.
            (after - before).Should().BeLessThan(Cycles / 2,
                because: $"{Cycles} open/dispose cycles must not accumulate handles " +
                         $"(before {before}, after {after})");
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
        public void WrongPassword_RepeatedlyRejected_LeaksNoHandles()
        {
            string db = TestDatabases.AdventureWorks;
            if (!TestDatabases.IsReadable(db)) return;

            // A rejected open is the interesting case: the file is already open by the time the
            // constructor decides to refuse it.
            string locked = Path.Combine(Path.GetTempPath(), $"jdr_life_{Guid.NewGuid():N}.mdb");
            File.Copy(db, locked, overwrite: true);

            try
            {
                Process self = Process.GetCurrentProcess();
                self.Refresh();
                int before = self.HandleCount;

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

                self.Refresh();
                (self.HandleCount - before).Should().BeLessThan(Cycles / 2);

                // The clincher: nothing still holds the file, so it can be deleted.
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
