/********************************************************
 * ADO.NET 2.0 Data Provider for SQLite Version 3.X
 * Written by Robert Simpson (robert@blackcastlesoft.com)
 * 
 * Released to the public domain, use at your own risk!
 ********************************************************/

namespace System.Data.SQLite
{
  using System;
  using System.Collections.Generic;

#if !NET_COMPACT_20 && (TRACE_CONNECTION || TRACE_STATEMENT)
  using System.Diagnostics;
#endif

  using System.Globalization;
  using System.Runtime.InteropServices;
  using System.Text;

  /// <summary>
  /// This is the method signature for the SQLite core library logging callback
  /// function for use with sqlite3_log() and the SQLITE_CONFIG_LOG.
  ///
  /// WARNING: This delegate is used more-or-less directly by native code, do
  ///          not modify its type signature.
  /// </summary>
  /// <param name="pUserData">
  /// The extra data associated with this message, if any.
  /// </param>
  /// <param name="errorCode">
  /// The error code associated with this message.
  /// </param>
  /// <param name="pMessage">
  /// The message string to be logged.
  /// </param>
#if !PLATFORM_COMPACTFRAMEWORK
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
  internal delegate void SQLiteLogCallback(IntPtr pUserData, int errorCode, IntPtr pMessage);

  /// <summary>
  /// This class implements SQLiteBase completely, and is the guts of the code that interop's SQLite with .NET
  /// </summary>
  internal class SQLite3 : SQLiteBase
  {
    private static object syncRoot = new object();

    //
    // NOTE: This is the public key for the System.Data.SQLite assembly.  If you change the
    //       SNK file, you will need to change this as well.
    //
    internal const string PublicKey =
        "002400000480000094000000060200000024000052534131000400000100010005a288de5687c4e1" +
        "b621ddff5d844727418956997f475eb829429e411aff3e93f97b70de698b972640925bdd44280df0" +
        "a25a843266973704137cbb0e7441c1fe7cae4e2440ae91ab8cde3933febcb1ac48dd33b40e13c421" +
        "d8215c18a4349a436dd499e3c385cc683015f886f6c10bd90115eb2bd61b67750839e3a19941dc9c";

#if !PLATFORM_COMPACTFRAMEWORK
    internal const string DesignerVersion = "1.0.95.0";
#endif

    /// <summary>
    /// The opaque pointer returned to us by the sqlite provider
    /// </summary>
    protected internal SQLiteConnectionHandle _sql;
    protected string _fileName;
    protected bool _usePool;
    protected int _poolVersion;

#if (NET_35 || NET_40 || NET_45 || NET_451) && !PLATFORM_COMPACTFRAMEWORK
    private bool _buildingSchema;
#endif

    /// <summary>
    /// The user-defined functions registered on this connection
    /// </summary>
    protected List<SQLiteFunction> _functions;

#if INTEROP_VIRTUAL_TABLE
    /// <summary>
    /// The modules created using this connection.
    /// </summary>
    protected Dictionary<string, SQLiteModule> _modules;
#endif

    ///////////////////////////////////////////////////////////////////////////////////////////////

    /// <summary>
    /// Constructs the object used to interact with the SQLite core library
    /// using the UTF-8 text encoding.
    /// </summary>
    /// <param name="fmt">
    /// The DateTime format to be used when converting string values to a
    /// DateTime and binding DateTime parameters.
    /// </param>
    /// <param name="kind">
    /// The <see cref="DateTimeKind" /> to be used when creating DateTime
    /// values.
    /// </param>
    /// <param name="fmtString">
    /// The format string to be used when parsing and formatting DateTime
    /// values.
    /// </param>
    /// <param name="db">
    /// The native handle to be associated with the database connection.
    /// </param>
    /// <param name="fileName">
    /// The fully qualified file name associated with <paramref name="db "/>.
    /// </param>
    /// <param name="ownHandle">
    /// Non-zero if the newly created object instance will need to dispose
    /// of <paramref name="db" /> when it is no longer needed.
    /// </param>
    internal SQLite3(
        SQLiteDateFormats fmt,
        DateTimeKind kind,
        string fmtString,
        IntPtr db,
        string fileName,
        bool ownHandle
        )
      : base(fmt, kind, fmtString)
    {
        if (db != IntPtr.Zero)
        {
            _sql = new SQLiteConnectionHandle(db, ownHandle);
            _fileName = fileName;

            SQLiteConnection.OnChanged(null, new ConnectionEventArgs(
                SQLiteConnectionEventType.NewCriticalHandle, null, null,
                null, null, _sql, fileName, new object[] { fmt, kind,
                fmtString, db, fileName, ownHandle }));
        }
    }

    ///////////////////////////////////////////////////////////////////////////////////////////////

    #region IDisposable "Pattern" Members
    private bool disposed;
    private void CheckDisposed() /* throw */
    {
#if THROW_ON_DISPOSED
        if (disposed)
            throw new ObjectDisposedException(typeof(SQLite3).Name);
#endif
    }

    ///////////////////////////////////////////////////////////////////////////////////////////////

    protected override void Dispose(bool disposing)
    {
        try
        {
            if (!disposed)
            {
                //if (disposing)
                //{
                //    ////////////////////////////////////
                //    // dispose managed resources here...
                //    ////////////////////////////////////
                //}

                //////////////////////////////////////
                // release unmanaged resources here...
                //////////////////////////////////////

#if INTEROP_VIRTUAL_TABLE
                DisposeModules();
#endif

                Close(false); /* Disposing, cannot throw. */
            }
        }
        finally
        {
            base.Dispose(disposing);

            //
            // NOTE: Everything should be fully disposed at this point.
            //
            disposed = true;
        }
    }
    #endregion

    ///////////////////////////////////////////////////////////////////////////////////////////////

#if INTEROP_VIRTUAL_TABLE
    /// <summary>
    /// This method attempts to dispose of all the <see cref="SQLiteModule" /> derived
    /// object instances currently associated with the native database connection.
    /// </summary>
    private void DisposeModules()
    {
        //
        // NOTE: If any modules were created, attempt to dispose of
        //       them now.  This code is designed to avoid throwing
        //       exceptions unless the Dispose method of the module
        //       itself throws an exception.
        //
        if (_modules != null)
        {
            foreach (KeyValuePair<string, SQLiteModule> pair in _modules)
            {
                SQLiteModule module = pair.Value;

                if (module == null)
                    continue;

                module.Dispose();
            }

            _modules.Clear();
        }
    }
#endif

    ///////////////////////////////////////////////////////////////////////////////////////////////

    // It isn't necessary to cleanup any functions we've registered.  If the connection
    // goes to the pool and is resurrected later, re-registered functions will overwrite the
    // previous functions.  The SQLiteFunctionCookieHandle will take care of freeing unmanaged
    // resources belonging to the previously-registered functions.
    internal override void Close(bool canThrow)
    {
      if (_sql != null)
      {
          if (!_sql.OwnHandle)
          {
              _sql = null;
              return;
          }

          if (_usePool)
          {
              if (SQLiteBase.ResetConnection(_sql, _sql, canThrow))
              {
#if INTEROP_VIRTUAL_TABLE
                  DisposeModules();
#endif

                  SQLiteConnectionPool.Add(_fileName, _sql, _poolVersion);

#if !NET_COMPACT_20 && TRACE_CONNECTION
                  Trace.WriteLine(String.Format("Close (Pool) Success: {0}", _sql));
#endif
              }
#if !NET_COMPACT_20 && TRACE_CONNECTION
              else
              {
                  Trace.WriteLine(String.Format("Close (Pool) Failure: {0}", _sql));
              }
#endif
          }
          else
          {
              _sql.Dispose();
          }
          _sql = null;
      }
    }

    ///////////////////////////////////////////////////////////////////////////////////////////////

    /// <summary>
    /// Attempts to interrupt the query currently executing on the associated
    /// native database connection.
    /// </summary>
    internal override void Cancel()
    {
      UnsafeNativeMethods.sqlite3_interrupt(_sql);
    }

    /// <summary>
    /// This function binds a user-defined function to the connection.
    /// </summary>
    /// <param name="functionAttribute">
    /// The <see cref="SQLiteFunctionAttribute"/> object instance containing
    /// the metadata for the function to be bound.
    /// </param>
    /// <param name="function">
    /// The <see cref="SQLiteFunction"/> object instance that implements the
    /// function to be bound.
    /// </param>
    /// <param name="flags">
    /// The flags associated with the parent connection object.
    /// </param>
    internal override void BindFunction(
        SQLiteFunctionAttribute functionAttribute,
        SQLiteFunction function,
        SQLiteConnectionFlags flags
        )
    {
        SQLiteFunction.BindFunction(this, functionAttribute, function, flags);

        if (_functions == null)
            _functions = new List<SQLiteFunction>();

        _functions.Add(function);
    }

    internal override string Version
    {
      get
      {
        return SQLiteVersion;
      }
    }

    internal override int VersionNumber
    {
      get
      {
        return SQLiteVersionNumber;
      }
    }

    internal static string DefineConstants
    {
        get
        {
            StringBuilder result = new StringBuilder();
            IList<string> list = SQLiteDefineConstants.OptionList;

            if (list != null)
            {
                foreach (string element in list)
                {
                    if (element == null)
                        continue;

                    if (result.Length > 0)
                        result.Append(' ');

                    result.Append(element);
                }
            }

            return result.ToString();
        }
    }

    internal static string SQLiteVersion
    {
      get
      {
        return UTF8ToString(UnsafeNativeMethods.sqlite3_libversion(), -1);
      }
    }

    internal static int SQLiteVersionNumber
    {
      get
      {
        return UnsafeNativeMethods.sqlite3_libversion_number();
      }
    }

    internal static string SQLiteSourceId
    {
      get
      {
        return UTF8ToString(UnsafeNativeMethods.sqlite3_sourceid(), -1);
      }
    }

    internal static string SQLiteCompileOptions
    {
        get
        {
            StringBuilder result = new StringBuilder();
            int index = 0;
            IntPtr zValue = UnsafeNativeMethods.sqlite3_compileoption_get(index++);

            while (zValue != IntPtr.Zero)
            {
                if (result.Length > 0)
                    result.Append(' ');

                result.Append(UTF8ToString(zValue, -1));
                zValue = UnsafeNativeMethods.sqlite3_compileoption_get(index++);
            }

            return result.ToString();
        }
    }

    internal static string InteropVersion
    {
        get
        {
#if !SQLITE_STANDARD
            return UTF8ToString(UnsafeNativeMethods.interop_libversion(), -1);
#else
            return null;
#endif
        }
    }

    internal static string InteropSourceId
    {
        get
        {
#if !SQLITE_STANDARD
            return UTF8ToString(UnsafeNativeMethods.interop_sourceid(), -1);
#else
            return null;
#endif
        }
    }

