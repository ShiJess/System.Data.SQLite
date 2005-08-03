﻿/********************************************************
 * ADO.NET 2.0 Data Provider for SQLite Version 3.X
 * Written by Robert Simpson (robert@blackcastlesoft.com)
 * 
 * Released to the public domain, use at your own risk!
 ********************************************************/

namespace System.Data.SQLite
{
  using System;
  using System.Runtime.InteropServices;
  using System.Collections.Generic;

  /// <summary>
  /// This class implements SQLiteBase completely, and is the guts of the code that interop's SQLite with .NET
  /// </summary>
  internal class SQLite3 : SQLiteBase
  {
    /// <summary>
    /// The opaque pointer returned to us by the sqlite provider
    /// </summary>
    protected int              _sql;
    /// <summary>
    /// The user-defined functions registered on this connection
    /// </summary>
    protected SQLiteFunction[] _functionsArray;

    internal SQLite3(DateTimeFormat fmt)
      : base(fmt)
    {
      _sql = 0;
      _functionsArray = null;
    }

    protected override void Dispose(bool bDisposing)
    {
      Close();
    }

    internal override void Close()
    {
      if (_sql != 0)
      {
        int n = UnsafeNativeMethods.sqlite3_close_interop(_sql);
        if (n > 0) throw new SQLiteException(n, SQLiteLastError());
        SQLiteFunction.UnbindFunctions(this, _functionsArray);
      }
      _sql = 0;
    }

    internal override void Cancel()
    {
      UnsafeNativeMethods.sqlite3_interrupt_interop(_sql);
    }

    internal override string Version
    {
      get
      {
        int len;
        return ToString(UnsafeNativeMethods.sqlite3_libversion_interop(out len), len);
      }
    }

    internal override int Changes
    {
      get
      {
        return UnsafeNativeMethods.sqlite3_changes_interop(_sql);
      }
    }

    internal override void Open(string strFilename)
    {
      if (_sql != 0) return;
      int n = UnsafeNativeMethods.sqlite3_open_interop(ToUTF8(strFilename), out _sql);
      if (n > 0) throw new SQLiteException(n, SQLiteLastError());

      _functionsArray = SQLiteFunction.BindFunctions(this);
    }

    internal override void SetTimeout(int nTimeoutMS)
    {
      int n = UnsafeNativeMethods.sqlite3_busy_timeout_interop(_sql, nTimeoutMS);
      if (n > 0) throw new SQLiteException(n, SQLiteLastError());
    }

    internal override void Execute(string strSql)
    {
      IntPtr p;
      string str = strSql;
      int len;

      int n = UnsafeNativeMethods.sqlite3_exec_interop(_sql, ToUTF8(strSql), 0, 0, out p, out len);
      if (p != IntPtr.Zero)
      {
        str = ToString(p, len);
        UnsafeNativeMethods.sqlite3_free_interop(p);
      }
      if (n > 0) throw new SQLiteException(n, str);
    }

    internal override bool Step(SQLiteStatement stmt)
    {
      int n;
      long dwtick = 0;
      Random rnd = null;

      while (true)
      {
        n = UnsafeNativeMethods.sqlite3_step_interop(stmt._sqlite_stmt);

        if (n == 100) return true;
        if (n == 101) return false;

        if (n > 0)
        {
          int r;
          // An error occurred, attempt to reset the statement.  If the reset worked because the
          // schema has changed, re-try the step again.  Otherwise throw the original error.

          r = Reset(stmt);

          if (r == 0)
            throw new SQLiteException(n, "in Step()");
          else if (r == 6 && stmt._command != null) // SQLITE_LOCKED
          {
            // Keep trying
            if (dwtick == 0) // First time we've encountered the lock
            {
              dwtick = DateTime.Now.Ticks + (stmt._command._commandTimeout * 10000000);
              rnd = new Random();
            }
            // If we've exceeded the command's timeout, give up and throw an error
            if (DateTime.Now.Ticks - dwtick > 0)
            {
              throw new SQLiteException(r, "in Step() - timeout");
            }
            else
            {
              // Otherwise sleep for a random amount of time up to 250ms
              UnsafeNativeMethods.Sleep((uint)rnd.Next(1, 250));
            }
          }

        }
      }
    }

    internal override void Finalize(SQLiteStatement stmt)
    {
      if (stmt._sqlite_stmt > 0)
      {
        int n = UnsafeNativeMethods.sqlite3_finalize_interop(stmt._sqlite_stmt);
        if (n > 0) throw new SQLiteException(n, "in Finalize()");
      }
      stmt._sqlite_stmt = 0;
    }

