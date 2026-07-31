using System.Runtime.InteropServices;
using System.Text;

namespace AppleMusicOverlay.Services;

public interface IReadOnlySqliteTextQuery
{
    IReadOnlyList<string?[]> Query(string databasePath, string sql);
}

public sealed partial class WindowsSqliteTextQuery : IReadOnlySqliteTextQuery
{
    private const int SqliteOk = 0;
    private const int SqliteRow = 100;
    private const int SqliteDone = 101;
    private const int SqliteOpenReadOnly = 0x00000001;
    private const int BusyTimeoutMilliseconds = 250;

    public IReadOnlyList<string?[]> Query(string databasePath, string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The Windows SQLite API is only available on Windows.");
        }

        int openResult = NativeMethods.sqlite3_open_v2(
            databasePath,
            out IntPtr database,
            SqliteOpenReadOnly,
            null);
        if (openResult != SqliteOk)
        {
            string message = ReadError(database, openResult);
            if (database != IntPtr.Zero)
            {
                NativeMethods.sqlite3_close_v2(database);
            }

            throw new InvalidOperationException(message);
        }

        try
        {
            NativeMethods.sqlite3_busy_timeout(database, BusyTimeoutMilliseconds);
            int prepareResult = NativeMethods.sqlite3_prepare_v2(
                database,
                sql,
                -1,
                out IntPtr statement,
                IntPtr.Zero);
            if (prepareResult != SqliteOk)
            {
                throw new InvalidOperationException(ReadError(database, prepareResult));
            }

            try
            {
                int columnCount = NativeMethods.sqlite3_column_count(statement);
                var rows = new List<string?[]>();
                while (true)
                {
                    int stepResult = NativeMethods.sqlite3_step(statement);
                    if (stepResult == SqliteDone)
                    {
                        return rows;
                    }

                    if (stepResult != SqliteRow)
                    {
                        throw new InvalidOperationException(ReadError(database, stepResult));
                    }

                    var row = new string?[columnCount];
                    for (int column = 0; column < columnCount; column++)
                    {
                        row[column] = ReadColumnText(statement, column);
                    }

                    rows.Add(row);
                }
            }
            finally
            {
                NativeMethods.sqlite3_finalize(statement);
            }
        }
        finally
        {
            NativeMethods.sqlite3_close_v2(database);
        }
    }

    private static string? ReadColumnText(IntPtr statement, int column)
    {
        IntPtr text = NativeMethods.sqlite3_column_text(statement, column);
        if (text == IntPtr.Zero)
        {
            return null;
        }

        int byteCount = NativeMethods.sqlite3_column_bytes(statement, column);
        if (byteCount <= 0)
        {
            return string.Empty;
        }

        byte[] bytes = new byte[byteCount];
        Marshal.Copy(text, bytes, 0, byteCount);
        return Encoding.UTF8.GetString(bytes);
    }

    private static string ReadError(IntPtr database, int resultCode)
    {
        string? detail = database == IntPtr.Zero
            ? null
            : Marshal.PtrToStringUTF8(NativeMethods.sqlite3_errmsg(database));
        return string.IsNullOrWhiteSpace(detail)
            ? $"SQLite operation failed with result code {resultCode}."
            : $"SQLite operation failed with result code {resultCode}: {detail}";
    }

    private static partial class NativeMethods
    {
        [LibraryImport("winsqlite3.dll", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int sqlite3_open_v2(
            string filename,
            out IntPtr database,
            int flags,
            string? virtualFileSystem);

        [LibraryImport("winsqlite3.dll", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int sqlite3_prepare_v2(
            IntPtr database,
            string sql,
            int byteCount,
            out IntPtr statement,
            IntPtr tail);

        [LibraryImport("winsqlite3.dll")]
        internal static partial int sqlite3_step(IntPtr statement);

        [LibraryImport("winsqlite3.dll")]
        internal static partial int sqlite3_busy_timeout(IntPtr database, int milliseconds);

        [LibraryImport("winsqlite3.dll")]
        internal static partial int sqlite3_column_count(IntPtr statement);

        [LibraryImport("winsqlite3.dll")]
        internal static partial IntPtr sqlite3_column_text(IntPtr statement, int column);

        [LibraryImport("winsqlite3.dll")]
        internal static partial int sqlite3_column_bytes(IntPtr statement, int column);

        [LibraryImport("winsqlite3.dll")]
        internal static partial IntPtr sqlite3_errmsg(IntPtr database);

        [LibraryImport("winsqlite3.dll")]
        internal static partial int sqlite3_finalize(IntPtr statement);

        [LibraryImport("winsqlite3.dll")]
        internal static partial int sqlite3_close_v2(IntPtr database);
    }
}