    internal static string InteropCompileOptions
    {
        get
        {
#if !SQLITE_STANDARD
            StringBuilder result = new StringBuilder();
            int index = 0;
            IntPtr zValue = UnsafeNativeMethods.interop_compileoption_get(index++);

            while (zValue != IntPtr.Zero)
            {
                if (result.Length > 0)
                    result.Append(' ');

                result.Append(UTF8ToString(zValue, -1));
                zValue = UnsafeNativeMethods.interop_compileoption_get(index++);
            }

            return result.ToString();
#else
            return null;
#endif
        }
    }

    internal override bool AutoCommit
    {
      get
      {
        return IsAutocommit(_sql, _sql);
      }
    }

    internal override long LastInsertRowId
    {
      get
      {
#if !PLATFORM_COMPACTFRAMEWORK
        return UnsafeNativeMethods.sqlite3_last_insert_rowid(_sql);
#elif !SQLITE_STANDARD
        long rowId = 0;
        UnsafeNativeMethods.sqlite3_last_insert_rowid_interop(_sql, ref rowId);
        return rowId;
#else
        throw new NotImplementedException();
#endif
      }
    }

    internal override int Changes
    {
      get
      {
#if !SQLITE_STANDARD
        return UnsafeNativeMethods.sqlite3_changes_interop(_sql);
#else
        return UnsafeNativeMethods.sqlite3_changes(_sql);
#endif
      }
    }

    internal override long MemoryUsed
    {
        get
        {
            return StaticMemoryUsed;
        }
    }

    internal static long StaticMemoryUsed
    {
        get
        {
#if !PLATFORM_COMPACTFRAMEWORK
            return UnsafeNativeMethods.sqlite3_memory_used();
#elif !SQLITE_STANDARD
            long bytes = 0;
            UnsafeNativeMethods.sqlite3_memory_used_interop(ref bytes);
            return bytes;
#else
            throw new NotImplementedException();
#endif
        }
    }

    internal override long MemoryHighwater
    {
        get
        {
            return StaticMemoryHighwater;
        }
    }

    internal static long StaticMemoryHighwater
    {
        get
        {
#if !PLATFORM_COMPACTFRAMEWORK
            return UnsafeNativeMethods.sqlite3_memory_highwater(0);
#elif !SQLITE_STANDARD
            long bytes = 0;
            UnsafeNativeMethods.sqlite3_memory_highwater_interop(0, ref bytes);
            return bytes;
#else
            throw new NotImplementedException();
#endif
        }
    }

    /// <summary>
    /// Returns non-zero if the underlying native connection handle is owned
    /// by this instance.
    /// </summary>
    internal override bool OwnHandle
    {
        get
        {
            if (_sql == null)
                throw new SQLiteException("no connection handle available");

            return _sql.OwnHandle;
        }
    }

    internal override SQLiteErrorCode SetMemoryStatus(bool value)
    {
        return StaticSetMemoryStatus(value);
    }

    internal static SQLiteErrorCode StaticSetMemoryStatus(bool value)
    {
        SQLiteErrorCode rc = UnsafeNativeMethods.sqlite3_config_int(
            SQLiteConfigOpsEnum.SQLITE_CONFIG_MEMSTATUS, value ? 1 : 0);

        return rc;
    }

    /// <summary>
    /// Attempts to free as much heap memory as possible for the database connection.
    /// </summary>
    /// <returns>A standard SQLite return code (i.e. zero for success and non-zero for failure).</returns>
    internal override SQLiteErrorCode ReleaseMemory()
    {
        SQLiteErrorCode rc = UnsafeNativeMethods.sqlite3_db_release_memory(_sql);
        return rc;
    }

    /// <summary>
    /// Attempts to free N bytes of heap memory by deallocating non-essential memory
    /// allocations held by the database library. Memory used to cache database pages
    /// to improve performance is an example of non-essential memory.  This is a no-op
    /// returning zero if the SQLite core library was not compiled with the compile-time
    /// option SQLITE_ENABLE_MEMORY_MANAGEMENT.  Optionally, attempts to reset and/or
    /// compact the Win32 native heap, if applicable.
    /// </summary>
    /// <param name="nBytes">
    /// The requested number of bytes to free.
    /// </param>
    /// <param name="reset">
    /// Non-zero to attempt a heap reset.
    /// </param>
    /// <param name="compact">
    /// Non-zero to attempt heap compaction.
    /// </param>
    /// <param name="nFree">
    /// The number of bytes actually freed.  This value may be zero.
    /// </param>
    /// <param name="resetOk">
    /// This value will be non-zero if the heap reset was successful.
    /// </param>
    /// <param name="nLargest">
    /// The size of the largest committed free block in the heap, in bytes.
    /// This value will be zero unless heap compaction is enabled.
    /// </param>
    /// <returns>
    /// A standard SQLite return code (i.e. zero for success and non-zero
    /// for failure).
    /// </returns>
    internal static SQLiteErrorCode StaticReleaseMemory(
        int nBytes,
        bool reset,
        bool compact,
        ref int nFree,
        ref bool resetOk,
        ref uint nLargest
        )
    {
        SQLiteErrorCode rc = SQLiteErrorCode.Ok;

        int nFreeLocal = UnsafeNativeMethods.sqlite3_release_memory(nBytes);
        uint nLargestLocal = 0;
        bool resetOkLocal = false;

#if !DEBUG && WINDOWS // NOTE: Should be "WIN32HEAP && !MEMDEBUG && WINDOWS"
        if ((rc == SQLiteErrorCode.Ok) && reset)
        {
            rc = UnsafeNativeMethods.sqlite3_win32_reset_heap();

            if (rc == SQLiteErrorCode.Ok)
                resetOkLocal = true;
        }

        if ((rc == SQLiteErrorCode.Ok) && compact)
            rc = UnsafeNativeMethods.sqlite3_win32_compact_heap(ref nLargestLocal);
#else
        if (reset || compact)
            rc = SQLiteErrorCode.NotFound;
#endif

        nFree = nFreeLocal;
        nLargest = nLargestLocal;
        resetOk = resetOkLocal;

        return rc;
    }

    /// <summary>
    /// Shutdown the SQLite engine so that it can be restarted with different
    /// configuration options.  We depend on auto initialization to recover.
    /// </summary>
    /// <returns>Returns a standard SQLite result code.</returns>
    internal override SQLiteErrorCode Shutdown()
    {
        return StaticShutdown(false);
    }

    /// <summary>
    /// Shutdown the SQLite engine so that it can be restarted with different
    /// configuration options.  We depend on auto initialization to recover.
    /// </summary>
    /// <param name="directories">
    /// Non-zero to reset the database and temporary directories to their
    /// default values, which should be null for both.  This parameter has no
    /// effect on non-Windows operating systems.
    /// </param>
    /// <returns>Returns a standard SQLite result code.</returns>
    internal static SQLiteErrorCode StaticShutdown(
        bool directories
        )
    {
        SQLiteErrorCode rc = SQLiteErrorCode.Ok;

        if (directories)
        {
#if WINDOWS
            if (rc == SQLiteErrorCode.Ok)
                rc = UnsafeNativeMethods.sqlite3_win32_set_directory(1, null);

            if (rc == SQLiteErrorCode.Ok)
                rc = UnsafeNativeMethods.sqlite3_win32_set_directory(2, null);
#else
#if !NET_COMPACT_20 && TRACE_CONNECTION
            Trace.WriteLine(
                "Shutdown: Cannot reset directories on this platform.");
#endif
#endif
        }

        if (rc == SQLiteErrorCode.Ok)
            rc = UnsafeNativeMethods.sqlite3_shutdown();

        return rc;
    }

    /// <summary>
    /// Determines if the associated native connection handle is open.
    /// </summary>
    /// <returns>
    /// Non-zero if the associated native connection handle is open.
    /// </returns>
    internal override bool IsOpen()
    {
        return (_sql != null) && !_sql.IsInvalid && !_sql.IsClosed;
    }

    internal override void Open(string strFilename, SQLiteConnectionFlags connectionFlags, SQLiteOpenFlagsEnum openFlags, int maxPoolSize, bool usePool)
    {
      //
      // NOTE: If the database connection is currently open, attempt to
      //       close it now.  This must be done because the file name or
      //       other parameters that may impact the underlying database
      //       connection may have changed.
      //
      if (_sql != null) Close(true);

      //
      // NOTE: If the connection was not closed successfully, throw an
      //       exception now.
      //
      if (_sql != null)
          throw new SQLiteException("connection handle is still active");

      _usePool = usePool;
      _fileName = strFilename;

      if (usePool)
      {
        _sql = SQLiteConnectionPool.Remove(strFilename, maxPoolSize, out _poolVersion);

#if !NET_COMPACT_20 && TRACE_CONNECTION
        Trace.WriteLine(String.Format("Open (Pool): {0}", (_sql != null) ? _sql.ToString() : "<null>"));
#endif
      }

      if (_sql == null)
      {
        try
        {
            // do nothing.
        }
        finally /* NOTE: Thread.Abort() protection. */
        {
          IntPtr db;
          SQLiteErrorCode n;

#if !SQLITE_STANDARD
          if ((connectionFlags & SQLiteConnectionFlags.NoExtensionFunctions) != SQLiteConnectionFlags.NoExtensionFunctions)
          {
            n = UnsafeNativeMethods.sqlite3_open_interop(ToUTF8(strFilename), openFlags, out db);
          }
          else
#endif
          {
            n = UnsafeNativeMethods.sqlite3_open_v2(ToUTF8(strFilename), out db, openFlags, IntPtr.Zero);
          }

#if !NET_COMPACT_20 && TRACE_CONNECTION
          Trace.WriteLine(String.Format("Open: {0}", db));
#endif

          if (n != SQLiteErrorCode.Ok) throw new SQLiteException(n, null);
          _sql = new SQLiteConnectionHandle(db, true);
        }
        lock (_sql) { /* HACK: Force the SyncBlock to be "created" now. */ }

        SQLiteConnection.OnChanged(null, new ConnectionEventArgs(
            SQLiteConnectionEventType.NewCriticalHandle, null, null,
            null, null, _sql, strFilename, new object[] { strFilename,
            connectionFlags, openFlags, maxPoolSize, usePool }));
      }

      // Bind functions to this connection.  If any previous functions of the same name
      // were already bound, then the new bindings replace the old.
      if ((connectionFlags & SQLiteConnectionFlags.NoBindFunctions) != SQLiteConnectionFlags.NoBindFunctions)
      {
          if (_functions == null)
              _functions = new List<SQLiteFunction>();

          _functions.AddRange(new List<SQLiteFunction>(SQLiteFunction.BindFunctions(this, connectionFlags)));
      }

      SetTimeout(0);
      GC.KeepAlive(_sql);
    }

    internal override void ClearPool()
    {
      SQLiteConnectionPool.ClearPool(_fileName);
    }

    internal override int CountPool()
    {
        Dictionary<string, int> counts = null;
        int openCount = 0;
        int closeCount = 0;
        int totalCount = 0;

        SQLiteConnectionPool.GetCounts(_fileName,
            ref counts, ref openCount, ref closeCount,
            ref totalCount);

        return totalCount;
    }