    internal override int Reset(SQLiteStatement stmt)
    {
      int n;

      n = UnsafeNativeMethods.sqlite3_reset_interop(stmt._sqlite_stmt);

      // If the schema changed, try and re-prepare it
      if (n == 17) // SQLITE_SCHEMA
      {
        // Recreate a dummy statement
        int nc = 0;
        string str;
        using (SQLiteStatement tmp = Prepare(stmt._sqlStatement, ref nc, out str))
        {
          // Finalize the existing statement
          Finalize(stmt);

          // Reassign a new statement pointer to the old statement and clear the temporary one
          stmt._sqlite_stmt = tmp._sqlite_stmt;
          tmp._sqlite_stmt = 0;

          // Reapply parameters
          stmt.BindParameters();
        }
        return -1; // Reset was OK, with schema change
      }
      else if (n == 6) // SQLITE_LOCKED
        return n;

      if (n > 0)
        throw new SQLiteException(n, "in Reset()");

      return 0; // We reset OK, no schema changes
    }

    internal override string SQLiteLastError()
    {
      int len;
      return ToString(UnsafeNativeMethods.sqlite3_errmsg_interop(_sql, out len), len);
    }

    internal override SQLiteStatement Prepare(string strSql, ref int nParamStart, out string strRemain)
    {
      int stmt;
      IntPtr ptr;
      int len;

      byte[] b = ToUTF8(strSql);

      int n = UnsafeNativeMethods.sqlite3_prepare_interop(_sql, b, b.Length - 1, out stmt, out ptr, out len);
      if (n > 0) throw new SQLiteException(n, "in Prepare()");

      strRemain = ToString(ptr, len);

      SQLiteStatement cmd = null;
      if (stmt > 0) cmd = new SQLiteStatement(this, stmt, strSql.Substring(0, strSql.Length - strRemain.Length), ref nParamStart);

      return cmd;
    }

    internal override void Bind_Double(SQLiteStatement stmt, int index, double value)
    {
      int n = UnsafeNativeMethods.sqlite3_bind_double_interop(stmt._sqlite_stmt, index, ref value);
      if (n > 0) throw new SQLiteException(n, SQLiteLastError());
    }

    internal override void Bind_Int32(SQLiteStatement stmt, int index, int value)
    {
      int n = UnsafeNativeMethods.sqlite3_bind_int_interop(stmt._sqlite_stmt, index, value);
      if (n > 0) throw new SQLiteException(n, SQLiteLastError());
    }

    internal override void Bind_Int64(SQLiteStatement stmt, int index, long value)
    {
      int n = UnsafeNativeMethods.sqlite3_bind_int64_interop(stmt._sqlite_stmt, index, ref value);
      if (n > 0) throw new SQLiteException(n, SQLiteLastError());
    }

    internal override void Bind_Text(SQLiteStatement stmt, int index, string value)
    {
      byte[] b = ToUTF8(value);
      int n = UnsafeNativeMethods.sqlite3_bind_text_interop(stmt._sqlite_stmt, index, b, b.Length - 1, -1);
      if (n > 0) throw new SQLiteException(n, SQLiteLastError());
    }

    internal override void Bind_DateTime(SQLiteStatement stmt, int index, DateTime dt)
    {
      byte[] b = ToUTF8(dt);
      int n = UnsafeNativeMethods.sqlite3_bind_text_interop(stmt._sqlite_stmt, index, b, b.Length - 1, -1);
      if (n > 0) throw new SQLiteException(n, SQLiteLastError());
    }

    internal override void Bind_Blob(SQLiteStatement stmt, int index, byte[] blobData)
    {
      int n = UnsafeNativeMethods.sqlite3_bind_blob_interop(stmt._sqlite_stmt, index, blobData, blobData.Length, -1);
      if (n > 0) throw new SQLiteException(n, SQLiteLastError());
    }

    internal override void Bind_Null(SQLiteStatement stmt, int index)
    {
      int n = UnsafeNativeMethods.sqlite3_bind_null_interop(stmt._sqlite_stmt, index);
      if (n > 0) throw new SQLiteException(n, SQLiteLastError());
    }

    internal override int Bind_ParamCount(SQLiteStatement stmt)
    {
      return UnsafeNativeMethods.sqlite3_bind_parameter_count_interop(stmt._sqlite_stmt);
    }

