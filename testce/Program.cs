/********************************************************
 * ADO.NET 2.0 Data Provider for SQLite Version 3.X
 * Written by Robert Simpson (robert@blackcastlesoft.com)
 * 
 * Released to the public domain, use at your own risk!
 ********************************************************/

using System;
using System.Data.Common;
using System.Data.SQLite;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace test
{
  class Program
  {
    private static readonly string DefaultConnectionString =
        "Data Source={DataDirectory}\\test.db;Password=yVXL39etehPX;";

    [MTAThread]
    static void Main()
    {
      Assembly assembly = Assembly.GetExecutingAssembly();
      AssemblyName assemblyName = assembly.GetName();
      string directory = Path.GetDirectoryName(assemblyName.CodeBase);

      try { File.Delete(directory + "\\test.db"); } catch { }

      SQLiteFunction.RegisterFunction(typeof(TestFunc));
      SQLiteFunction.RegisterFunction(typeof(MyCount));
      SQLiteFunction.RegisterFunction(typeof(MySequence));

      using (DbConnection cnn = new SQLiteConnection())
      {
        string connectionString = DefaultConnectionString;

        try
        {
          //
          // NOTE: Attempt to open the configuration file associated with
          //       this test executable.  It should contain *EXACTLY* one
          //       line, which will be the connection string to use for
          //       this test run.
          //
          using (StreamReader streamReader = File.OpenText(
              directory + "\\test.cfg"))
          {
            connectionString = streamReader.ReadToEnd().Trim();
          }
        }
        catch
        {
          // do nothing.
        }

        //
        // NOTE: If we are unable to obtain a valid connection string
        //       bail out now.
        //
        if (connectionString != null)
        {
          //
          // NOTE: Replace the "{DataDirectory}" token, if any, in the
          //       connection string with the actual directory this test
          //       assembly is executing from.
          //
          connectionString = connectionString.Replace(
            "{DataDirectory}", directory);

          cnn.ConnectionString = connectionString;
          cnn.Open();

          TestCases tests = new TestCases();

          tests.Run(cnn);

          Application.Run(tests.frm);
        }
      }
    }
  }
}