    internal override void SetTimeout(int nTimeoutMS)
    {
      IntPtr db = _sql;
      if (db == IntPtr.Zero) throw new SQLiteException("no connection handle available");
      SQLiteErrorCode n = UnsafeNativeMethods.sqlite3_busy_timeout(db, nTimeoutMS);
      if (n != SQLiteErrorCode.Ok) throw new SQLiteException(n, GetLastError());
    }

    internal override bool Step(SQLiteStatement stmt)
    {
      SQLiteErrorCode n;
      Random rnd = null;
      uint starttick = (uint)Environment.TickCount;
      uint timeout = (uint)(stmt._command._commandTimeout * 1000);

      while (true)
      {
        n = UnsafeNativeMethods.sqlite3_step(stmt._sqlite_stmt);

        if (n == SQLiteErrorCode.Row) return true;
        if (n == SQLiteErrorCode.Done) return false;

        if (n != SQLiteErrorCode.Ok)
        {
          SQLiteErrorCode r;

          // An error occurred, attempt to reset the statement.  If the reset worked because the
          // schema has changed, re-try the step again.  If it errored our because the database
          // is locked, then keep retrying until the command timeout occurs.
          r = Reset(stmt);

          if (r == SQLiteErrorCode.Ok)
            throw new SQLiteException(n, GetLastError());

          else if ((r == SQLiteErrorCode.Locked || r == SQLiteErrorCode.Busy) && stmt._command != null)
          {
            // Keep trying
            if (rnd == null) // First time we've encountered the lock
              rnd = new Random();

            // If we've exceeded the command's timeout, give up and throw an error
            if ((uint)Environment.TickCount - starttick > timeout)
            {
              throw new SQLiteException(r, GetLastError());
            }
            else
            {
              // Otherwise sleep for a random amount of time up to 150ms
              System.Threading.Thread.Sleep(rnd.Next(1, 150));
            }
          }
        }
      }
    }

    internal override SQLiteErrorCode Reset(SQLiteStatement stmt)
    {
      SQLiteErrorCode n;

#if !SQLITE_STANDARD
      n = UnsafeNativeMethods.sqlite3_reset_interop(stmt._sqlite_stmt);
#else
      n = UnsafeNativeMethods.sqlite3_reset(stmt._sqlite_stmt);
#endif

      // If the schema changed, try and re-prepare it
      if (n == SQLiteErrorCode.Schema)
      {
        // Recreate a dummy statement
        string str;
        using (SQLiteStatement tmp = Prepare(null, stmt._sqlStatement, null, (uint)(stmt._command._commandTimeout * 1000), out str))
        {
          // Finalize the existing statement
          stmt._sqlite_stmt.Dispose();
          // Reassign a new statement pointer to the old statement and clear the temporary one
          stmt._sqlite_stmt = tmp._sqlite_stmt;
          tmp._sqlite_stmt = null;

          // Reapply parameters
          stmt.BindParameters();
        }
        return SQLiteErrorCode.Unknown; // Reset was OK, with schema change
      }
      else if (n == SQLiteErrorCode.Locked || n == SQLiteErrorCode.Busy)
        return n;

      if (n != SQLiteErrorCode.Ok)
        throw new SQLiteException(n, GetLastError());

      return n; // We reset OK, no schema changes
    }

    internal override string GetLastError()
    {
        return GetLastError(null);
    }

    internal override string GetLastError(string defValue)
    {
        string result = SQLiteBase.GetLastError(_sql, _sql);
        if (String.IsNullOrEmpty(result)) result = defValue;
        return result;
    }

    internal override SQLiteStatement Prepare(SQLiteConnection cnn, string strSql, SQLiteStatement previous, uint timeoutMS, out string strRemain)
    {
      if (!String.IsNullOrEmpty(strSql))
      {
        //
        // NOTE: SQLite does not support the concept of separate schemas
        //       in one database; therefore, remove the base schema name
        //       used to smooth integration with the base .NET Framework
        //       data classes.
        //
        string baseSchemaName = (cnn != null) ? cnn._baseSchemaName : null;

        if (!String.IsNullOrEmpty(baseSchemaName))
        {
          strSql = strSql.Replace(
              String.Format(CultureInfo.InvariantCulture,
              "[{0}].", baseSchemaName), String.Empty);

          strSql = strSql.Replace(
              String.Format(CultureInfo.InvariantCulture,
              "{0}.", baseSchemaName), String.Empty);
        }
      }

      SQLiteConnectionFlags flags =
          (cnn != null) ? cnn.Flags : SQLiteConnectionFlags.Default;

      if ((flags & SQLiteConnectionFlags.LogPrepare) == SQLiteConnectionFlags.LogPrepare)
      {
          if ((strSql == null) || (strSql.Length == 0) || (strSql.Trim().Length == 0))
              SQLiteLog.LogMessage("Preparing {<nothing>}...");
          else
              SQLiteLog.LogMessage(String.Format(
                  CultureInfo.CurrentCulture, "Preparing {{{0}}}...", strSql));
      }

      IntPtr stmt = IntPtr.Zero;
      IntPtr ptr = IntPtr.Zero;
      int len = 0;
      SQLiteErrorCode n = SQLiteErrorCode.Schema;
      int retries = 0;
      byte[] b = ToUTF8(strSql);
      string typedefs = null;
      SQLiteStatement cmd = null;
      Random rnd = null;
      uint starttick = (uint)Environment.TickCount;

      GCHandle handle = GCHandle.Alloc(b, GCHandleType.Pinned);
      IntPtr psql = handle.AddrOfPinnedObject();
      SQLiteStatementHandle statementHandle = null;
      try
      {
        while ((n == SQLiteErrorCode.Schema || n == SQLiteErrorCode.Locked || n == SQLiteErrorCode.Busy) && retries < 3)
        {
          try
          {
            // do nothing.
          }
          finally /* NOTE: Thread.Abort() protection. */
          {
#if !SQLITE_STANDARD
            n = UnsafeNativeMethods.sqlite3_prepare_interop(_sql, psql, b.Length - 1, out stmt, out ptr, out len);
#else
#if USE_PREPARE_V2
            n = UnsafeNativeMethods.sqlite3_prepare_v2(_sql, psql, b.Length - 1, out stmt, out ptr);
#else
            n = UnsafeNativeMethods.sqlite3_prepare(_sql, psql, b.Length - 1, out stmt, out ptr);
#endif
            len = -1;
#endif

#if !NET_COMPACT_20 && TRACE_STATEMENT
            Trace.WriteLine(String.Format("Prepare ({0}): {1}", n, stmt));
#endif

            if ((n == SQLiteErrorCode.Ok) && (stmt != IntPtr.Zero))
            {
              if (statementHandle != null) statementHandle.Dispose();
              statementHandle = new SQLiteStatementHandle(_sql, stmt);
            }
          }

          if (statementHandle != null)
          {
            SQLiteConnection.OnChanged(null, new ConnectionEventArgs(
              SQLiteConnectionEventType.NewCriticalHandle, null, null,
              null, null, statementHandle, strSql, new object[] { cnn,
              strSql, previous, timeoutMS }));
          }

          if (n == SQLiteErrorCode.Schema)
            retries++;
          else if (n == SQLiteErrorCode.Error)
          {
            if (String.Compare(GetLastError(), "near \"TYPES\": syntax error", StringComparison.OrdinalIgnoreCase) == 0)
            {
              int pos = strSql.IndexOf(';');
              if (pos == -1) pos = strSql.Length - 1;

              typedefs = strSql.Substring(0, pos + 1);
              strSql = strSql.Substring(pos + 1);

              strRemain = "";

              while (cmd == null && strSql.Length > 0)
              {
                cmd = Prepare(cnn, strSql, previous, timeoutMS, out strRemain);
                strSql = strRemain;
              }

              if (cmd != null)
                cmd.SetTypes(typedefs);

              return cmd;
            }
#if (NET_35 || NET_40 || NET_45 || NET_451) && !PLATFORM_COMPACTFRAMEWORK
            else if (_buildingSchema == false && String.Compare(GetLastError(), 0, "no such table: TEMP.SCHEMA", 0, 26, StringComparison.OrdinalIgnoreCase) == 0)
            {
              strRemain = "";
              _buildingSchema = true;
              try
              {
                ISQLiteSchemaExtensions ext = ((IServiceProvider)SQLiteFactory.Instance).GetService(typeof(ISQLiteSchemaExtensions)) as ISQLiteSchemaExtensions;

                if (ext != null)
                  ext.BuildTempSchema(cnn);

                while (cmd == null && strSql.Length > 0)
                {
                  cmd = Prepare(cnn, strSql, previous, timeoutMS, out strRemain);
                  strSql = strRemain;
                }

                return cmd;
              }
              finally
              {
                _buildingSchema = false;
              }
            }
#endif
          }
          else if (n == SQLiteErrorCode.Locked || n == SQLiteErrorCode.Busy) // Locked -- delay a small amount before retrying
          {
            // Keep trying
            if (rnd == null) // First time we've encountered the lock
              rnd = new Random();

            // If we've exceeded the command's timeout, give up and throw an error
            if ((uint)Environment.TickCount - starttick > timeoutMS)
            {
              throw new SQLiteException(n, GetLastError());
            }
            else
            {
              // Otherwise sleep for a random amount of time up to 150ms
              System.Threading.Thread.Sleep(rnd.Next(1, 150));
            }
          }
        }

        if (n != SQLiteErrorCode.Ok) throw new SQLiteException(n, GetLastError());

        strRemain = UTF8ToString(ptr, len);

        if (statementHandle != null) cmd = new SQLiteStatement(this, flags, statementHandle, strSql.Substring(0, strSql.Length - strRemain.Length), previous);

        return cmd;
      }
      finally
      {
        handle.Free();
      }
    }

    protected static void LogBind(SQLiteStatementHandle handle, int index)
    {
        IntPtr handleIntPtr = handle;

        SQLiteLog.LogMessage(String.Format(
            CultureInfo.CurrentCulture,
            "Binding statement {0} paramter #{1} as NULL...",
            handleIntPtr, index));
    }

    protected static void LogBind(SQLiteStatementHandle handle, int index, ValueType value)
    {
        IntPtr handleIntPtr = handle;

        SQLiteLog.LogMessage(String.Format(
            "Binding statement {0} paramter #{1} as type {2} with value {{{3}}}...",
            handleIntPtr, index, value.GetType(), value));
    }