    internal override string Bind_ParamName(SQLiteStatement stmt, int index)
    {
      int len;
      return ToString(UnsafeNativeMethods.sqlite3_bind_parameter_name_interop(stmt._sqlite_stmt, index, out len), len);
    }

    internal override int Bind_ParamIndex(SQLiteStatement stmt, string paramName)
    {
      return UnsafeNativeMethods.sqlite3_bind_parameter_index_interop(stmt._sqlite_stmt, ToUTF8(paramName));
    }

    internal override int ColumnCount(SQLiteStatement stmt)
    {
      return UnsafeNativeMethods.sqlite3_column_count_interop(stmt._sqlite_stmt);
    }

    internal override string ColumnName(SQLiteStatement stmt, int index)
    {
      int len;
      return ToString(UnsafeNativeMethods.sqlite3_column_name_interop(stmt._sqlite_stmt, index, out len), len);
    }

    internal override string ColumnType(SQLiteStatement stmt, int index, out TypeAffinity nAffinity)
    {
      nAffinity = TypeAffinity.None;

      int len;
      IntPtr p = UnsafeNativeMethods.sqlite3_column_decltype_interop(stmt._sqlite_stmt, index, out len);
      if (p != IntPtr.Zero) return ToString(p, len);
      else
      {
        nAffinity = UnsafeNativeMethods.sqlite3_column_type_interop(stmt._sqlite_stmt, index);
        switch (nAffinity)
        {
          case TypeAffinity.Int64:
            return "BIGINT";
          case TypeAffinity.Double:
            return "DOUBLE";
          case TypeAffinity.Blob:
            return "BLOB";
          default:
            return "TEXT";
        }
      }
    }

    internal override int ColumnIndex(SQLiteStatement stmt, string columnName)
    {
      int x = ColumnCount(stmt);

      for (int n = 0; n < x; n++)
      {
        if (String.Compare(columnName, ColumnName(stmt, n), true) == 0) return n;
      }
      return -1;
    }

    internal override double GetDouble(SQLiteStatement stmt, int index)
    {
      double value;
      UnsafeNativeMethods.sqlite3_column_double_interop(stmt._sqlite_stmt, index, out value);
      return value;
    }

    internal override int GetInt32(SQLiteStatement stmt, int index)
    {
      return UnsafeNativeMethods.sqlite3_column_int_interop(stmt._sqlite_stmt, index);
    }

    internal override long GetInt64(SQLiteStatement stmt, int index)
    {
      long value;
      UnsafeNativeMethods.sqlite3_column_int64_interop(stmt._sqlite_stmt, index, out value);
      return value;
    }

    internal override string GetText(SQLiteStatement stmt, int index)
    {
      int len;
      return ToString(UnsafeNativeMethods.sqlite3_column_text_interop(stmt._sqlite_stmt, index, out len), len);
    }

    internal override DateTime GetDateTime(SQLiteStatement stmt, int index)
    {
      int len;
      return ToDateTime(UnsafeNativeMethods.sqlite3_column_text_interop(stmt._sqlite_stmt, index, out len), len);
    }

    internal override long GetBytes(SQLiteStatement stmt, int index, int nDataOffset, byte[] bDest, int nStart, int nLength)
    {
      IntPtr ptr;
      int nlen;
      int nCopied = nLength;

      nlen = UnsafeNativeMethods.sqlite3_column_bytes_interop(stmt._sqlite_stmt, index);
      ptr = UnsafeNativeMethods.sqlite3_column_blob_interop(stmt._sqlite_stmt, index);

      if (bDest == null) return nlen;

      if (nCopied + nStart > bDest.Length) nCopied = bDest.Length - nStart;
      if (nCopied + nDataOffset > nlen) nCopied = nlen - nDataOffset;

      if (nCopied > 0)
        Marshal.Copy((IntPtr)(ptr.ToInt32() + nDataOffset), bDest, nStart, nCopied);
      else nCopied = 0;

      return nCopied;
    }

    internal override long GetChars(SQLiteStatement stmt, int index, int nDataOffset, char[] bDest, int nStart, int nLength)
    {
      int nlen;
      int nCopied = nLength;

      string str = GetText(stmt, index);
      nlen = str.Length;

      if (bDest == null) return nlen;

      if (nCopied + nStart > bDest.Length) nCopied = bDest.Length - nStart;
      if (nCopied + nDataOffset > nlen) nCopied = nlen - nDataOffset;

      if (nCopied > 0)
        str.CopyTo(nDataOffset, bDest, nStart, nCopied);
      else nCopied = 0;

      return nCopied;
    }

