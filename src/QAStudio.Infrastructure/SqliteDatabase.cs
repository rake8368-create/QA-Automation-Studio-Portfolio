using System.Runtime.InteropServices;
using System.Text;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("QAStudio.Tests")]
namespace QAStudio.Infrastructure;

// WHAT: Minimal owned SQLite connection to the Windows 10/11 system SQLite library.
// WHY: Offline native storage without a Python subprocess, server or downloaded NuGet/native binary.
// CONTRACT: UTF-8 bound parameters, deterministic statement lifetime, foreign keys and busy timeout per connection.
// FAILURE: Native SQLite errors become exceptions; statements finalize and callers roll back transactions.
// TEST: Korean/newline/quote roundtrip, direct constraint/trigger tests, reopen and rollback tests.
internal sealed class SqliteDatabase : IDisposable
{
    private IntPtr handle;
    internal SqliteDatabase(string path, bool readOnly = false)
    {
        const int readOnlyFlag = 1, readWriteCreateFlags = 2 | 4, fullMutexFlag = 0x10000;
        int rc = Native.sqlite3_open_v2(path, out handle, (readOnly ? readOnlyFlag : readWriteCreateFlags) | fullMutexFlag, IntPtr.Zero);
        if (rc != 0) { string message = Error(); Dispose(); throw new InvalidDataException(message); }
        Native.sqlite3_busy_timeout(handle, 5000);
        Execute("PRAGMA foreign_keys=ON");
    }
    internal List<string?[]> Query(string sql, params string?[] values)
    {
        Check(Native.sqlite3_prepare_v2(handle, sql, -1, out var statement, IntPtr.Zero));
        try
        {
            for (int i = 0; i < values.Length; i++)
                Check(values[i] is { } text ? Native.sqlite3_bind_text(statement, i + 1, text, Encoding.UTF8.GetByteCount(text), new IntPtr(-1)) : Native.sqlite3_bind_null(statement, i + 1));
            List<string?[]> rows = [];
            int rc;
            while ((rc = Native.sqlite3_step(statement)) == 100)
            {
                var row = new string?[Native.sqlite3_column_count(statement)];
                for (int i = 0; i < row.Length; i++)
                {
                    if (Native.sqlite3_column_type(statement, i) == 5) continue;
                    row[i] = Marshal.PtrToStringUTF8(Native.sqlite3_column_text(statement, i), Native.sqlite3_column_bytes(statement, i));
                }
                rows.Add(row);
            }
            if (rc != 101) Check(rc);
            return rows;
        }
        finally { Native.sqlite3_finalize(statement); }
    }
    internal void Execute(string sql, params string?[] values) => Query(sql, values);
    // WHAT: SQLite online backup includes committed WAL content without file-copy races.
    // FAILURE: A failed backup aborts import; an existing backup is never overwritten.
    internal void BackupTo(string path)
    {
        if (File.Exists(path)) throw new IOException("Backup already exists");
        using var destination = new SqliteDatabase(path);
        var backup = Native.sqlite3_backup_init(destination.handle, "main", handle, "main");
        if (backup == IntPtr.Zero) throw new InvalidDataException("SQLite backup initialization failed");
        int rc;
        try { rc = Native.sqlite3_backup_step(backup, -1); }
        finally { Native.sqlite3_backup_finish(backup); }
        if (rc != 101) throw new InvalidDataException($"SQLite backup failed: {rc}");
        if (destination.Query("PRAGMA integrity_check")[0][0] != "ok") throw new InvalidDataException("Backup integrity check failed");
    }
    private string Error() => Marshal.PtrToStringUTF8(Native.sqlite3_errmsg(handle)) ?? "SQLite error";
    private void Check(int result) { if (result != 0) throw new InvalidDataException($"SQLite {result}: {Error()}"); }
    public void Dispose() { if (handle != IntPtr.Zero) { Native.sqlite3_close_v2(handle); handle = IntPtr.Zero; } }

    private static class Native
    {
        private const string Library = "winsqlite3.dll";
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_backup_init(IntPtr destination, [MarshalAs(UnmanagedType.LPUTF8Str)] string destinationName, IntPtr source, [MarshalAs(UnmanagedType.LPUTF8Str)] string sourceName);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_backup_step(IntPtr backup, int pages);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_backup_finish(IntPtr backup);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_close_v2(IntPtr db);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_errmsg(IntPtr db);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out IntPtr statement, IntPtr tail);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_text(IntPtr statement, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, int bytes, IntPtr destructor);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_null(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_step(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_finalize(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_column_count(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_column_type(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_column_text(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_column_bytes(IntPtr statement, int index);
    }
}