    private static string FormatDateTime(DateTime value)
    {
        StringBuilder result = new StringBuilder();

        result.Append(value.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFFK"));
        result.Append(' ');
        result.Append(value.Kind);
        result.Append(' ');
        result.Append(value.Ticks);

        return result.ToString();
    }

    protected static void LogBind(SQLiteStatementHandle handle, int index, DateTime value)
    {
        IntPtr handleIntPtr = handle;

        SQLiteLog.LogMessage(String.Format(
            "Binding statement {0} paramter #{1} as type {2} with value {{{3}}}...",
            handleIntPtr, index, typeof(DateTime), FormatDateTime(value)));
    }

    protected static void LogBind(SQLiteStatementHandle handle, int index, string value)
    {
        IntPtr handleIntPtr = handle;

        SQLiteLog.LogMessage(String.Format(
            "Binding statement {0} paramter #{1} as type {2} with value {{{3}}}...",
            handleIntPtr, index, typeof(String), (value != null) ? value : "<null>"));
    }

    private static string ToHexadecimalString(
        byte[] array
        )
    {
        if (array == null)
            return null;

        StringBuilder result = new StringBuilder(array.Length * 2);

        int length = array.Length;

        for (int index = 0; index < length; index++)
            result.Append(array[index].ToString("x2"));

        return result.ToString();
    }

    protected static void LogBind(SQLiteStatementHandle handle, int index, byte[] value)
    {
        IntPtr handleIntPtr = handle;

        SQLiteLog.LogMessage(String.Format(
            "Binding statement {0} paramter #{1} as type {2} with value {{{3}}}...",
            handleIntPtr, index, typeof(Byte[]), (value != null) ? ToHexadecimalString(value) : "<null>"));
    }

    internal override void Bind_Double(SQLiteStatement stmt, SQLiteConnectionFlags flags, int index, double value)
    {
        SQLiteStatementHandle handle = stmt._sqlite_stmt;

        if ((flags & SQLiteConnectionFlags.LogBind) == SQLiteConnectionFlags.LogBind)
        {
            LogBind(handle, index, value);
        }

#if !PLATFORM_COMPACTFRAMEWORK
        SQLiteErrorCode n = UnsafeNativeMethods.sqlite3_bind_double(handle, index, value);
#elif !SQLITE_STANDARD
        SQLiteErrorCode n = UnsafeNativeMethods.sqlite3_bind_double_interop(handle, index, ref value);
#else
        throw new NotImplementedException();
#endif
        if (n != SQLiteErrorCode.Ok) throw new SQLiteException(n, GetLastError());
    }

    internal override void Bind_Int32(SQLiteStatement stmt, SQLiteConnectionFlags flags, int index, int value)
    {
        SQLiteStatementHandle handle = stmt._sqlite_stmt;

        if ((flags & SQLiteConnectionFlags.LogBind) == SQLiteConnectionFlags.LogBind)
        {
            LogBind(handle, index, value);
        }

        SQLiteErrorCode n = UnsafeNativeMethods.sqlite3_bind_int(handle, index, value);
        if (n != SQLiteErrorCode.Ok) throw new SQLiteException(n, GetLastError());
    }

    internal override void Bind_UInt32(SQLiteStatement stmt, SQLiteConnectionFlags flags, int index, uint value)
    {
        SQLiteStatementHandle handle = stmt._sqlite_stmt;

#if !PLATFORM_COMPACTFRAMEWORK
        if ((flags & SQLiteConnectionFlags.LogBind) == SQLiteConnectionFlags.LogBind)
        {
            LogBind(handle, index, value);
        }
#endif

        SQLiteErrorCode n;

        if ((flags & SQLiteConnectionFlags.BindUInt32AsInt64) == SQLiteConnectionFlags.BindUInt32AsInt64)
        {
            long value2 = value;

#if !PLATFORM_COMPACTFRAMEWORK
            n = UnsafeNativeMethods.sqlite3_bind_int64(handle, index, value2);
#elif !SQLITE_STANDARD
            n = UnsafeNativeMethods.sqlite3_bind_int64_interop(handle, index, ref value2);
#else
            throw new NotImplementedException();
#endif
        }
        else
        {
            n = UnsafeNativeMethods.sqlite3_bind_uint(handle, index, value);
        }
        if (n != SQLiteErrorCode.Ok) throw new SQLiteException(n, GetLastError());
    }

    internal override void Bind_Int64(SQLiteStatement stmt, SQLiteConnectionFlags flags, int index, long value)
    {
        SQLiteStatementHandle handle = stmt._sqlite_stmt;

#if !PLATFORM_COMPACTFRAMEWORK
        if ((flags & SQLiteConnectionFlags.LogBind) == SQLiteConnectionFlags.LogBind)
        {
            LogBind(handle, index, value);
        }

        SQLiteErrorCode n = UnsafeNativeMethods.sqlite3_bind_int64(handle, index, value);
#elif !SQLITE_STANDARD
        SQLiteErrorCode n = UnsafeNativeMethods.sqlite3_bind_int64_interop(handle, index, ref value);
#else
        throw new NotImplementedException();
#endif
        if (n != SQLiteErrorCode.Ok) throw new SQLiteException(n, GetLastError());
    }

    internal override void Bind_UInt64(SQLiteStatement stmt, SQLiteConnectionFlags flags, int index, ulong value)
    {
        SQLiteStatementHandle handle = stmt._sqlite_stmt;

#if !PLATFORM_COMPACTFRAMEWORK
        if ((flags & SQLiteConnectionFlags.LogBind) == SQLiteConnectionFlags.LogBind)
        {
            LogBind(handle, index, value);
        }

        SQLiteErrorCode n = UnsafeNativeMethods.sqlite3_bind_uint64(handle, index, value);
#elif !SQLITE_STANDARD
        SQLiteErrorCode n = UnsafeNativeMethods.sqlite3_bind_uint64_interop(handle, index, ref value);
#else
        throw new NotImplementedException();
#endif
        if (n != SQLiteErrorCode.Ok) throw new SQLiteException(n, GetLastError());
    }

    internal override void Bind_Text(SQLiteStatement stmt, SQLiteConnectionFlags flags, int index, string value)
    {
        SQLiteStatementHandle handle = stmt._sqlite_stmt;

#if !PLATFORM_COMPACTFRAMEWORK
        if ((flags & SQLiteConnectionFlags.LogBind) == SQLiteConnectionFlags.LogBind)
        {
            LogBind(handle, index, value);
        }
#endif

        byte[] b = ToUTF8(value);

#if !PLATFORM_COMPACTFRAMEWORK
        if ((flags & SQLiteConnectionFlags.LogBind) == SQLiteConnectionFlags.LogBind)
        {
            LogBind(handle, index, b);
        }
#endif

        SQLiteErrorCode n = UnsafeNativeMethods.sqlite3_bind_text(handle, index, b, b.Length - 1, (IntPtr)(-1));
        if (n != SQLiteErrorCode.Ok) throw new SQLiteException(n, GetLastError());
    }

    internal override void Bind_DateTime(SQLiteStatement stmt, SQLiteConnectionFlags flags, int index, DateTime dt)
    {
        SQLiteStatementHandle handle = stmt._sqlite_stmt;

#if !PLATFORM_COMPACTFRAMEWORK
        if ((flags & SQLiteConnectionFlags.LogBind) == SQLiteConnectionFlags.LogBind)
        {
            LogBind(handle, index, dt);
        }
#endif

        switch (_datetimeFormat)
        {
            case SQLiteDateFormats.Ticks:
                {
                    long value = dt.Ticks;

#if !PLATFORM_COMPACTFRAMEWORK
                    if ((flags & SQLiteConnectionFlags.LogBind) == SQLiteConnectionFlags.LogBind)
                    {
                        LogBind(handle, index, value);
                    }

                    SQLiteErrorCode n = UnsafeNativeMethods.sqlite3_bind_int64(handle, index, value);
#elif !SQLITE_STANDARD
                    SQLiteErrorCode n = UnsafeNativeMethods.sqlite3_bind_int64_interop(handle, index, ref value);
#else
                    throw new NotImplementedException();
#endif
                    if (n != SQLiteErrorCode.Ok) throw new SQLiteException(n, GetLastError());
                    break;
                }
            case SQLiteDateFormats.JulianDay:
                {
                    double value = ToJulianDay(dt);

#if !PLATFORM_COMPACTFRAMEWORK
                    if ((flags & SQLiteConnectionFlags.LogBind) == SQLiteConnectionFlags.LogBind)
                    {
                        LogBind(handle, index, value);
                    }

                    SQLiteErrorCode n = UnsafeNativeMethods.sqlite3_bind_double(handle, index, value);
#elif !SQLITE_STANDARD
                    SQLiteErrorCode n = UnsafeNativeMethods.sqlite3_bind_double_interop(handle, index, ref value);
#else
                    throw new NotImplementedException();
#endif
                    if (n != SQLiteErrorCode.Ok) throw new SQLiteException(n, GetLastError());
                    break;
                }
            case SQLiteDateFormats.UnixEpoch:
                {
                    long value = Convert.ToInt64(dt.Subtract(UnixEpoch).TotalSeconds);

#if !PLATFORM_COMPACTFRAMEWORK
                    if ((flags & SQLiteConnectionFlags.LogBind) == SQLiteConnectionFlags.LogBind)
                    {
                        LogBind(handle, index, value);
                    }

                    SQLiteErrorCode n = UnsafeNativeMethods.sqlite3_bind_int64(handle, index, value);
#elif !SQLITE_STANDARD
                    SQLiteErrorCode n = UnsafeNativeMethods.sqlite3_bind_int64_interop(handle, index, ref value);
#else
                    throw new NotImplementedException();
#endif
                    if (n != SQLiteErrorCode.Ok) throw new SQLiteException(n, GetLastError());
                    break;
                }
            default:
                {
                    byte[] b = ToUTF8(dt);

#if !PLATFORM_COMPACTFRAMEWORK
                    if ((flags & SQLiteConnectionFlags.LogBind) == SQLiteConnectionFlags.LogBind)
                    {
                        LogBind(handle, index, b);
                    }
#endif

                    SQLiteErrorCode n = UnsafeNativeMethods.sqlite3_bind_text(handle, index, b, b.Length - 1, (IntPtr)(-1));
                    if (n != SQLiteErrorCode.Ok) throw new SQLiteException(n, GetLastError());
                    break;
                }
        }
    }

    internal override void Bind_Blob(SQLiteStatement stmt, SQLiteConnectionFlags flags, int index, byte[] blobData)
    {
        SQLiteStatementHandle handle = stmt._sqlite_stmt;

#if !PLATFORM_COMPACTFRAMEWORK
        if ((flags & SQLiteConnectionFlags.LogBind) == SQLiteConnectionFlags.LogBind)
        {
            LogBind(handle, index, blobData);
        }
#endif

        SQLiteErrorCode n = UnsafeNativeMethods.sqlite3_bind_blob(handle, index, blobData, blobData.Length, (IntPtr)(-1));
        if (n != SQLiteErrorCode.Ok) throw new SQLiteException(n, GetLastError());
    }

    internal override void Bind_Null(SQLiteStatement stmt, SQLiteConnectionFlags flags, int index)
    {
        SQLiteStatementHandle handle = stmt._sqlite_stmt;

#if !PLATFORM_COMPACTFRAMEWORK
        if ((flags & SQLiteConnectionFlags.LogBind) == SQLiteConnectionFlags.LogBind)
        {
            LogBind(handle, index);
        }
#endif

        SQLiteErrorCode n = UnsafeNativeMethods.sqlite3_bind_null(handle, index);
        if (n != SQLiteErrorCode.Ok) throw new SQLiteException(n, GetLastError());
    }

    internal override int Bind_ParamCount(SQLiteStatement stmt, SQLiteConnectionFlags flags)
    {
        SQLiteStatementHandle handle = stmt._sqlite_stmt;
        int value = UnsafeNativeMethods.sqlite3_bind_parameter_count(handle);

        if ((flags & SQLiteConnectionFlags.LogBind) == SQLiteConnectionFlags.LogBind)
        {
            IntPtr handleIntPtr = handle;

            SQLiteLog.LogMessage(String.Format(
                CultureInfo.CurrentCulture,
                "Statement {0} paramter count is {1}.",
                handleIntPtr, value));
        }

        return value;
    }

    internal override string Bind_ParamName(SQLiteStatement stmt, SQLiteConnectionFlags flags, int index)
    {
        SQLiteStatementHandle handle = stmt._sqlite_stmt;
        string name;

#if !SQLITE_STANDARD
        int len;
        name = UTF8ToString(UnsafeNativeMethods.sqlite3_bind_parameter_name_interop(handle, index, out len), len);
#else
        name = UTF8ToString(UnsafeNativeMethods.sqlite3_bind_parameter_name(handle, index), -1);
#endif

        if ((flags & SQLiteConnectionFlags.LogBind) == SQLiteConnectionFlags.LogBind)
        {
            IntPtr handleIntPtr = handle;

            SQLiteLog.LogMessage(String.Format(
                CultureInfo.CurrentCulture,
                "Statement {0} paramter #{1} name is {{{2}}}.",
                handleIntPtr, index, name));
        }

        return name;
    }

    internal override int Bind_ParamIndex(SQLiteStatement stmt, SQLiteConnectionFlags flags, string paramName)
    {
        SQLiteStatementHandle handle = stmt._sqlite_stmt;
        int index = UnsafeNativeMethods.sqlite3_bind_parameter_index(handle, ToUTF8(paramName));

        if ((flags & SQLiteConnectionFlags.LogBind) == SQLiteConnectionFlags.LogBind)
        {
            IntPtr handleIntPtr = handle;

            SQLiteLog.LogMessage(String.Format(
                CultureInfo.CurrentCulture,
                "Statement {0} paramter index of name {{{1}}} is #{2}.",
                handleIntPtr, paramName, index));
        }

        return index;
    }

    internal override int ColumnCount(SQLiteStatement stmt)
    {
      return UnsafeNativeMethods.sqlite3_column_count(stmt._sqlite_stmt);
    }

    internal override string ColumnName(SQLiteStatement stmt, int index)
    {
#if !SQLITE_STANDARD
      int len;
      IntPtr p = UnsafeNativeMethods.sqlite3_column_name_interop(stmt._sqlite_stmt, index, out len);
#else
      IntPtr p = UnsafeNativeMethods.sqlite3_column_name(stmt._sqlite_stmt, index);
#endif
      if (p == IntPtr.Zero)
        throw new SQLiteException(SQLiteErrorCode.NoMem, GetLastError());
#if !SQLITE_STANDARD
      return UTF8ToString(p, len);
#else
      return UTF8ToString(p, -1);
#endif
    }

    internal override TypeAffinity ColumnAffinity(SQLiteStatement stmt, int index)
    {
      return UnsafeNativeMethods.sqlite3_column_type(stmt._sqlite_stmt, index);
    }

    internal override string ColumnType(SQLiteStatement stmt, int index, out TypeAffinity nAffinity)
    {
      int len;
#if !SQLITE_STANDARD
      IntPtr p = UnsafeNativeMethods.sqlite3_column_decltype_interop(stmt._sqlite_stmt, index, out len);
#else
      len = -1;
      IntPtr p = UnsafeNativeMethods.sqlite3_column_decltype(stmt._sqlite_stmt, index);
#endif
      nAffinity = ColumnAffinity(stmt, index);

      if (p != IntPtr.Zero) return UTF8ToString(p, len);
      else
      {
        string[] ar = stmt.TypeDefinitions;
        if (ar != null)
        {
          if (index < ar.Length && ar[index] != null)
            return ar[index];
        }
        return String.Empty;

        //switch (nAffinity)
        //{
        //  case TypeAffinity.Int64:
        //    return "BIGINT";
        //  case TypeAffinity.Double:
        //    return "DOUBLE";
        //  case TypeAffinity.Blob:
        //    return "BLOB";
        //  default:
        //    return "TEXT";
        //}
      }
    }

    internal override int ColumnIndex(SQLiteStatement stmt, string columnName)
    {
      int x = ColumnCount(stmt);

      for (int n = 0; n < x; n++)
      {
        if (String.Compare(columnName, ColumnName(stmt, n), StringComparison.OrdinalIgnoreCase) == 0)
          return n;
      }
      return -1;
    }

    internal override string ColumnOriginalName(SQLiteStatement stmt, int index)
    {
#if !SQLITE_STANDARD
      int len;
      return UTF8ToString(UnsafeNativeMethods.sqlite3_column_origin_name_interop(stmt._sqlite_stmt, index, out len), len);
#else
      return UTF8ToString(UnsafeNativeMethods.sqlite3_column_origin_name(stmt._sqlite_stmt, index), -1);
#endif
    }

    internal override string ColumnDatabaseName(SQLiteStatement stmt, int index)
    {
#if !SQLITE_STANDARD
      int len;
      return UTF8ToString(UnsafeNativeMethods.sqlite3_column_database_name_interop(stmt._sqlite_stmt, index, out len), len);
#else
      return UTF8ToString(UnsafeNativeMethods.sqlite3_column_database_name(stmt._sqlite_stmt, index), -1);
#endif
    }

    internal override string ColumnTableName(SQLiteStatement stmt, int index)
    {
#if !SQLITE_STANDARD
      int len;
      return UTF8ToString(UnsafeNativeMethods.sqlite3_column_table_name_interop(stmt._sqlite_stmt, index, out len), len);
#else
      return UTF8ToString(UnsafeNativeMethods.sqlite3_column_table_name(stmt._sqlite_stmt, index), -1);
#endif
    }

    internal override void ColumnMetaData(string dataBase, string table, string column, out string dataType, out string collateSequence, out bool notNull, out bool primaryKey, out bool autoIncrement)
    {
      IntPtr dataTypePtr;
      IntPtr collSeqPtr;
      int nnotNull;
      int nprimaryKey;
      int nautoInc;
      SQLiteErrorCode n;
      int dtLen;
      int csLen;

#if !SQLITE_STANDARD
      n = UnsafeNativeMethods.sqlite3_table_column_metadata_interop(_sql, ToUTF8(dataBase), ToUTF8(table), ToUTF8(column), out dataTypePtr, out collSeqPtr, out nnotNull, out nprimaryKey, out nautoInc, out dtLen, out csLen);
#else
      dtLen = -1;
      csLen = -1;

      n = UnsafeNativeMethods.sqlite3_table_column_metadata(_sql, ToUTF8(dataBase), ToUTF8(table), ToUTF8(column), out dataTypePtr, out collSeqPtr, out nnotNull, out nprimaryKey, out nautoInc);
#endif
      if (n != SQLiteErrorCode.Ok) throw new SQLiteException(n, GetLastError());

      dataType = UTF8ToString(dataTypePtr, dtLen);
      collateSequence = UTF8ToString(collSeqPtr, csLen);

      notNull = (nnotNull == 1);
      primaryKey = (nprimaryKey == 1);
      autoIncrement = (nautoInc == 1);
    }

    internal override double GetDouble(SQLiteStatement stmt, int index)
    {
      double value;
#if !PLATFORM_COMPACTFRAMEWORK
      value = UnsafeNativeMethods.sqlite3_column_double(stmt._sqlite_stmt, index);
#elif !SQLITE_STANDARD
      UnsafeNativeMethods.sqlite3_column_double_interop(stmt._sqlite_stmt, index, out value);
#else
      throw new NotImplementedException();
#endif
      return value;
    }

    internal override sbyte GetSByte(SQLiteStatement stmt, int index)
    {
      return unchecked((sbyte)(GetInt32(stmt, index) & byte.MaxValue));
    }

    internal override byte GetByte(SQLiteStatement stmt, int index)
    {
      return unchecked((byte)(GetInt32(stmt, index) & byte.MaxValue));
    }

    internal override short GetInt16(SQLiteStatement stmt, int index)
    {
      return unchecked((short)(GetInt32(stmt, index) & ushort.MaxValue));
    }

    internal override ushort GetUInt16(SQLiteStatement stmt, int index)
    {
      return unchecked((ushort)(GetInt32(stmt, index) & ushort.MaxValue));
    }

    internal override int GetInt32(SQLiteStatement stmt, int index)
    {
      return UnsafeNativeMethods.sqlite3_column_int(stmt._sqlite_stmt, index);
    }

    internal override uint GetUInt32(SQLiteStatement stmt, int index)
    {
      return unchecked((uint)GetInt32(stmt, index));
    }

    internal override long GetInt64(SQLiteStatement stmt, int index)
    {
      long value;
#if !PLATFORM_COMPACTFRAMEWORK
      value = UnsafeNativeMethods.sqlite3_column_int64(stmt._sqlite_stmt, index);
#elif !SQLITE_STANDARD
      UnsafeNativeMethods.sqlite3_column_int64_interop(stmt._sqlite_stmt, index, out value);
#else
      throw new NotImplementedException();
#endif
      return value;
    }

    internal override ulong GetUInt64(SQLiteStatement stmt, int index)
    {
      return unchecked((ulong)GetInt64(stmt, index));
    }

    internal override string GetText(SQLiteStatement stmt, int index)
    {
#if !SQLITE_STANDARD
      int len;
      return UTF8ToString(UnsafeNativeMethods.sqlite3_column_text_interop(stmt._sqlite_stmt, index, out len), len);
#else
      return UTF8ToString(UnsafeNativeMethods.sqlite3_column_text(stmt._sqlite_stmt, index),
        UnsafeNativeMethods.sqlite3_column_bytes(stmt._sqlite_stmt, index));
#endif
    }

    internal override DateTime GetDateTime(SQLiteStatement stmt, int index)
    {
      if (_datetimeFormat == SQLiteDateFormats.Ticks)
        return ToDateTime(GetInt64(stmt, index), _datetimeKind);
      else if (_datetimeFormat == SQLiteDateFormats.JulianDay)
        return ToDateTime(GetDouble(stmt, index), _datetimeKind);
      else if (_datetimeFormat == SQLiteDateFormats.UnixEpoch)
        return ToDateTime(GetInt32(stmt, index), _datetimeKind);

#if !SQLITE_STANDARD
      int len;
      return ToDateTime(UnsafeNativeMethods.sqlite3_column_text_interop(stmt._sqlite_stmt, index, out len), len);
#else
      return ToDateTime(UnsafeNativeMethods.sqlite3_column_text(stmt._sqlite_stmt, index),
        UnsafeNativeMethods.sqlite3_column_bytes(stmt._sqlite_stmt, index));
#endif
    }

    internal override long GetBytes(SQLiteStatement stmt, int index, int nDataOffset, byte[] bDest, int nStart, int nLength)
    {
      int nlen = UnsafeNativeMethods.sqlite3_column_bytes(stmt._sqlite_stmt, index);

      // If no destination buffer, return the size needed.
      if (bDest == null) return nlen;

      int nCopied = nLength;

      if (nCopied + nStart > bDest.Length) nCopied = bDest.Length - nStart;
      if (nCopied + nDataOffset > nlen) nCopied = nlen - nDataOffset;

      if (nCopied > 0)
      {
        IntPtr ptr = UnsafeNativeMethods.sqlite3_column_blob(stmt._sqlite_stmt, index);

        Marshal.Copy((IntPtr)(ptr.ToInt64() + nDataOffset), bDest, nStart, nCopied);
      }
      else
      {
        nCopied = 0;
      }

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
      return (ColumnAffinity(stmt, index) == TypeAffinity.Null);
    }

    internal override int AggregateCount(IntPtr context)
    {
      return UnsafeNativeMethods.sqlite3_aggregate_count(context);
    }

    internal override void CreateFunction(string strFunction, int nArgs, bool needCollSeq, SQLiteCallback func, SQLiteCallback funcstep, SQLiteFinalCallback funcfinal)
    {
      SQLiteErrorCode n;

#if !SQLITE_STANDARD
      n = UnsafeNativeMethods.sqlite3_create_function_interop(_sql, ToUTF8(strFunction), nArgs, 4, IntPtr.Zero, func, funcstep, funcfinal, (needCollSeq == true) ? 1 : 0);
      if (n == SQLiteErrorCode.Ok) n = UnsafeNativeMethods.sqlite3_create_function_interop(_sql, ToUTF8(strFunction), nArgs, 1, IntPtr.Zero, func, funcstep, funcfinal, (needCollSeq == true) ? 1 : 0);
#else
      n = UnsafeNativeMethods.sqlite3_create_function(_sql, ToUTF8(strFunction), nArgs, 4, IntPtr.Zero, func, funcstep, funcfinal);
      if (n == SQLiteErrorCode.Ok) n = UnsafeNativeMethods.sqlite3_create_function(_sql, ToUTF8(strFunction), nArgs, 1, IntPtr.Zero, func, funcstep, funcfinal);
#endif
      if (n != SQLiteErrorCode.Ok) throw new SQLiteException(n, GetLastError());
    }

    internal override void CreateCollation(string strCollation, SQLiteCollation func, SQLiteCollation func16)
    {
      SQLiteErrorCode n = UnsafeNativeMethods.sqlite3_create_collation(_sql, ToUTF8(strCollation), 2, IntPtr.Zero, func16);
      if (n == SQLiteErrorCode.Ok) n = UnsafeNativeMethods.sqlite3_create_collation(_sql, ToUTF8(strCollation), 1, IntPtr.Zero, func);
      if (n != SQLiteErrorCode.Ok) throw new SQLiteException(n, GetLastError());
    }

    internal override int ContextCollateCompare(CollationEncodingEnum enc, IntPtr context, string s1, string s2)
    {
#if !SQLITE_STANDARD
      byte[] b1;
      byte[] b2;
      System.Text.Encoding converter = null;

      switch (enc)
      {
        case CollationEncodingEnum.UTF8:
          converter = System.Text.Encoding.UTF8;
          break;
        case CollationEncodingEnum.UTF16LE:
          converter = System.Text.Encoding.Unicode;
          break;
        case CollationEncodingEnum.UTF16BE:
          converter = System.Text.Encoding.BigEndianUnicode;
          break;
      }

      b1 = converter.GetBytes(s1);
      b2 = converter.GetBytes(s2);

      return UnsafeNativeMethods.sqlite3_context_collcompare_interop(context, b1, b1.Length, b2, b2.Length);
#else
      throw new NotImplementedException();
#endif
    }

    internal override int ContextCollateCompare(CollationEncodingEnum enc, IntPtr context, char[] c1, char[] c2)
    {
#if !SQLITE_STANDARD
      byte[] b1;
      byte[] b2;
      System.Text.Encoding converter = null;

      switch (enc)
      {
        case CollationEncodingEnum.UTF8:
          converter = System.Text.Encoding.UTF8;
          break;
        case CollationEncodingEnum.UTF16LE:
          converter = System.Text.Encoding.Unicode;
          break;
        case CollationEncodingEnum.UTF16BE:
          converter = System.Text.Encoding.BigEndianUnicode;
          break;
      }

      b1 = converter.GetBytes(c1);
      b2 = converter.GetBytes(c2);

      return UnsafeNativeMethods.sqlite3_context_collcompare_interop(context, b1, b1.Length, b2, b2.Length);
#else
      throw new NotImplementedException();
#endif
    }

    internal override CollationSequence GetCollationSequence(SQLiteFunction func, IntPtr context)
    {
#if !SQLITE_STANDARD
      CollationSequence seq = new CollationSequence();
      int len;
      int type;
      int enc;
      IntPtr p = UnsafeNativeMethods.sqlite3_context_collseq_interop(context, out type, out enc, out len);

      if (p != null) seq.Name = UTF8ToString(p, len);
      seq.Type = (CollationTypeEnum)type;
      seq._func = func;
      seq.Encoding = (CollationEncodingEnum)enc;

      return seq;
#else
      throw new NotImplementedException();
#endif
    }

    internal override long GetParamValueBytes(IntPtr p, int nDataOffset, byte[] bDest, int nStart, int nLength)
    {
      int nlen = UnsafeNativeMethods.sqlite3_value_bytes(p);

      // If no destination buffer, return the size needed.
      if (bDest == null) return nlen;

      int nCopied = nLength;

      if (nCopied + nStart > bDest.Length) nCopied = bDest.Length - nStart;
      if (nCopied + nDataOffset > nlen) nCopied = nlen - nDataOffset;

      if (nCopied > 0)
      {
        IntPtr ptr = UnsafeNativeMethods.sqlite3_value_blob(p);

        Marshal.Copy((IntPtr)(ptr.ToInt64() + nDataOffset), bDest, nStart, nCopied);
      }
      else
      {
        nCopied = 0;
      }

      return nCopied;
    }

    internal override double GetParamValueDouble(IntPtr ptr)
    {
      double value;
#if !PLATFORM_COMPACTFRAMEWORK
      value = UnsafeNativeMethods.sqlite3_value_double(ptr);
#elif !SQLITE_STANDARD
      UnsafeNativeMethods.sqlite3_value_double_interop(ptr, out value);
#else
      throw new NotImplementedException();
#endif
      return value;
    }

    internal override int GetParamValueInt32(IntPtr ptr)
    {
      return UnsafeNativeMethods.sqlite3_value_int(ptr);
    }

    internal override long GetParamValueInt64(IntPtr ptr)
    {
      Int64 value;
#if !PLATFORM_COMPACTFRAMEWORK
      value = UnsafeNativeMethods.sqlite3_value_int64(ptr);
#elif !SQLITE_STANDARD
      UnsafeNativeMethods.sqlite3_value_int64_interop(ptr, out value);
#else
      throw new NotImplementedException();
#endif
      return value;
    }

    internal override string GetParamValueText(IntPtr ptr)
    {
#if !SQLITE_STANDARD
      int len;
      return UTF8ToString(UnsafeNativeMethods.sqlite3_value_text_interop(ptr, out len), len);
#else
      return UTF8ToString(UnsafeNativeMethods.sqlite3_value_text(ptr),
        UnsafeNativeMethods.sqlite3_value_bytes(ptr));
#endif
    }

    internal override TypeAffinity GetParamValueType(IntPtr ptr)
    {
      return UnsafeNativeMethods.sqlite3_value_type(ptr);
    }

    internal override void ReturnBlob(IntPtr context, byte[] value)
    {
      UnsafeNativeMethods.sqlite3_result_blob(context, value, value.Length, (IntPtr)(-1));
    }

    internal override void ReturnDouble(IntPtr context, double value)
    {
#if !PLATFORM_COMPACTFRAMEWORK
      UnsafeNativeMethods.sqlite3_result_double(context, value);
#elif !SQLITE_STANDARD
      UnsafeNativeMethods.sqlite3_result_double_interop(context, ref value);
#else
      throw new NotImplementedException();
#endif
    }

    internal override void ReturnError(IntPtr context, string value)
    {
      UnsafeNativeMethods.sqlite3_result_error(context, ToUTF8(value), value.Length);
    }

    internal override void ReturnInt32(IntPtr context, int value)
    {
      UnsafeNativeMethods.sqlite3_result_int(context, value);
    }

    internal override void ReturnInt64(IntPtr context, long value)
    {
#if !PLATFORM_COMPACTFRAMEWORK
      UnsafeNativeMethods.sqlite3_result_int64(context, value);
#elif !SQLITE_STANDARD
      UnsafeNativeMethods.sqlite3_result_int64_interop(context, ref value);
#else
      throw new NotImplementedException();
#endif
    }

    internal override void ReturnNull(IntPtr context)
    {
      UnsafeNativeMethods.sqlite3_result_null(context);
    }

    internal override void ReturnText(IntPtr context, string value)
    {
      byte[] b = ToUTF8(value);
      UnsafeNativeMethods.sqlite3_result_text(context, ToUTF8(value), b.Length - 1, (IntPtr)(-1));
    }

#if INTEROP_VIRTUAL_TABLE
    /// <summary>
    /// Calls the native SQLite core library in order to create a disposable
    /// module containing the implementation of a virtual table.
    /// </summary>
    /// <param name="module">
    /// The module object to be used when creating the native disposable module.
    /// </param>
    /// <param name="flags">
    /// The flags for the associated <see cref="SQLiteConnection" /> object instance.
    /// </param>
    internal override void CreateModule(SQLiteModule module, SQLiteConnectionFlags flags)
    {
        if (module == null)
            throw new ArgumentNullException("module");

        if ((flags & SQLiteConnectionFlags.NoLogModule) != SQLiteConnectionFlags.NoLogModule)
        {
            module.LogErrors = ((flags & SQLiteConnectionFlags.LogModuleError) == SQLiteConnectionFlags.LogModuleError);
            module.LogExceptions = ((flags & SQLiteConnectionFlags.LogModuleException) == SQLiteConnectionFlags.LogModuleException);
        }

        if (_sql == null)
            throw new SQLiteException("connection has an invalid handle");

        SetLoadExtension(true);
        LoadExtension(UnsafeNativeMethods.SQLITE_DLL, "sqlite3_vtshim_init");

        if (module.CreateDisposableModule(_sql))
        {
            if (_modules == null)
                _modules = new Dictionary<string, SQLiteModule>();

            _modules.Add(module.Name, module);

            if (_usePool)
            {
                _usePool = false;

#if !NET_COMPACT_20 && TRACE_CONNECTION
                Trace.WriteLine(String.Format("CreateModule (Pool) Disabled: {0}", _sql));
#endif
            }
        }
        else
        {
            throw new SQLiteException(GetLastError());
        }
    }

    /// <summary>
    /// Calls the native SQLite core library in order to cleanup the resources
    /// associated with a module containing the implementation of a virtual table.
    /// </summary>
    /// <param name="module">
    /// The module object previously passed to the <see cref="CreateModule" />
    /// method.
    /// </param>
    /// <param name="flags">
    /// The flags for the associated <see cref="SQLiteConnection" /> object instance.
    /// </param>
    internal override void DisposeModule(SQLiteModule module, SQLiteConnectionFlags flags)
    {
        if (module == null)
            throw new ArgumentNullException("module");

        module.Dispose();
    }
#endif

    internal override IntPtr AggregateContext(IntPtr context)
    {
      return UnsafeNativeMethods.sqlite3_aggregate_context(context, 1);
    }

#if INTEROP_VIRTUAL_TABLE
    /// <summary>
    /// Calls the native SQLite core library in order to declare a virtual table
    /// in response to a call into the <see cref="ISQLiteNativeModule.xCreate" />
    /// or <see cref="ISQLiteNativeModule.xConnect" /> virtual table methods.
    /// </summary>
    /// <param name="module">
    /// The virtual table module that is to be responsible for the virtual table
    /// being declared.
    /// </param>
    /// <param name="strSql">
    /// The string containing the SQL statement describing the virtual table to
    /// be declared.
    /// </param>
    /// <param name="error">
    /// Upon success, the contents of this parameter are undefined.  Upon failure,
    /// it should contain an appropriate error message.
    /// </param>
    /// <returns>
    /// A standard SQLite return code.
    /// </returns>
    internal override SQLiteErrorCode DeclareVirtualTable(
        SQLiteModule module,
        string strSql,
        ref string error
        )
    {
        if (_sql == null)
        {
            error = "connection has an invalid handle";
            return SQLiteErrorCode.Error;
        }

        IntPtr pSql = IntPtr.Zero;

        try
        {
            pSql = SQLiteString.Utf8IntPtrFromString(strSql);

            SQLiteErrorCode n = UnsafeNativeMethods.sqlite3_declare_vtab(
                _sql, pSql);

            if ((n == SQLiteErrorCode.Ok) && (module != null))
                module.Declared = true;

            if (n != SQLiteErrorCode.Ok) error = GetLastError();

            return n;
        }
        finally
        {
            if (pSql != IntPtr.Zero)
            {
                SQLiteMemory.Free(pSql);
                pSql = IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// Calls the native SQLite core library in order to declare a virtual table
    /// function in response to a call into the <see cref="ISQLiteNativeModule.xCreate" />
    /// or <see cref="ISQLiteNativeModule.xConnect" /> virtual table methods.
    /// </summary>
    /// <param name="module">
    /// The virtual table module that is to be responsible for the virtual table
    /// function being declared.
    /// </param>
    /// <param name="argumentCount">
    /// The number of arguments to the function being declared.
    /// </param>
    /// <param name="name">
    /// The name of the function being declared.
    /// </param>
    /// <param name="error">
    /// Upon success, the contents of this parameter are undefined.  Upon failure,
    /// it should contain an appropriate error message.
    /// </param>
    /// <returns>
    /// A standard SQLite return code.
    /// </returns>
    internal override SQLiteErrorCode DeclareVirtualFunction(
        SQLiteModule module,
        int argumentCount,
        string name,
        ref string error
        )
    {
        if (_sql == null)
        {
            error = "connection has an invalid handle";
            return SQLiteErrorCode.Error;
        }

        IntPtr pName = IntPtr.Zero;

        try
        {
            pName = SQLiteString.Utf8IntPtrFromString(name);

            SQLiteErrorCode n = UnsafeNativeMethods.sqlite3_overload_function(
                _sql, pName, argumentCount);

            if (n != SQLiteErrorCode.Ok) error = GetLastError();

            return n;
        }
        finally
        {
            if (pName != IntPtr.Zero)
            {
                SQLiteMemory.Free(pName);
                pName = IntPtr.Zero;
            }
        }
    }
#endif

    /// <summary>
    /// Enables or disabled extension loading by SQLite.
    /// </summary>
    /// <param name="bOnOff">
    /// True to enable loading of extensions, false to disable.
    /// </param>
    internal override void SetLoadExtension(bool bOnOff)
    {
        SQLiteErrorCode n = UnsafeNativeMethods.sqlite3_enable_load_extension(
            _sql, (bOnOff ? -1 : 0));

        if (n != SQLiteErrorCode.Ok) throw new SQLiteException(n, GetLastError());
    }

    /// <summary>
    /// Loads a SQLite extension library from the named file.
    /// </summary>
    /// <param name="fileName">
    /// The name of the dynamic link library file containing the extension.
    /// </param>
    /// <param name="procName">
    /// The name of the exported function used to initialize the extension.
    /// If null, the default "sqlite3_extension_init" will be used.
    /// </param>
    internal override void LoadExtension(string fileName, string procName)
    {
        if (fileName == null)
            throw new ArgumentNullException("fileName");

        IntPtr pError = IntPtr.Zero;

        try
        {
            byte[] utf8FileName = UTF8Encoding.UTF8.GetBytes(fileName + '\0');
            byte[] utf8ProcName = null;

            if (procName != null)
                utf8ProcName = UTF8Encoding.UTF8.GetBytes(procName + '\0');

            SQLiteErrorCode n = UnsafeNativeMethods.sqlite3_load_extension(
                _sql, utf8FileName, utf8ProcName, ref pError);

            if (n != SQLiteErrorCode.Ok)
                throw new SQLiteException(n, UTF8ToString(pError, -1));
        }
        finally
        {
            if (pError != IntPtr.Zero)
            {
                UnsafeNativeMethods.sqlite3_free(pError);
                pError = IntPtr.Zero;
            }
        }
    }

    /// Enables or disabled extended result codes returned by SQLite
    internal override void SetExtendedResultCodes(bool bOnOff)
    {
      SQLiteErrorCode n = UnsafeNativeMethods.sqlite3_extended_result_codes(
          _sql, (bOnOff ? -1 : 0));

      if (n != SQLiteErrorCode.Ok) throw new SQLiteException(n, GetLastError());
    }
    /// Gets the last SQLite error code
    internal override SQLiteErrorCode ResultCode()
    {
      return UnsafeNativeMethods.sqlite3_errcode(_sql);
    }
    /// Gets the last SQLite extended error code
    internal override SQLiteErrorCode ExtendedResultCode()
    {
      return UnsafeNativeMethods.sqlite3_extended_errcode(_sql);
    }

    /// Add a log message via the SQLite sqlite3_log interface.
    internal override void LogMessage(SQLiteErrorCode iErrCode, string zMessage)
    {
      StaticLogMessage(iErrCode, zMessage);
    }

    /// Add a log message via the SQLite sqlite3_log interface.
    internal static void StaticLogMessage(SQLiteErrorCode iErrCode, string zMessage)
    {
      UnsafeNativeMethods.sqlite3_log(iErrCode, ToUTF8(zMessage));
    }

#if INTEROP_CODEC
    internal override void SetPassword(byte[] passwordBytes)
    {
      SQLiteErrorCode n = UnsafeNativeMethods.sqlite3_key(_sql, passwordBytes, passwordBytes.Length);
      if (n != SQLiteErrorCode.Ok) throw new SQLiteException(n, GetLastError());
    }

    internal override void ChangePassword(byte[] newPasswordBytes)
    {
      SQLiteErrorCode n = UnsafeNativeMethods.sqlite3_rekey(_sql, newPasswordBytes, (newPasswordBytes == null) ? 0 : newPasswordBytes.Length);
      if (n != SQLiteErrorCode.Ok) throw new SQLiteException(n, GetLastError());
    }
#endif

    internal override void SetAuthorizerHook(SQLiteAuthorizerCallback func)
    {
      UnsafeNativeMethods.sqlite3_set_authorizer(_sql, func, IntPtr.Zero);
    }

    internal override void SetUpdateHook(SQLiteUpdateCallback func)
    {
      UnsafeNativeMethods.sqlite3_update_hook(_sql, func, IntPtr.Zero);
    }

    internal override void SetCommitHook(SQLiteCommitCallback func)
    {
      UnsafeNativeMethods.sqlite3_commit_hook(_sql, func, IntPtr.Zero);
    }

    internal override void SetTraceCallback(SQLiteTraceCallback func)
    {
      UnsafeNativeMethods.sqlite3_trace(_sql, func, IntPtr.Zero);
    }

    internal override void SetRollbackHook(SQLiteRollbackCallback func)
    {
      UnsafeNativeMethods.sqlite3_rollback_hook(_sql, func, IntPtr.Zero);
    }

    /// <summary>
    /// Allows the setting of a logging callback invoked by SQLite when a
    /// log event occurs.  Only one callback may be set.  If NULL is passed,
    /// the logging callback is unregistered.
    /// </summary>
    /// <param name="func">The callback function to invoke.</param>
    /// <returns>Returns a result code</returns>
    internal override SQLiteErrorCode SetLogCallback(SQLiteLogCallback func)
    {
        SQLiteErrorCode rc = UnsafeNativeMethods.sqlite3_config_log(
            SQLiteConfigOpsEnum.SQLITE_CONFIG_LOG, func, IntPtr.Zero);

        return rc;
    }

    ///////////////////////////////////////////////////////////////////////////////////////////////

    /// <summary>
    /// Creates a new SQLite backup object based on the provided destination
    /// database connection.  The source database connection is the one
    /// associated with this object.  The source and destination database
    /// connections cannot be the same.
    /// </summary>
    /// <param name="destCnn">The destination database connection.</param>
    /// <param name="destName">The destination database name.</param>
    /// <param name="sourceName">The source database name.</param>
    /// <returns>The newly created backup object.</returns>
    internal override SQLiteBackup InitializeBackup(
        SQLiteConnection destCnn,
        string destName,
        string sourceName
        )
    {
        if (destCnn == null)
            throw new ArgumentNullException("destCnn");

        if (destName == null)
            throw new ArgumentNullException("destName");

        if (sourceName == null)
            throw new ArgumentNullException("sourceName");

        SQLite3 destSqlite3 = destCnn._sql as SQLite3;

        if (destSqlite3 == null)
            throw new ArgumentException(
                "Destination connection has no wrapper.",
                "destCnn");

        SQLiteConnectionHandle destHandle = destSqlite3._sql;

        if (destHandle == null)
            throw new ArgumentException(
                "Destination connection has an invalid handle.",
                "destCnn");

        SQLiteConnectionHandle sourceHandle = _sql;

        if (sourceHandle == null)
            throw new InvalidOperationException(
                "Source connection has an invalid handle.");

        byte[] zDestName = ToUTF8(destName);
        byte[] zSourceName = ToUTF8(sourceName);

        SQLiteBackupHandle backupHandle = null;

        try
        {
            // do nothing.
        }
        finally /* NOTE: Thread.Abort() protection. */
        {
            IntPtr backup = UnsafeNativeMethods.sqlite3_backup_init(
                destHandle, zDestName, sourceHandle, zSourceName);

            if (backup == IntPtr.Zero)
            {
                SQLiteErrorCode resultCode = ResultCode();

                if (resultCode != SQLiteErrorCode.Ok)
                    throw new SQLiteException(resultCode, GetLastError());
                else
                    throw new SQLiteException("failed to initialize backup");
            }

            backupHandle = new SQLiteBackupHandle(destHandle, backup);
        }

        SQLiteConnection.OnChanged(null, new ConnectionEventArgs(
            SQLiteConnectionEventType.NewCriticalHandle, null, null,
            null, null, backupHandle, null, new object[] { destCnn,
            destName, sourceName }));

        return new SQLiteBackup(
            this, backupHandle, destHandle, zDestName, sourceHandle,
            zSourceName);
    }

    /// <summary>
    /// Copies up to N pages from the source database to the destination
    /// database associated with the specified backup object.
    /// </summary>
    /// <param name="backup">The backup object to use.</param>
    /// <param name="nPage">
    /// The number of pages to copy, negative to copy all remaining pages.
    /// </param>
    /// <param name="retry">
    /// Set to true if the operation needs to be retried due to database
    /// locking issues; otherwise, set to false.
    /// </param>
    /// <returns>
    /// True if there are more pages to be copied, false otherwise.
    /// </returns>
    internal override bool StepBackup(
        SQLiteBackup backup,
        int nPage,
        out bool retry
        )
    {
        retry = false;

        if (backup == null)
            throw new ArgumentNullException("backup");

        SQLiteBackupHandle handle = backup._sqlite_backup;

        if (handle == null)
            throw new InvalidOperationException(
                "Backup object has an invalid handle.");

        IntPtr handlePtr = handle;

        if (handlePtr == IntPtr.Zero)
            throw new InvalidOperationException(
                "Backup object has an invalid handle pointer.");

        SQLiteErrorCode n = UnsafeNativeMethods.sqlite3_backup_step(handlePtr, nPage);
        backup._stepResult = n; /* NOTE: Save for use by FinishBackup. */

        if (n == SQLiteErrorCode.Ok)
        {
            return true;
        }
        else if (n == SQLiteErrorCode.Busy)
        {
            retry = true;
            return true;
        }
        else if (n == SQLiteErrorCode.Locked)
        {
            retry = true;
            return true;
        }
        else if (n == SQLiteErrorCode.Done)
        {
            return false;
        }
        else
        {
            throw new SQLiteException(n, GetLastError());
        }
    }

    /// <summary>
    /// Returns the number of pages remaining to be copied from the source
    /// database to the destination database associated with the specified
    /// backup object.
    /// </summary>
    /// <param name="backup">The backup object to check.</param>
    /// <returns>The number of pages remaining to be copied.</returns>
    internal override int RemainingBackup(
        SQLiteBackup backup
        )
    {
        if (backup == null)
            throw new ArgumentNullException("backup");

        SQLiteBackupHandle handle = backup._sqlite_backup;

        if (handle == null)
            throw new InvalidOperationException(
                "Backup object has an invalid handle.");

        IntPtr handlePtr = handle;

        if (handlePtr == IntPtr.Zero)
            throw new InvalidOperationException(
                "Backup object has an invalid handle pointer.");

        return UnsafeNativeMethods.sqlite3_backup_remaining(handlePtr);
    }

    /// <summary>
    /// Returns the total number of pages in the source database associated
    /// with the specified backup object.
    /// </summary>
    /// <param name="backup">The backup object to check.</param>
    /// <returns>The total number of pages in the source database.</returns>
    internal override int PageCountBackup(
        SQLiteBackup backup
        )
    {
        if (backup == null)
            throw new ArgumentNullException("backup");

        SQLiteBackupHandle handle = backup._sqlite_backup;

        if (handle == null)
            throw new InvalidOperationException(
                "Backup object has an invalid handle.");

        IntPtr handlePtr = handle;

        if (handlePtr == IntPtr.Zero)
            throw new InvalidOperationException(
                "Backup object has an invalid handle pointer.");

        return UnsafeNativeMethods.sqlite3_backup_pagecount(handlePtr);
    }

    /// <summary>
    /// Destroys the backup object, rolling back any backup that may be in
    /// progess.
    /// </summary>
    /// <param name="backup">The backup object to destroy.</param>
    internal override void FinishBackup(
        SQLiteBackup backup
        )
    {
        if (backup == null)
            throw new ArgumentNullException("backup");

        SQLiteBackupHandle handle = backup._sqlite_backup;

        if (handle == null)
            throw new InvalidOperationException(
                "Backup object has an invalid handle.");

        IntPtr handlePtr = handle;

        if (handlePtr == IntPtr.Zero)
            throw new InvalidOperationException(
                "Backup object has an invalid handle pointer.");

#if !SQLITE_STANDARD
        SQLiteErrorCode n = UnsafeNativeMethods.sqlite3_backup_finish_interop(handlePtr);
#else
        SQLiteErrorCode n = UnsafeNativeMethods.sqlite3_backup_finish(handlePtr);
#endif
        handle.SetHandleAsInvalid();

#if COUNT_HANDLE
        if ((n == SQLiteErrorCode.Ok) || (n == backup._stepResult)) handle.WasReleasedOk();
#endif

        if ((n != SQLiteErrorCode.Ok) && (n != backup._stepResult))
            throw new SQLiteException(n, GetLastError());
    }

    ///////////////////////////////////////////////////////////////////////////////////////////////

    /// <summary>
    /// Determines if the SQLite core library has been initialized for the
    /// current process.
    /// </summary>
    /// <returns>
    /// A boolean indicating whether or not the SQLite core library has been
    /// initialized for the current process.
    /// </returns>
    internal override bool IsInitialized()
    {
        return StaticIsInitialized();
    }

    /// <summary>
    /// Determines if the SQLite core library has been initialized for the
    /// current process.
    /// </summary>
    /// <returns>
    /// A boolean indicating whether or not the SQLite core library has been
    /// initialized for the current process.
    /// </returns>
    internal static bool StaticIsInitialized()
    {
        //
        // BUGFIX: Prevent races with other threads for this entire block, due
        //         to the try/finally semantics.  See ticket [72905c9a77].
        //
        lock (syncRoot)
        {
            //
            // NOTE: Save the state of the logging class and then restore it
            //       after we are done to avoid logging too many false errors.
            //
            bool savedEnabled = SQLiteLog.Enabled;
            SQLiteLog.Enabled = false;

            try
            {
                //
                // NOTE: This method [ab]uses the fact that SQLite will always
                //       return SQLITE_ERROR for any unknown configuration option
                //       *unless* the SQLite library has already been initialized.
                //       In that case it will always return SQLITE_MISUSE.
                //
                SQLiteErrorCode rc = UnsafeNativeMethods.sqlite3_config_none(
                    SQLiteConfigOpsEnum.SQLITE_CONFIG_NONE);

                return (rc == SQLiteErrorCode.Misuse);
            }
            finally
            {
                SQLiteLog.Enabled = savedEnabled;
            }
        }
    }

    /// <summary>
    /// Helper function to retrieve a column of data from an active statement.
    /// </summary>
    /// <param name="stmt">The statement being step()'d through</param>
    /// <param name="flags">The flags associated with the connection.</param>
    /// <param name="index">The column index to retrieve</param>
    /// <param name="typ">The type of data contained in the column.  If Uninitialized, this function will retrieve the datatype information.</param>
    /// <returns>Returns the data in the column</returns>
    internal override object GetValue(SQLiteStatement stmt, SQLiteConnectionFlags flags, int index, SQLiteType typ)
    {
      TypeAffinity aff = typ.Affinity;
      if (aff == TypeAffinity.Null) return DBNull.Value;
      Type t = null;

      if (typ.Type != DbType.Object)
      {
        t = SQLiteConvert.SQLiteTypeToType(typ);
        aff = TypeToAffinity(t);
      }

      if ((flags & SQLiteConnectionFlags.GetAllAsText) == SQLiteConnectionFlags.GetAllAsText)
          return GetText(stmt, index);

      switch (aff)
      {
        case TypeAffinity.Blob:
          if (typ.Type == DbType.Guid && typ.Affinity == TypeAffinity.Text)
            return new Guid(GetText(stmt, index));

          int n = (int)GetBytes(stmt, index, 0, null, 0, 0);
          byte[] b = new byte[n];
          GetBytes(stmt, index, 0, b, 0, n);

          if (typ.Type == DbType.Guid && n == 16)
            return new Guid(b);

          return b;
        case TypeAffinity.DateTime:
          return GetDateTime(stmt, index);
        case TypeAffinity.Double:
          if (t == null) return GetDouble(stmt, index);
          return Convert.ChangeType(GetDouble(stmt, index), t, null);
        case TypeAffinity.Int64:
          if (t == null) return GetInt64(stmt, index);
          if (t == typeof(SByte)) return GetSByte(stmt, index);
          if (t == typeof(Byte)) return GetByte(stmt, index);
          if (t == typeof(Int16)) return GetInt16(stmt, index);
          if (t == typeof(UInt16)) return GetUInt16(stmt, index);
          if (t == typeof(Int32)) return GetInt32(stmt, index);
          if (t == typeof(UInt32)) return GetUInt32(stmt, index);
          if (t == typeof(UInt64)) return GetUInt64(stmt, index);
          return Convert.ChangeType(GetInt64(stmt, index), t, null);
        default:
          return GetText(stmt, index);
      }
    }

    internal override int GetCursorForTable(SQLiteStatement stmt, int db, int rootPage)
    {
#if !SQLITE_STANDARD
      return UnsafeNativeMethods.sqlite3_table_cursor_interop(stmt._sqlite_stmt, db, rootPage);
#else
      return -1;
#endif
    }

    internal override long GetRowIdForCursor(SQLiteStatement stmt, int cursor)
    {
#if !SQLITE_STANDARD
      long rowid;
      SQLiteErrorCode rc = UnsafeNativeMethods.sqlite3_cursor_rowid_interop(stmt._sqlite_stmt, cursor, out rowid);
      if (rc == SQLiteErrorCode.Ok) return rowid;

      return 0;
#else
      return 0;
#endif
    }

    internal override void GetIndexColumnExtendedInfo(string database, string index, string column, out int sortMode, out int onError, out string collationSequence)
    {
#if !SQLITE_STANDARD
      IntPtr coll;
      int colllen;
      SQLiteErrorCode rc;

      rc = UnsafeNativeMethods.sqlite3_index_column_info_interop(_sql, ToUTF8(database), ToUTF8(index), ToUTF8(column), out sortMode, out onError, out coll, out colllen);
      if (rc != SQLiteErrorCode.Ok) throw new SQLiteException(rc, null);

      collationSequence = UTF8ToString(coll, colllen);
#else
      sortMode = 0;
      onError = 2;
      collationSequence = "BINARY";
#endif
    }

    internal override SQLiteErrorCode FileControl(string zDbName, int op, IntPtr pArg)
    {
      return UnsafeNativeMethods.sqlite3_file_control(_sql, (zDbName != null) ? ToUTF8(zDbName) : null, op, pArg);
    }
  }
}