    internal override bool IsNull(SQLiteStatement stmt, int index)
    {
      return (UnsafeNativeMethods.sqlite3_column_type_interop(stmt._sqlite_stmt, index) == TypeAffinity.Null);
    }

    internal override int AggregateCount(int context)
    {
      return UnsafeNativeMethods.sqlite3_aggregate_count_interop(context);
    }

    internal override int CreateFunction(string strFunction, int nArgs, SQLiteCallback func, SQLiteCallback funcstep, SQLiteCallback funcfinal)
    {
      int nCookie;

      int n = UnsafeNativeMethods.sqlite3_create_function_interop(_sql, ToUTF8(strFunction), nArgs, 1, func, funcstep, funcfinal, out nCookie);
      if (n > 0) throw new SQLiteException(n, SQLiteLastError());

      return nCookie;
    }

    internal override int CreateCollation(string strCollation, SQLiteCollation func)
    {
      int nCookie;

      int n = UnsafeNativeMethods.sqlite3_create_collation_interop(_sql, ToUTF8(strCollation), 1, 0, func, out nCookie);
      if (n > 0) throw new SQLiteException(n, SQLiteLastError());

      return nCookie;
    }

    internal override void FreeFunction(int nCookie)
    {
      UnsafeNativeMethods.sqlite3_function_free_callbackcookie(nCookie);
    }

    internal override long GetParamValueBytes(int p, int nDataOffset, byte[] bDest, int nStart, int nLength)
    {
      IntPtr ptr;
      int nlen;
      int nCopied = nLength;

      nlen = UnsafeNativeMethods.sqlite3_value_bytes_interop(p);
      ptr = UnsafeNativeMethods.sqlite3_value_blob_interop(p);

      if (bDest == null) return nlen;

      if (nCopied + nStart > bDest.Length) nCopied = bDest.Length - nStart;
      if (nCopied + nDataOffset > nlen) nCopied = nlen - nDataOffset;

      if (nCopied > 0)
        Marshal.Copy((IntPtr)(ptr.ToInt32() + nDataOffset), bDest, nStart, nCopied);
      else nCopied = 0;

      return nCopied;
    }

    internal override double GetParamValueDouble(int ptr)
    {
      double value;
      UnsafeNativeMethods.sqlite3_value_double_interop(ptr, out value);
      return value;
    }

    internal override int GetParamValueInt32(int ptr)
    {
      return UnsafeNativeMethods.sqlite3_value_int_interop(ptr);
    }

    internal override long GetParamValueInt64(int ptr)
    {
      Int64 value;
      UnsafeNativeMethods.sqlite3_value_int64_interop(ptr, out value);
      return value;
    }

    internal override string GetParamValueText(int ptr)
    {
      int len;
      return ToString(UnsafeNativeMethods.sqlite3_value_text_interop(ptr, out len), len);
    }

    internal override TypeAffinity GetParamValueType(int ptr)
    {
      return UnsafeNativeMethods.sqlite3_value_type_interop(ptr);
    }

    internal override void ReturnBlob(int context, byte[] value)
    {
      UnsafeNativeMethods.sqlite3_result_blob_interop(context, value, value.Length, -1);
    }

    internal override void ReturnDouble(int context, double value)
    {
      UnsafeNativeMethods.sqlite3_result_double_interop(context, ref value);
    }

    internal override void ReturnError(int context, string value)
    {
      UnsafeNativeMethods.sqlite3_result_error_interop(context, ToUTF8(value), value.Length);
    }

    internal override void ReturnInt32(int context, int value)
    {
      UnsafeNativeMethods.sqlite3_result_int_interop(context, value);
    }

    internal override void ReturnInt64(int context, long value)
    {
      UnsafeNativeMethods.sqlite3_result_int64_interop(context, ref value);
    }

    internal override void ReturnNull(int context)
    {
      UnsafeNativeMethods.sqlite3_result_null_interop(context);
    }

    internal override void ReturnText(int context, string value)
    {
      UnsafeNativeMethods.sqlite3_result_text_interop(context, ToUTF8(value), value.Length, -1);
    }

    internal override int AggregateContext(int context)
    {
      return UnsafeNativeMethods.sqlite3_aggregate_context_interop(context, 1);
    }

    internal override void SetRealColNames(bool bOn)
    {
      UnsafeNativeMethods.sqlite3_realcolnames(_sql, Convert.ToInt32(bOn));
    }
  }
}
