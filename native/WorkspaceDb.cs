using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AIPhotoStudio
{
    // One connection per workspace. StudioWorkspace serializes use of this connection.
    internal sealed class WorkspaceDb : IDisposable
    {
        private const int Row = 100;
        private const int Done = 101;
        private IntPtr connection;
        private static readonly IntPtr Transient = new IntPtr(-1);

        internal WorkspaceDb(string file, bool readOnly)
        {
            // READONLY or READWRITE | CREATE. UTF-8 allows Unicode Windows paths.
            string ioPath=WorkspacePaths.ForIO(file);
            byte[] vfs=null;
            if(ioPath.Length>=248)
            {
                byte[] longPath=Utf8("win32-longpath");
                if(Native.sqlite3_vfs_find(longPath)!=IntPtr.Zero) vfs=longPath;
            }
            int result = Native.sqlite3_open_v2(Utf8(ioPath), out connection, readOnly ? 1 : 6, vfs);
            try
            {
                Check(result);
                Check(Native.sqlite3_busy_timeout(connection, 5000));
                Run("PRAGMA foreign_keys=ON");
                if (!readOnly) Run("PRAGMA synchronous=FULL");
            }
            catch { Dispose(); throw; }
        }

        private static byte[] Utf8(string value)
        {
            return Encoding.UTF8.GetBytes(value + "\0");
        }

        private void Check(int result)
        {
            if (result == 0) return;
            string message = connection == IntPtr.Zero ? "无法打开数据库" : Marshal.PtrToStringUni(Native.sqlite3_errmsg16(connection));
            throw new InvalidOperationException("SQLite (" + result + ") " + message);
        }

        private IntPtr Prepare(string sql, object[] values)
        {
            if (connection == IntPtr.Zero) throw new ObjectDisposedException("StudioWorkspace");
            IntPtr statement;
            Check(Native.sqlite3_prepare16_v2(connection, sql, -1, out statement, IntPtr.Zero));
            try
            {
                if (Native.sqlite3_bind_parameter_count(statement) != values.Length)
                    throw new ArgumentException("数据库语句的参数数量不一致。");
                for (int i = 0; i < values.Length; i++)
                {
                    object value = values[i];
                    int slot = i + 1;
                    if (value == null) Check(Native.sqlite3_bind_null(statement, slot));
                    else if (value is string)
                    {
                        string s = (string)value;
                        Check(Native.sqlite3_bind_text16(statement, slot, s, checked(s.Length * 2), Transient));
                    }
                    else if (value is int || value is long)
                        Check(Native.sqlite3_bind_int64(statement, slot, Convert.ToInt64(value)));
                    else if (value is bool) Check(Native.sqlite3_bind_int64(statement, slot, (bool)value ? 1 : 0));
                    else throw new ArgumentException("不支持的数据库参数类型。");
                }
                return statement;
            }
            catch { Native.sqlite3_finalize(statement); throw; }
        }

        internal void Run(string sql, params object[] values)
        {
            IntPtr statement = Prepare(sql, values);
            try
            {
                int result = Native.sqlite3_step(statement);
                if (result != Done) Check(result);
            }
            finally { Native.sqlite3_finalize(statement); }
        }

        internal List<object[]> Rows(string sql, params object[] values)
        {
            IntPtr statement = Prepare(sql, values);
            try
            {
                List<object[]> rows = new List<object[]>();
                int columns = Native.sqlite3_column_count(statement);
                int result;
                while ((result = Native.sqlite3_step(statement)) == Row)
                {
                    object[] cells = new object[columns];
                    for (int i = 0; i < columns; i++)
                    {
                        switch (Native.sqlite3_column_type(statement, i))
                        {
                            case 1: cells[i] = Native.sqlite3_column_int64(statement, i); break;
                            case 2: cells[i] = Native.sqlite3_column_double(statement, i); break;
                            case 3:
                                IntPtr ptr = Native.sqlite3_column_text16(statement, i);
                                cells[i] = Marshal.PtrToStringUni(ptr, Native.sqlite3_column_bytes16(statement, i) / 2);
                                break;
                            case 5: break;
                            default: throw new InvalidDataException("图库中出现了无法读取的字段类型。");
                        }
                    }
                    rows.Add(cells);
                }
                if (result != Done) Check(result);
                return rows;
            }
            finally { Native.sqlite3_finalize(statement); }
        }

        internal object Scalar(string sql, params object[] values)
        {
            List<object[]> result = Rows(sql, values);
            return result.Count == 0 ? null : result[0][0];
        }

        internal T Transaction<T>(bool writing, Func<T> action)
        {
            Run(writing ? "BEGIN IMMEDIATE" : "BEGIN");
            try
            {
                T result = action();
                Run("COMMIT");
                return result;
            }
            catch
            {
                try { Run("ROLLBACK"); } catch (InvalidOperationException) { }
                throw;
            }
        }

        // The caller holds a RESERVED lock on the live database. This second, read-only
        // connection backs up the committed state, including pages still in a WAL.
        internal static void Backup(string from, string to)
        {
            using (WorkspaceDb source = new WorkspaceDb(from, true))
            using (WorkspaceDb destination = new WorkspaceDb(to, false))
            {
                IntPtr backup = Native.sqlite3_backup_init(destination.connection, Utf8("main"), source.connection, Utf8("main"));
                if (backup == IntPtr.Zero) destination.Check(Native.sqlite3_errcode(destination.connection));
                int result;
                try { result = Native.sqlite3_backup_step(backup, -1); }
                finally { destination.Check(Native.sqlite3_backup_finish(backup)); }
                if (result != Done) destination.Check(result);
                if (!String.Equals((string)destination.Scalar("PRAGMA quick_check"), "ok", StringComparison.Ordinal))
                    throw new InvalidDataException("升级前的备份未通过 SQLite 完整性检查。");
            }
        }

        public void Dispose()
        {
            if (connection == IntPtr.Zero) return;
            Native.sqlite3_close(connection);
            connection = IntPtr.Zero;
        }

        private static class Native
        {
            private const string Lib = "winsqlite3.dll";
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_open_v2(byte[] path, out IntPtr db, int flags, byte[] vfs);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_vfs_find(byte[] name);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_close(IntPtr db);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_busy_timeout(IntPtr db, int ms);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_errmsg16(IntPtr db);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_errcode(IntPtr db);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)] internal static extern int sqlite3_prepare16_v2(IntPtr db, string query, int bytes, out IntPtr statement, IntPtr tail);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_parameter_count(IntPtr s);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_null(IntPtr s, int i);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_int64(IntPtr s, int i, long value);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)] internal static extern int sqlite3_bind_text16(IntPtr s, int i, string value, int bytes, IntPtr destructor);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_step(IntPtr s);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_finalize(IntPtr s);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_column_count(IntPtr s);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_column_type(IntPtr s, int i);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern long sqlite3_column_int64(IntPtr s, int i);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern double sqlite3_column_double(IntPtr s, int i);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_column_text16(IntPtr s, int i);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_column_bytes16(IntPtr s, int i);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_backup_init(IntPtr dest, byte[] destName, IntPtr src, byte[] srcName);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_backup_step(IntPtr backup, int pages);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_backup_finish(IntPtr backup);
        }
    }
}
