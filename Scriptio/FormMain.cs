///////////////////////////////////////////////////////////////////////////////////////////////
//  Scriptio - Script SQL Server 2005 objects
//  Copyright (C) 2005 Bill Graziano and 2008 Riccardo Spagni

//  This program is free software; you can redistribute it and/or modify
//  it under the terms of the GNU General Public License as published by
//  the Free Software Foundation; either version 2 of the License, or
//  (at your option) any later version.

//  This program is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//  GNU General Public License for more details.

//  You should have received a copy of the GNU General Public License
//  along with this program; if not, write to the Free Software
//  Foundation, Inc., 51 Franklin St, Fifth Floor, Boston, MA  02110-1301  USA
///////////////////////////////////////////////////////////////////////////////////////////////

// Please note: comments marked RS are for code pieces added by Riccardo Spagni

// TODO: (ie. optional extras:)
//
// - NB: Check if this works with SQL 2008 (Katmai)
//
// - NB: Find a way of integrating with SQL 2005's context menu; maybe have Scriptio as a contex menu item on a
//   database level, and then launch Scriptio with the authentication items already filled in, the database list
//   populated with a single item (the database we've right clicked, obviously) and grayed out, and fire off the
//   PopulateDatabases method. Also, see if we can't integrate it with SQL 2008 as well whilst we're about it.
//
// - Build intelligence into the selection system, so that when you select an item in the DataGrid it checks if
//   all Stored Procs, for example, have been manually ticked in the DataGrid, and then reflects that in the
//   CheckedListBox by ticking the StoredProc's item.
//
// - "Beautify" the items in clbType, so that it puts a space between capitalised words - ie. when it adds
//   UserDefinedTypes to the clbType items collection, it should add it as "User Defined Types". Beautify the
//   description in the DataGrid too so that they match.
//
// - Add other encoding options (UTF-7, UTF-8, UTF-32, BigEndianUnicode), dependant on demand
//
// - Make sure we've exposed ALL relevant ScriptingOptions properties to the user, you never know what oddity might
//   be required by a single person in a single environment

