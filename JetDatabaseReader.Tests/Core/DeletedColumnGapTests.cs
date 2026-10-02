using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace JetDatabaseReader.Tests
{
    public class DeletedColumnGapTests
    {
        // A Jet4 row written before Boolean column 1 was deleted. The current schema retains
        // columns 0 and 2; its null mask and fixed offsets still use the original slots.
        private static readonly byte[] OldRow =
        {
            3, 0,                       // three columns when this row was written
            11, 0, 0, 0,                // column 0 (Long)
            22, 0, 0, 0,                // surviving column 2 (Long)
            0b00000101                 // columns 0 and 2 have values
        };

        [Fact]
        public void DeletedColumnGap_IsRejectedByDefault()
        {
            if (!File.Exists(TestDatabases.Jet4NoPassword)) return;

            new AccessReaderOptions().AllowDeletedColumnGaps.Should().BeFalse();
            using var reader = TestDatabases.Open(TestDatabases.Jet4NoPassword);

            Action read = () => Decode(reader, OldRow);
            read.Should().Throw<JetLimitationException>();
        }

        [Fact]
        public void DeletedColumnGap_OptInReadsSurvivingColumnSlots()
        {
            if (!File.Exists(TestDatabases.Jet4NoPassword)) return;

            using var reader = TestDatabases.Open(TestDatabases.Jet4NoPassword,
                new AccessReaderOptions { AllowDeletedColumnGaps = true });

            Decode(reader, OldRow).Should().Equal(11, 22);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void CurrentRowWithDeletedColumnGap_RemainsReadable(bool allowDeletedColumnGaps)
        {
            if (!File.Exists(TestDatabases.Jet4NoPassword)) return;

            using var reader = TestDatabases.Open(TestDatabases.Jet4NoPassword,
                new AccessReaderOptions { AllowDeletedColumnGaps = allowDeletedColumnGaps });

            // A row written before surviving column 2 was added.
            Decode(reader, new byte[] { 2, 0, 11, 0, 0, 0, 22, 0, 0, 0, 0b00000001 })
                .Should().Equal(11, DBNull.Value);
        }

        [Fact]
        public void ExternalAlbauSamples_ReadPositionWithOptIn()
        {
            // Customer databases are private, so this extra integration check runs only when
            // the caller points it at a local corpus containing both named samples.
            string? directory = Environment.GetEnvironmentVariable("JETDATABASEREADER_ALBAU_DB_DIR");
            if (string.IsNullOrWhiteSpace(directory)) return;

            foreach (string name in new[] { "C1000124.mdb", "C1000300.MDB" })
            {
                string path = Path.Combine(directory, name);
                File.Exists(path).Should().BeTrue(because: $"the requested corpus should contain {name}");

                using var compatible = TestDatabases.Open(path,
                    new AccessReaderOptions { AllowDeletedColumnGaps = true });
                compatible.StreamRows("Position").Should().NotBeEmpty();
            }
        }

        private static object[] Decode(AccessReader reader, byte[] row)
        {
            var table = new TableDef
            {
                HasDeletedColumns = true,
                Columns = new List<ColumnInfo>
                {
                    new ColumnInfo { ColNum = 0, Type = 0x04, Flags = 0x01, FixedOff = 0 },
                    new ColumnInfo { ColNum = 2, Type = 0x04, Flags = 0x01, FixedOff = 4 }
                }
            };
            var shape = new RowShape { Table = table, Columns = table.Columns.ToArray(), Source = new[] { 0, 1 } };
            var values = new object[2];

            MethodInfo crackRow = typeof(AccessReader).GetMethod("CrackRow", BindingFlags.Instance | BindingFlags.NonPublic)!;
            try
            {
                bool decoded = (bool)crackRow.Invoke(reader, new object[] { row, 0, row.Length, shape, null!, values })!;
                decoded.Should().BeTrue();
                return values;
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }
}
