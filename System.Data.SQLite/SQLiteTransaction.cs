﻿/********************************************************
 * ADO.NET 2.0 Data Provider for SQLite Version 3.X
 * Written by Robert Simpson (robert@blackcastlesoft.com)
 * 
 * Released to the public domain, use at your own risk!
 ********************************************************/

namespace System.Data.SQLite
{
  using System;
  using System.Data;
  using System.Data.Common;

  /// <summary>
  /// SQLite implementation of DbTransaction.
  /// </summary>
  public sealed class SQLiteTransaction : DbTransaction
  {
    /// <summary>
    /// The connection to which this transaction is bound
    /// </summary>
    internal SQLiteConnection _cnn;

    /// <summary>
    /// Constructs the transaction object, binding it to the supplied connection
    /// </summary>
    /// <param name="cnn">The connection to open a transaction on</param>
    internal SQLiteTransaction(SQLiteConnection cnn)
    {
      try
      {
        cnn._sql.Execute("BEGIN IMMEDIATE");
        _cnn = cnn;
      }
      catch (SQLiteException)
      {
        BaseDispose();
        throw;
      }
    }

    /// <summary>
    /// Commits the current transaction.
    /// </summary>
    public override void Commit()
    {
      if (_cnn == null)
        throw new ArgumentNullException();

      try
      {
        _cnn._sql.Execute("COMMIT");
      }
      catch (SQLiteException)
      {
        BaseDispose();
        throw;
      }
      BaseDispose();
    }

    /// <summary>
    /// Returns the underlying connection to which this transaction applies.
    /// </summary>
    public new SQLiteConnection Connection
    {
      get { return _cnn; }
    }

    /// <summary>
    /// Forwards to the local Connection property
    /// </summary>
    protected override DbConnection DbConnection
    {
      get { return Connection; }
    }

    /// <summary>
    /// Disposes the transaction.  If it is currently active, any changes are rolled back.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
      if (_cnn != null) 
        Rollback();

      _cnn = null;

      base.Dispose(disposing);
    }

    /// <summary>
    /// Gets the isolation level of the transaction.  SQLite does not support isolation levels, so this always returns Unspecified.
    /// </summary>
    public override IsolationLevel IsolationLevel
    {
      get { return IsolationLevel.Unspecified; }
    }

    /// <summary>
    /// Rolls back the active transaction.
    /// </summary>
    public override void Rollback()
    {
      if (_cnn == null)
        throw new ArgumentNullException();

      try
      {
        _cnn._sql.Execute("ROLLBACK");
      }
      catch (SQLiteException)
      {
        BaseDispose();
        throw;
      }
      BaseDispose();
    }

    private void BaseDispose()
    {
      _cnn._activeTransaction = null;
      _cnn = null;
    }
  }
}