using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Sdk.Sfc;
using Microsoft.SqlServer.Management.Smo;
using System;
using System.Collections.Specialized;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Scriptio
{
  public partial class Scriptio : Form
  {
    // RS: It's better to use StringBuilder to build our script than string concatenation, as the StringBuilder
    // object is more efficient. When you concatenate a string, the original string is actually destroyed on
    // the heap and a new one is created.
    private StringBuilder result;

    // RS: This is the main DataTable that contains ALL of the objects on the database, system and user
    // supplied
    private DataTable allobjects = new DataTable();

    // RS: We use a DataView with a custom RowFilter to ensure that we exclude system supplied objects from
    // the DataGrid that the user will use to select the objects they want to script
    private DataView allobjectsview;

    public Scriptio()
    {
      InitializeComponent();

      aboutToolStripMenuItem.Click += new EventHandler(AboutToolStripMenuItem_Click);
      exitToolStripMenuItem.Click += new EventHandler(ExitToolStripMenuItem_Click);
    }

    #region Menu Stuff

    private void ExitToolStripMenuItem_Click(object sender, EventArgs e)
    {
      Close();
    }

    private void AboutToolStripMenuItem_Click(object sender, EventArgs e)
    {
      AboutForm aboutForm = new AboutForm();
      aboutForm.ShowDialog();
    }

    #endregion Menu Stuff

    private void BtnCancel_Click(object sender, EventArgs e)
    {
      Close();
    }

    private void BtnConnect_Click(object sender, EventArgs e)
    {
      PopulateDatabases(txtServerName.Text);
    }

    private void PopulateDatabases(string serverName)
    {
      // RS: Tell the user what we're doing when we do it
      toolStripStatusLabel1.Text = "Reading databases...";
      toolStripProgressBar1.Value = 0;
      Application.DoEvents();
      ddlDatabases.Items.Clear();
      try
      {
        SqlConnection connection = GetConnection("tempdb");
        //Server srv = new Server(new ServerConnection(connection));
        Server srv = new Server(serverName);

        // Check if we're using 2005 or higher
        if (srv.Information.Version.Major >= 9)
        {
          // RS: Set the progress bar maximum so we can update it as we step through
          toolStripProgressBar1.Maximum = srv.Databases.Count;

          ddlDatabases.Items.Add("(Select a database)");

          foreach (Database db in srv.Databases)
          {
            // RS: Update the progress bar
            toolStripProgressBar1.Value++;
            if (!db.IsSystemObject && db.IsAccessible)
            {
              ddlDatabases.Items.Add(db.Name);
            }
          }

          if (ddlDatabases.Items.Count > 0)
          {
            ddlDatabases.SelectedIndex = 0;
          }
        }
        else
        {
          MessageBox.Show("SMO Scripting is only available for SQL Server 2005 and higher",
              "Incorrect Server Version", MessageBoxButtons.OK, MessageBoxIcon.Error);

          dgAvailableObjects.Rows.Clear();
          chkScriptAll.Checked = false;
          ddlDatabases.ResetText();
        }
      }
      catch (ConnectionFailureException)
      {
        MessageBox.Show("Unable to connect to server",
                "Invalid Server", MessageBoxButtons.OK, MessageBoxIcon.Error);
      }

      // RS: Reset the label text
      toolStripStatusLabel1.Text = "Ready";
    }

    private void PopulateObjects(string serverName, string databaseName)
    {
      // RS: This has been completely overhauled and replaced. Instead of querying the sys.all_objects,
      // sys.schemas and sys.assemblies tables we step through our SMO database object. This is efficient
      // enough - even on large databases - but it allows us to use the Scripter object to script objects
      // based on the URN returned.
      Server srv;

      if (chkUseWindowsAuthentication.Checked)
      {
        srv = new Server(txtServerName.Text);
      }
      else
      {
        srv = new Server(new ServerConnection(txtServerName.Text, txtUsername.Text, txtPassword.Text));
      }

      Database db = srv.Databases[ddlDatabases.SelectedItem.ToString()];

      // RS: Set the DataTable up so we're all ready to add data to it
      allobjects = null;
      allobjects = new DataTable();
      allobjects.Columns.Add("Script", typeof(bool));
      allobjects.Columns.Add("Schema", typeof(string));
      allobjects.Columns.Add("Object", typeof(string));
      allobjects.Columns.Add("Type", typeof(string));
      allobjects.Columns.Add("URN", typeof(string));

      // RS: Set a temporary DataTable up so we can plug the EnumObjects stuff into it. There is a method
      // behind this madness - we can use the Count property of the allobjectsenum DataTable to get a total
      // size of the objects in our database.
      DataTable allobjectsenum = new DataTable();
      toolStripStatusLabel1.Text = "Enumerating objects in database...";
      Application.DoEvents();
      allobjectsenum = db.EnumObjects();

      toolStripProgressBar1.Value = 0;
      toolStripProgressBar1.Maximum = allobjectsenum.Rows.Count;
      toolStripStatusLabel1.Text = "Reading primary objects...";
      Application.DoEvents();

      // RS: Clear the filter items.
      chkScriptAll.Checked = false;
      clbSchema.Items.Clear();
      clbType.Items.Clear();

      // RS: Step through our enumerated DataTable and add them to a final DataTable. There's probably a
      // better way of doing this, but I like the fact that this updated the progress bar as it keeps the
      // user notified that things are happening.
      foreach (DataRow dataRow in allobjectsenum.Rows)
      {
        toolStripProgressBar1.Value++;
        allobjects.Rows.Add(new object[] { false, dataRow[1].ToString(), dataRow[2].ToString(), dataRow[0].ToString(), dataRow[3].ToString() });
        // RS: This is a little chunky, I'm open to suggestions in terms of making it more efficient. All
        // it does is populate the clbSchema listbox with unique schema values that aren't system schemas.
        if ((!clbSchema.Items.Contains(dataRow[1].ToString())) && (dataRow[1].ToString() != "" && dataRow[1].ToString() != "sys" && dataRow[1].ToString() != "INFORMATION_SCHEMA"))
        {
          clbSchema.Items.Add(dataRow[1].ToString());
        }

        Application.DoEvents();
      }

      // RS: Disable the schema list if it's empty
      if (clbSchema.Items.Count > 0)
      {
        clbSchema.Enabled = true;
      }
      else
      {
        clbSchema.Enabled = false;
      }

      // RS: Update the user
      toolStripProgressBar1.Value = 0;
      toolStripStatusLabel1.Text = "Reading secondary objects...";
      Application.DoEvents();

      // RS: This is icky, but the only way. Step through triggers on a per table basis and add it to the
      // DataTable. NOTE: We don't need to update the clbSchema list box, as the schema that the table
      // that "owns" the trigger is inferred on the trigger. I think. Does that make sense to anyone besides
      // me?
      foreach (Table tbl in db.Tables)
      {
        foreach (Trigger trg in tbl.Triggers)
        {
          allobjects.Rows.Add(new object[] { false, tbl.Schema.ToString(), trg.Name.ToString(), "Trigger", trg.Urn.ToString() });
        }
      }

      // RS: Set the DataView up based on the allobjects DataTable
      allobjectsview = new DataView(allobjects);

      // RS: Large databases can take a few seconds to create the DataView, tell the user what we're doing
      toolStripStatusLabel1.Text = "Filtering and sorting...";
      Application.DoEvents();

      // RS: Filter our system objects
      allobjectsview.RowFilter = "([Schema] <> 'INFORMATION_SCHEMA') AND ([Schema] <> 'sys') AND" +
                                  "([Type] IN ('ExtendedStoredProcedure', 'PartitionFunction', 'PartitionScheme', " +
                                  "'SqlAssembly', 'StoredProcedure', 'Table', 'Trigger', 'UserDefinedAggregate', " +
                                  "'UserDefinedDataType', 'UserDefinedFunction', 'UserDefinedType', 'View'))";
      // RS: Sort by the type of object we're dealing with
      allobjectsview.Sort = "[Type]";

      // RS: This is an optional RowFilter that has Users and Schema's in it - but you end up with stuff
      // that is Microsoft supplied, and it's really not worth our while adding an entire section that
      // iterates through objects and checks their IsSystemSupplied property - because then we'd also need
      // to ensure that IsSystemSupplied is part of the default properties that SMO fetches - an altogether
      // unnecessary exercise, and one that would noticeably slow SMO down.

      /* allobjects.RowFilter =  "([Schema] <> 'INFORMATION_SCHEMA') AND ([Schema] <> 'sys') AND" +
                              "([Type] IN ('ExtendedStoredProcedure', 'PartitionFunction', 'PartitionScheme', 'Trigger', " +
                              "'Schema', 'SqlAssembly', 'StoredProcedure', 'Table', 'User', 'UserDefinedAggregate', " +
                              "'UserDefinedDataType', 'UserDefinedFunction', 'UserDefinedType', 'View'))";*/

      dgAvailableObjects.DataSource = allobjectsview;

      foreach (DataGridViewRow dgr in dgAvailableObjects.Rows)
      {
        // RS: Some more chunky clbType populating code...saves us the hassle of having to add it when
        // we're populating, as we don't know whether or not we have triggers until later.
        if ((!clbType.Items.Contains(dgr.Cells[3].Value.ToString())) && (dgr.Cells[3].Value.ToString() != string.Empty))
        {
          clbType.Items.Add(dgr.Cells[3].Value.ToString());
        }
      }

      // RS: Disable the Type list if it's empty
      if (clbType.Items.Count > 0)
      {
        clbType.Enabled = true;
      }
      else
      {
        clbType.Enabled = false;
      }

      // RS: Hide the URN from the DataGrid
      dgAvailableObjects.Columns[4].Visible = false;
      // RS: Set column widths on the DataGrid
      dgAvailableObjects.Columns[0].Width = 50;
      dgAvailableObjects.Columns[1].Width = 130;
      dgAvailableObjects.Columns[2].Width = 316;
      dgAvailableObjects.Columns[3].Width = 200;
      toolStripStatusLabel1.Text = "Ready";
    }

    private SqlConnection GetConnection(string databaseName)
    {
      SqlConnectionStringBuilder sqlConnectionStringBuilder = new SqlConnectionStringBuilder();
      sqlConnectionStringBuilder.DataSource = txtServerName.Text;
      if (chkUseWindowsAuthentication.Checked)
      {
        sqlConnectionStringBuilder.IntegratedSecurity = true;
      }
      else
      {
        sqlConnectionStringBuilder.IntegratedSecurity = false;
        sqlConnectionStringBuilder.UserID = txtUsername.Text;
        sqlConnectionStringBuilder.Password = txtPassword.Text;
      }

      sqlConnectionStringBuilder.InitialCatalog = databaseName;
      sqlConnectionStringBuilder.ApplicationName = "Scriptio";
      SqlConnection sqlConnection = new SqlConnection(sqlConnectionStringBuilder.ConnectionString);
      return sqlConnection;
    }

    private string GetConnectionString(string databaseName)
    {
      SqlConnectionStringBuilder csb = new SqlConnectionStringBuilder();
      csb.DataSource = txtServerName.Text;
      if (chkUseWindowsAuthentication.Checked)
      {
        csb.IntegratedSecurity = true;
      }
      else
      {
        csb.IntegratedSecurity = false;
        csb.UserID = txtUsername.Text;
        csb.Password = txtPassword.Text;
      }
      csb.InitialCatalog = databaseName;
      csb.ApplicationName = "Scriptio";
      return csb.ConnectionString;
    }

    private void DdlDatabases_SelectedIndexChanged(object sender, EventArgs e)
    {
      ComboBox comboxBox = (ComboBox)sender;
      chkScriptAll.Checked = false;
      if (comboxBox.SelectedIndex > 0)
      {
        PopulateObjects(txtServerName.Text, comboxBox.SelectedItem.ToString());
      }
      else
      {
        dgAvailableObjects.Rows.Clear();
      }
    }

    private void btnScript_Click(object sender, EventArgs e)
    {
      // RS: This section has also been heavily modified and rewritten to use the Scripter object instead of
      // iterating through predefined objects and using their .Script method. This gives us the added
      // flexibility of being able to populate the allobjects DataTable with ANY object that has a valid
      // URN, and the code will script it.
      toolStripStatusLabel1.Text = "Preparing script...";
      Application.DoEvents();

      Server srv;
      if (chkUseWindowsAuthentication.Checked)
      {
        srv = new Server(txtServerName.Text);
      }
      else
      {
        srv = new Server(new ServerConnection(txtServerName.Text, txtUsername.Text, txtPassword.Text));
      }

      string objectType = "";
      string objectName = "";
      string schema = "";
      txtResult.Text = "";

      result = new StringBuilder();

      // RS: Instead of stepping through the dataset to see what's checked, we just use a DataView to limit
      // the rows to only those that have the Script column equal to true.
      DataView allobjectschecked = new DataView(allobjects);
      // RS: Filter our system objects
      allobjectschecked.RowFilter = "([Schema] <> 'INFORMATION_SCHEMA') AND ([Schema] <> 'sys') AND" +
                                    "([Type] IN ('ExtendedStoredProcedure', 'PartitionFunction', 'PartitionScheme', " +
                                    "'SqlAssembly', 'StoredProcedure', 'Table', 'Trigger', 'UserDefinedAggregate', " +
                                    "'UserDefinedDataType', 'UserDefinedFunction', 'UserDefinedType', 'View')) " +
                                    "AND [Script] = True";

      dgAvailableObjects.EndEdit();
      // RS: We use the DataView's count property rather than the deprecated function from the previous
      // version of Scriptio.
      int totalObjects = allobjectschecked.Count;
      toolStripProgressBar1.Value = 0;
      toolStripProgressBar1.Maximum = totalObjects;
      result.EnsureCapacity(totalObjects * 4000);

      // Delete the file if it already exists
      if (File.Exists(txtSaveLocation.Text))
      {
        File.Delete(txtSaveLocation.Text);
      }

      ScriptingOptions baseOptions = new ScriptingOptions();
      baseOptions.IncludeHeaders = chkIncludeHeaders.Checked;
      baseOptions.Indexes = chkIndexes.Checked;
      baseOptions.DriAllKeys = chkKeys.Checked;
      baseOptions.NoCollation = !chkCollation.Checked;
      baseOptions.SchemaQualify = chkSchemaQualifyCreates.Checked;
      baseOptions.SchemaQualifyForeignKeysReferences = chkSchemaQualifyFK.Checked;
      baseOptions.Permissions = chkPermissions.Checked;

      if (rdoOneFile.Checked || rdoOnePerObject.Checked)
      {
        baseOptions.FileName = txtSaveLocation.Text;
        baseOptions.AppendToFile = true;
      }

      ScriptingOptions dropOptions = new ScriptingOptions();
      dropOptions.ScriptDrops = true;
      dropOptions.IncludeIfNotExists = chkExistance.Checked;
      dropOptions.SchemaQualify = chkSchemaQualifyDrops.Checked;

      // RS: Set the encoding options for both the drop AND the base scripting options object. We have to
      // do it on both, otherwise we have two different encodings in the same file, and we end up with a
      // complete mess.
      if (chkGenerateASCII.Checked)
      {
        dropOptions.Encoding = Encoding.ASCII;
        baseOptions.Encoding = Encoding.ASCII;
      }
      else
      {
        dropOptions.Encoding = Encoding.Unicode;
        baseOptions.Encoding = Encoding.Unicode;
      }

      // RS: Set the Scripter object up based on the connection to the server - the need for a Database
      // object is deprecated.
      Scripter scriptit = new Scripter(srv);
      // RS: Setup a URN object - this doesn't work quite the way I expected it to, but I managed to find a
      // code sample that had the index references. Now I understand it:)
      Urn[] scripturn = new Urn[1];

      if (rdoOneFile.Checked || rdoOnePerObject.Checked)
      {
        dropOptions.FileName = txtSaveLocation.Text;
        dropOptions.AppendToFile = true;
      }

      // process each checked object
      foreach (DataRowView row in allobjectschecked)
      {
        toolStripProgressBar1.Value++;

        objectType = row[3].ToString();
        objectName = row[2].ToString();
        schema = row[1].ToString();
        string fileName = "";

        if (rdoOnePerObject.Checked)
        {
          // RS: Set the filename up based on whether we're using the static .sql extension, or the
          // more dynamic SQL 2000 file extensions
          if (chkNamingConventions.Checked)
          {
            switch (objectType)
            {
              case "StoredProcedure":
                if (schema.Length > 0)
                {
                  fileName = Path.Combine(txtSaveLocation.Text, schema + "." + objectName + ".prc");
                }
                else
                {
                  fileName = Path.Combine(txtSaveLocation.Text, objectName + ".prc");
                }

                break;

              case "UserDefinedFunction":
                if (schema.Length > 0)
                {
                  fileName = Path.Combine(txtSaveLocation.Text, schema + "." + objectName + ".udf");
                }
                else
                {
                  fileName = Path.Combine(txtSaveLocation.Text, objectName + ".udf");
                }

                break;

              case "View":
                if (schema.Length > 0)
                {
                  fileName = Path.Combine(txtSaveLocation.Text, schema + "." + objectName + ".viw");
                }
                else
                {
                  fileName = Path.Combine(txtSaveLocation.Text, objectName + ".viw");
                }

                break;

              case "Table":
                if (schema.Length > 0)
                {
                  fileName = Path.Combine(txtSaveLocation.Text, schema + "." + objectName + ".tab");
                }
                else
                {
                  fileName = Path.Combine(txtSaveLocation.Text, objectName + ".tab");
                }

                break;

              case "Trigger":
                if (schema.Length > 0)
                {
                  fileName = Path.Combine(txtSaveLocation.Text, schema + "." + objectName + ".trg");
                }
                else
                {
                  fileName = Path.Combine(txtSaveLocation.Text, objectName + ".trg");
                }

                break;

              default:
                if (schema.Length > 0)
                {
                  fileName = Path.Combine(txtSaveLocation.Text, schema + "." + objectName + ".sql");
                }
                else
                {
                  fileName = Path.Combine(txtSaveLocation.Text, objectName + ".sql");
                }

                break;
            }
          }
          else
          {
            if (schema.Length > 0)
            {
              fileName = Path.Combine(txtSaveLocation.Text, schema + "." + objectName + ".sql");
            }
            else
            {
              fileName = Path.Combine(txtSaveLocation.Text, objectName + ".sql");
            }
          }

          if (File.Exists(fileName))
          {
            File.Delete(fileName);
          }

          baseOptions.FileName = fileName;
          dropOptions.FileName = fileName;
        }

        // RS: Tell the user what object we're scripting so they know when they hit a large object.
        if (schema.Length > 0)
        {
          toolStripStatusLabel1.Text = $"Scripting: {schema}.{objectName}...";
        }
        else
        {
          toolStripStatusLabel1.Text = $"Scripting: {objectName}...";
        }

        // RS: Run DoEvents() *before* we start the scripting, so the user sees the update above.
        Application.DoEvents();

        // RS: Set the URN object equal to the URN string.
        scripturn[0] = row[4].ToString();

        if (chkDrop.Checked)
        {
          // RS: Set the Scripter options, as we don't pass it when we call the Script method.
          scriptit.Options = dropOptions;
          // RS: Call the Script method, and pass the URN.
          AddLines(scriptit.Script(scripturn));
          AddGo();
        }

        if (chkCreate.Checked)
        {
          // RS: As per above
          scriptit.Options = baseOptions;
          AddLines(scriptit.Script(scripturn));
          AddGo();
        }
      }

      txtResult.MaxLength = result.Length + 100;
      txtResult.Text = result.ToString();
      toolStripStatusLabel1.Text = "Ready";
      tabControl1.SelectedTab = tabResult;
      // RS: Null our scripting objects (not necessary, but I like it)
      scriptit = null;
      scripturn = null;
      allobjectschecked.RowFilter = "";
      Application.DoEvents();
    }

    private void AddLines(StringCollection strings)
    {
      foreach (string s in strings)
      {
        result.Append(Environment.NewLine + s);

        if (s.StartsWith("SET QUOTED_IDENTIFIER") || s.StartsWith("SET ANSI_NULLS"))
          result.Append(Environment.NewLine + "GO");
      }
    }

    private void AddGo()
    {
      result.Append(Environment.NewLine + "GO" + Environment.NewLine + Environment.NewLine);
    }

    private void ChkScriptAll_CheckedChanged(object sender, EventArgs e)
    {
      CheckBox checkbox = (CheckBox)sender;
      foreach (DataGridViewRow row in dgAvailableObjects.Rows)
      {
        row.Cells[0].Value = checkbox.Checked;
      }
    }

    private void BtnPickDirectory_Click(object sender, EventArgs e)
    {
      if (rdoOneFile.Checked)
      {
        saveFileDialog1.ShowDialog();
        txtSaveLocation.Text = saveFileDialog1.FileName.ToString();
      }

      if (rdoOnePerObject.Checked)
      {
        folderBrowserDialog1.ShowDialog();
        txtSaveLocation.Text = folderBrowserDialog1.SelectedPath.ToString();
      }
    }

    private void RdoOneFile_CheckedChanged(object sender, EventArgs e)
    {
      txtSaveLocation.Text = string.Empty;
      chkNamingConventions.Enabled = false;
    }

    private void RdoNoFiles_CheckedChanged(object sender, EventArgs e)
    {
      txtSaveLocation.Text = "";
      chkNamingConventions.Enabled = false;
    }

    private void RdoOnePerObject_CheckedChanged(object sender, EventArgs e)
    {
      txtSaveLocation.Text = "";
      chkNamingConventions.Enabled = true;
    }

    private void BtnCopyClipboard_Click(object sender, EventArgs e)
    {
      txtResult.SelectAll();
      txtResult.Copy();
    }

    private void ChkUseWindowsAuthentication_CheckedChanged(object sender, EventArgs e)
    {
      CheckBox c = (CheckBox)sender;
      if (c.Checked)
      {
        txtUsername.Enabled = false;
        txtPassword.Enabled = false;
      }
      else
      {
        txtUsername.Enabled = true;
        txtPassword.Enabled = true;
      }
    }

    private void BtnSaveAs_Click(object sender, EventArgs e)
    {
      // RS: New code to save the scripted output.
      if (saveFileDialog1.ShowDialog() == DialogResult.OK)
      {
        // RS: Use a StreamWriter, and push each line of resultant text into it.
        StreamWriter sw;
        // RS: Make sure our StreamWriter's encoding is configured based on the user's options
        Encoding encoding;
        encoding = chkGenerateASCII.Checked ? Encoding.ASCII : Encoding.Unicode;
        sw = new StreamWriter(saveFileDialog1.FileName, false, encoding);
        //if (chkGenerateASCII.Checked)
        //{
        //  sw = new StreamWriter(saveFileDialog1.FileName, false, Encoding.ASCII);
        //}
        //else
        //{
        //  sw = new StreamWriter(saveFileDialog1.FileName, false, Encoding.Unicode);
        //}

        foreach (string str in txtResult.Lines)
        {
          sw.WriteLine(str);
        }

        sw.Close();
      }
    }

    private void Scriptio_Load(object sender, EventArgs e)
    {
      // RS: If we're building a debug build, set the server name to localhost and populate the database
      // list - saves us the hassle if we're testing. Change this as necessary.
#if (DEBUG)
      txtServerName.Text = @"localhost";
      PopulateDatabases(txtServerName.Text);
#endif
    }

    private void ClbType_SelectedIndexChanged(object sender, EventArgs e)
    {
      foreach (DataRow dataRow in allobjects.Rows)
      {
        // RS: Run throught the DataGrid and check anything that's been checked in the Type list
        if (clbType.CheckedItems.Contains(dataRow[3].ToString()))
        {
          dataRow[0] = true;
        }
        else
        {
          dataRow[0] = false;
        }
      }
    }

    private void ClbSchema_SelectedIndexChanged(object sender, EventArgs e)
    {
      foreach (DataRow dataRow in allobjects.Rows)
      {
        // RS: Run throught the DataGrid and check anything that's been checked in the Type list
        if (clbSchema.CheckedItems.Contains(dataRow[1].ToString()))
        {
          dataRow[0] = true;
        }
        else
        {
          dataRow[0] = false;
        }
      }
    }
  }
}