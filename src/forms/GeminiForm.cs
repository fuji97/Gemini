﻿using IronRuby.Builtins;
using ScintillaNet;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Permissions;
using System.Text.RegularExpressions;
using System.Windows.Forms;

/*\
 *  ######  ###### ##     ## ###### ##    ## ######
 * ##    ## ##     ###   ###   ##   ###   ##   ##
 * ##       ##     #### ####   ##   ####  ##   ##
 * ##  ###  ####   ## ### ##   ##   ## ## ##   ##
 * ##    ## ##     ##     ##   ##   ##  ####   ##
 * ##    ## ##     ##     ##   ##   ##   ###   ##
 *  ######  ###### ##     ## ###### ##    ## ######
\*/

namespace Gemini
{
  public partial class GeminiForm : Form
  {
    [DllImport("User32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /*\
     * ####### ##         ##        ##
     * ##                 ##        ##
     * ##      ###  ####  ##        ##  ######
     * #####   ##  ##  ## ##    ###### ##
     * ##      ##  ###### ##   ##   ##  #####
     * ##      ##  ##     ##   ##   ##      ##
     * ##      ##   #####  ###  ###### ######
     * ======================================
    \*/

    #region Fields and Properties

    private string _projectScriptPath = "";
    private string _projectScriptsFolderPath = "";
    private string _projectEngine = "";

    private Regex _invalidRegex = new Regex(@"[^A-Za-z0-9 +\-_=.,!@#$%^&();'(){}[\]]+",
      RegexOptions.Singleline | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private Regex _fileNameRegex = new Regex(@"([A-Za-z0-9 +\-_=.,!@#$%^&();'(){}[\]]+)\.([A-Za-z0-9]{8})\.rb",
      RegexOptions.Singleline | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private bool _projectNeedSave = false;
    private byte[] _projectLastSave;
    private List<Script> _scripts = new List<Script>();

    private bool _updatingText = false;

    private List<int> _usedSections = new List<int>();
    private List<ScriptList> _relations = new List<ScriptList>();

    private struct ScriptList
    {
      public ScriptList(int section, List<int> list)
      {
        Section = section;
        List = list;
      }

      public int Section;
      public List<int> List;
    }

    private FindReplaceDialog _findReplaceDialog = new FindReplaceDialog();
    private Process _charmap = new Process();

    #endregion Fields and Properties

    /*\
     *  ######                           ##                                ##
     * ##                                ##                                ##
     * ##       ######  #######   ###### #######  ######  ##    ##  ###### #######  ######   ######
     * ##      ##    ## ##    ## ##      ##      ##    ## ##    ## ##      ##      ##    ## ##    ##
     * ##      ##    ## ##    ##  #####  ##      ##       ##    ## ##      ##      ##    ## ##
     * ##      ##    ## ##    ##      ## ##   ## ##       ##    ## ##      ##   ## ##    ## ##
     *  ######  ######  ##    ## ######   #####  ##        ######   ######  #####   ######  ##
     * =============================================================================================
    \*/

    #region Contructor

    /// <summary>
    /// Initializes form components, child forms, and the Ruby engine.
    /// </summary>
    [EnvironmentPermission(SecurityAction.LinkDemand, Unrestricted = true)]
    public GeminiForm(string[] args)
    {
      InitializeComponent();
      Icon = Icon.FromHandle(Properties.Resources.gemini.GetHicon());
      Ruby.CreateRuntime();
      Settings.SetDefaults();
      Settings.SetLocalDefaults();
      Settings.LoadConfiguration();
      ApplySettings();
      UpdateMenusEnabled();
      string[] files = { };
      if (args.Length > 0 && IsProject(args[0]))
        OpenProject(args[0]);
      else if (Settings.RecentPriority && Settings.AutoOpen && Settings.RecentlyOpened.Count > 0)
        OpenRecentProject(0, false);
      else if (Settings.AutoOpen && IsProject(files = Directory.GetFiles(Application.StartupPath)))
      {
        foreach (string file in files)
          if (IsProject(file))
          {
            OpenProject(file);
            return;
          }
        if (Settings.RecentlyOpened.Count > 0)
          OpenRecentProject(0, false);
      }
      else if (Settings.AutoOpen && Settings.RecentlyOpened.Count > 0)
        OpenRecentProject(0, false);
      if (Settings.AutoCheckUpdates)
        new UpdateForm();
    }

    /// <summary>
    /// Applies all changes that are configured in the settings
    /// </summary>
    private void ApplySettings()
    {
      UpdateSettingsState();
      UpdateRecentProjectList();
      UpdateAutoCompleteWords();
      foreach (Script script in _scripts)
      {
        script.UpdateSettings();
        script.SetStyle();
      }
      if (Settings.AutoHideMenuBar && menuMain_menuStrip.Visible) MenuBarDeactivate();
      else MenuBarActive();
      Bounds = Settings.WindowBounds;
      if (Settings.WindowMaximized)
        WindowState = FormWindowState.Maximized;
    }

    #endregion Contructor

    /*\
     * #######                                  #######                            ##
     * ##                                       ##                                 ##
     * ##       ######   ######  ##### ###      ##      ##     ##  #####  #######  ######
     * #####   ##    ## ##    ## ##  ##  ##     #####   ##     ## ##   ## ##    ## ##
     * ##      ##    ## ##       ##  ##  ##     ##       ##   ##  ####### ##    ## ##
     * ##      ##    ## ##       ##  ##  ##     ##        ## ##   ##      ##    ## ##  ##
     * ##       ######  ##       ##  ##  ##     #######    ###     #####  ##    ##  ####
     * ==================================================================================
    \*/

    #region Main Form Events

    /// <summary>
    /// Cleans up resources and saves the state of the current settings for next time
    /// </summary>
    private void GeminiForm_Closing(object sender, FormClosingEventArgs e)
    {
      if (!CloseProject(true))
      {
        e.Cancel = true;
        return;
      }
      if (Settings.AutoSaveConfig)
        SaveConfiguration(false);
      try { _charmap.Kill(); } catch { }
    }

    private void GeminiForm_KeyDown(object sender, KeyEventArgs e)
    {
      if (Settings.AutoHideMenuBar && menuMain_menuStrip.Visible && e.Alt) MenuBarDeactivate();
      else if (Settings.AutoHideMenuBar && e.Alt) MenuBarActive();
    }

    /// <summary>
    /// Automatically rewrite the latest save when the script file is overwritten by another program
    /// </summary>
    private void fileSystemWatcher_Changed(object sender, FileSystemEventArgs e)
    {
      scriptsFileWatcher.EnableRaisingEvents = false;
      DateTime t = DateTime.Now;
      while ((DateTime.Now - t).TotalMilliseconds < 1000)
        try
        {
          File.WriteAllBytes(_projectScriptPath, _projectLastSave);
          break;
        }
        catch { }
      scriptsFileWatcher.EnableRaisingEvents = true;
    }

    #endregion Main Form Events

    /*\
     * ##     ##                                   ##               ##
     * ###   ###                                   ##
     * #### ####  #####  #######  ##    ##  ###### #######  ######  ### ######
     * ## ### ## ##   ## ##    ## ##    ## ##      ##      ##    ## ##  ##   ##
     * ##     ## ####### ##    ## ##    ##  #####  ##      ##       ##  ##   ##
     * ##     ## ##      ##    ## ##    ##      ## ##   ## ##       ##  ######
     * ##     ##  #####  ##    ##  ######  ######   #####  ##       ##  ##
     * =================================================================##=====
    \*/

    #region Menu Strip Events

    private void menuMain_menuStrip_Leave(object sender, EventArgs e)
    {
      if (Settings.AutoHideMenuBar) MenuBarDeactivate();
    }

    private void MenuBarActive()
    {
      menuMain_menuStrip.Visible = true;
    }

    private void MenuBarDeactivate()
    {
      menuMain_menuStrip.Visible = false;
    }

    #endregion Menu Strip Events

    /*\
     * ##     ##                               #######
     * ###   ###                               ##
     * #### ####  #####  #######  ##    ##     ##
     * ## ### ## ##   ## ##    ## ##    ##     #####
     * ##     ## ####### ##    ## ##    ##     ##
     * ##     ## ##      ##    ## ##    ##     ##      ###
     * ##     ##  #####  ##    ##  ######      ##      ###
     * ===================================================
    \*/

    #region Menu File Events

    private void mainMenu_ToolStripMenuItem_DropDownOpening(object sender, EventArgs e)
    {
      UpdateMenusEnabled();
    }

    private void mainMenu_ToolStripMenuItem_NewProjectRMXP_Click(object sender, EventArgs e)
    {
      using (NewProjectForm dialog = new NewProjectForm("RMXP"))
        if (dialog.ShowDialog() == DialogResult.OK)
          CreateProject("RMXP", dialog.GameTitle, dialog.ProjectDirectory, dialog.IncludeLibrary, dialog.OpenProject);
    }

    private void mainMenu_ToolStripMenuItem_NewProjectRMVX_Click(object sender, EventArgs e)
    {
      using (NewProjectForm dialog = new NewProjectForm("RMVX"))
        if (dialog.ShowDialog() == DialogResult.OK)
          CreateProject("RMVX", dialog.GameTitle, dialog.ProjectDirectory, dialog.IncludeLibrary, dialog.OpenProject);
    }

    private void mainMenu_ToolStripMenuItem_NewProjectRMVXAce_Click(object sender, EventArgs e)
    {
      using (NewProjectForm dialog = new NewProjectForm("RMVXAce"))
        if (dialog.ShowDialog() == DialogResult.OK)
          CreateProject("RMVXAce", dialog.GameTitle, dialog.ProjectDirectory, dialog.IncludeLibrary, dialog.OpenProject);
    }

    /// <summary>
    /// Starts open dialog for opening RMXP/RMVX project files
    /// </summary>
    private void mainMenu_ToolStripMenuItem_OpenProject_Click(object sender, EventArgs e)
    {
      using (OpenFileDialog dialog = new OpenFileDialog())
      {
        dialog.Filter = "RPG Makers Projects & Scripts|*.rxproj;*.rvproj;*.rvproj2;*.rxdata;*.rvdata;*.rvdata2|" +
                            "RMXP Project|*.rxproj|RMVX Project|*.rvproj|RMVXAce Project|*.rvproj2|" +
                            "RMXP Script|*.rxdata|RMVX Script|*.rvdata|RMVXAce Script|*.rvdata2|" +
                            "All Documents|*.*";
        dialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        dialog.Title = "Open Game Project...";
        if (dialog.ShowDialog() == DialogResult.OK)
          OpenProject(dialog.FileName);
      }
    }

    /// <summary>
    /// Opens the selected recent document after ensuring it still exists
    /// </summary>
    private void mainMenu_ToolStripMenuItem_OpenRecentProject_Click(object sender, EventArgs e)
    {
      OpenRecentProject(menuMain_dropFile_itemOpenRecent.DropDownItems.IndexOf((ToolStripItem)sender), true);
    }

    private void mainMenu_ToolStripMenuItem_CloseProject_Click(object sender, EventArgs e)
    {
      CloseProject(true);
    }

    /// <summary>
    /// Updates the text of each script, then Marshals it using Ruby
    /// </summary>
    private void mainMenu_ToolStripMenuItem_SaveProject_Click(object sender, EventArgs e)
    {
      SaveScripts();
    }

    private void menuMain_dropFile_itemImportScripts_Click(object sender, EventArgs e)
    {
      int section = int.Parse(scriptsView.SelectedNode.Name);
      int parent = GetParentList(section).Section;
      ImportScriptsFrom(parent, scriptsView.SelectedNode.Index + 1, Directory.GetFiles(_projectScriptsFolderPath));
      RestichList(parent);
    }

    /// <summary>
    /// Imports an existing text document or .rb file into the editor, adding it to the Scipt list
    /// </summary>
    private void menuMain_dropFile_itemImportScriptsFrom_Click(object sender, EventArgs e)
    {
      int section = int.Parse(scriptsView.SelectedNode.Name);
      int parent = GetParentList(section).Section;
      using (OpenFileDialog dialog = new OpenFileDialog())
      {
        dialog.Filter = "Ruby Script|*.rb|Text Document|*.txt|All Documents|*.*";
        dialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        dialog.Title = "Import Scripts...";
        dialog.Multiselect = true;
        DialogResult result = dialog.ShowDialog();
        if (result == DialogResult.OK)
          ImportScriptsFrom(parent, scriptsView.SelectedNode.Index + 1, dialog.FileNames);
      }
    }

    private void menuMain_dropFile_itemExport_Click(object sender, EventArgs e)
    {
      ExportScripts();
    }

    private void mainMenu_ToolStripMenuItem_ExportScriptsRMData_Click(object sender, EventArgs e)
    {
      using (SaveFileDialog dialog = new SaveFileDialog())
      {
        dialog.FileName = Path.GetFileName(_projectScriptPath);
        dialog.Filter = _projectEngine + " Scripts|*" + Path.GetExtension(_projectScriptPath) + "|All Documents|*.*";
        dialog.InitialDirectory = Settings.ProjectDirectory;
        dialog.Title = "Export Scripts...";
        if (dialog.ShowDialog() == DialogResult.OK)
          SaveScripts(dialog.FileName);
      }
    }

    /// <summary>
    /// Exports the scripts with a .txt extension
    /// </summary>
    private void mainMenu_ToolStripMenuItem_ExportScriptsText_Click(object sender, EventArgs e)
    {
      ExportScriptsTo(".txt");
    }

    /// <summary>
    /// Exports the scripts with an .rb extension
    /// </summary>
    private void mainMenu_ToolStripMenuItem_ExportScriptsRuby_Click(object sender, EventArgs e)
    {
      ExportScriptsTo(".rb");
    }

    private void mainMenu_ToolStripMenuItem_SaveSettings_Click(object sender, EventArgs e)
    {
      SaveConfiguration(true);
    }

    /// <summary>
    /// Toggles auto-save of configuration when the program exits
    /// </summary>
    private void mainMenu_ToolStripMenuItem_AutoSaveSettings_Click(object sender, EventArgs e)
    {
      Settings.AutoSaveConfig = !Settings.AutoSaveConfig;
      UpdateSettingsState();
      menuMain_dropFile.ShowDropDown();
      menuMain_dropSettings_itemSaveSettings.ShowDropDown();
      menuMain_dropSettings_itemAutoSaveSettings.Select();
    }

    /// <summary>
    /// Load default settings.
    /// </summary>
    private void mainMenu_ToolStripMenuItem_DeleteSettings_Click(object sender, EventArgs e)
    {
      if (MessageBox.Show("Are you sure you want to delete all settings?",
          "Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        return;
      Settings.SetDefaults();
      ApplySettings();
    }

    /// <summary>
    /// Exits the application
    /// </summary>
    private void mainMenu_ToolStripMenuItem_Exit_Click(object sender, EventArgs e)
    {
      Close();
    }

    #endregion Menu File Events

    /*\
     * ##     ##                               #######
     * ###   ###                               ##
     * #### ####  #####  #######  ##    ##     ##
     * ## ### ## ##   ## ##    ## ##    ##     #####
     * ##     ## ####### ##    ## ##    ##     ##
     * ##     ## ##      ##    ## ##    ##     ##      ###
     * ##     ##  #####  ##    ##  ######      ####### ###
     * ===================================================
    \*/

    #region Menu Edit Events

    private void mainMenu_ToolStripMenuItem_Undo_Click(object sender, EventArgs e)
    {
      Script script = GetActiveScript();
      if (script != null)
        script.Scintilla.Commands.Execute(BindableCommand.Undo);
    }

    private void mainMenu_ToolStripMenuItem_Redo_Click(object sender, EventArgs e)
    {
      Script script = GetActiveScript();
      if (script != null)
        script.Scintilla.Commands.Execute(BindableCommand.Redo);
    }

    private void mainMenu_ToolStripMenuItem_Cut_Click(object sender, EventArgs e)
    {
      Script script = GetActiveScript();
      if (script != null)
        script.Scintilla.Commands.Execute(BindableCommand.Cut);
    }

    private void mainMenu_ToolStripMenuItem_Copy_Click(object sender, EventArgs e)
    {
      Script script = GetActiveScript();
      if (script != null)
        script.Scintilla.Commands.Execute(BindableCommand.Copy);
    }

    private void mainMenu_ToolStripMenuItem_Paste_Click(object sender, EventArgs e)
    {
      Script script = GetActiveScript();
      if (script != null)
        script.Scintilla.Commands.Execute(BindableCommand.Paste);
    }

    private void mainMenu_ToolStripMenuItem_Delete_Click(object sender, EventArgs e)
    {
      Script script = GetActiveScript();
      if (script != null)
        script.Scintilla.Commands.Execute(BindableCommand.DeleteBack);
    }

    private void mainMenu_ToolStripMenuItem_SelectAll_Click(object sender, EventArgs e)
    {
      Script script = GetActiveScript();
      if (script != null)
        script.Scintilla.Commands.Execute(BindableCommand.SelectAll);
    }

    /// <summary>
    /// Opens the quick-find dialog
    /// </summary>
    private void mainMenu_ToolStripMenuItem_IncrementalSearch_Click(object sender, EventArgs e)
    {
      Script script = GetActiveScript();
      if (script != null)
      {
        script.Scintilla.Commands.Execute(BindableCommand.IncrementalSearch);
        Point p = script.Scintilla.PointToClient(MousePosition);
        if (script.Scintilla.Bounds.Contains(p))
          script.Scintilla.FindReplace.IncrementalSearcher.Location = p;
      }
    }

    /// <summary>
    /// Opens the find/replace dialog with the Find tab selected
    /// </summary>
    private void mainMenu_ToolStripMenuItem_Find_Click(object sender, EventArgs e)
    {
      ShowFind();
    }

    /// <summary>
    /// Opens the find/replace dialog with the Replace tab selected
    /// </summary>
    private void mainMenu_ToolStripMenuItem_Replace_Click(object sender, EventArgs e)
    {
      ShowReplace();
    }

    /// <summary>
    /// Opens the dialog for the "goto line"
    /// </summary>
    private void mainMenu_ToolStripMenuItem_GoToLine_Click(object sender, EventArgs e)
    {
      Script script = GetActiveScript();
      if (script != null)
        script.Scintilla.Commands.Execute(BindableCommand.ShowGoTo);
    }

    private void mainMenu_ToolStripMenuItem_ToggleComment_Click(object sender, EventArgs e)
    {
      Script script = GetActiveScript();
      if (script != null)
        script.Scintilla.Commands.Execute(BindableCommand.ToggleLineComment);
    }

    /// <summary>
    /// Batch comments all selected lines
    /// </summary>
    private void mainMenu_ToolStripMenuItem_Comment_Click(object sender, EventArgs e)
    {
      Script script = GetActiveScript();
      if (script != null)
        script.Scintilla.Commands.Execute(BindableCommand.LineComment);
    }

    /// <summary>
    /// Batch uncomments all selected lines
    /// </summary>
    private void mainMenu_ToolStripMenuItem_UnComment_Click(object sender, EventArgs e)
    {
      Script script = GetActiveScript();
      if (script != null)
        script.Scintilla.Commands.Execute(BindableCommand.LineUncomment);
    }

    /// <summary>
    /// Initiates the function to apply the proper structuring to the open script
    /// </summary>
    private void mainMenu_ToolStripMenuItem_StructureScriptCurrent_Click(object sender, EventArgs e)
    {
      Script script = GetActiveScript();
      if (script != null)
        script.StructureScript();
    }

    /// <summary>
    /// Applies the restructuring to all scripts that are opened currently
    /// </summary>
    private void mainMenu_ToolStripMenuItem_StructureScriptOpen_Click(object sender, EventArgs e)
    {
      Enabled = false;
      foreach (Script script in _scripts)
        if (script.Opened)
          script.StructureScript();
      Enabled = true;
    }

    /// <summary>
    /// Applies the restructuring to all scripts
    /// </summary>
    private void mainMenu_ToolStripMenuItem_StructureScriptAll_Click(object sender, EventArgs e)
    {
      Enabled = false;
      foreach (Script script in _scripts)
        script.StructureScript();
      UpdateNames();
      Enabled = true;
    }

    private void mainMenu_ToolStripMenuItem_RemoveEmptyLinesCurrent_Click(object sender, EventArgs e)
    {
      Script script = GetActiveScript();
      if (script != null)
        script.RemoveEmptyLines();
    }

    private void mainMenu_ToolStripMenuItem_RemoveEmptyLinesOpen_Click(object sender, EventArgs e)
    {
      Enabled = false;
      foreach (Script script in _scripts)
        if (script.Opened)
          script.RemoveEmptyLines();
      Enabled = true;
    }

    private void mainMenu_ToolStripMenuItem_RemoveEmptyLinesAll_Click(object sender, EventArgs e)
    {
      Enabled = false;
      foreach (Script script in _scripts)
        script.RemoveEmptyLines();
      UpdateNames();
      Enabled = true;
    }

    #endregion Menu Edit Events

    /*\
     * ##     ##                                ######
     * ###   ###                               ##
     * #### ####  #####  #######  ##    ##     ##
     * ## ### ## ##   ## ##    ## ##    ##      ######
     * ##     ## ####### ##    ## ##    ##           ##
     * ##     ## ##      ##    ## ##    ##           ## ###
     * ##     ##  #####  ##    ##  ######      #######  ###
     * ====================================================
    \*/

    #region Menu Settings Events

    private void menuMain_dropSettings_itemProjectSettings_Click(object sender, EventArgs e)
    {
      if (Settings.ProjectConfig)
      {
        DialogResult result = MessageBox.Show("Do you want to save the local configuration now?", "Save configuration?", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (result == DialogResult.Yes)
          SaveLocalConfiguration();
      }
      else if (File.Exists(Settings.ProjectDirectory + "Gemini.config"))
      {
        DialogResult result = MessageBox.Show("There was found a configuration in the project folder, do you wish to load it?\nIf not loaded now, it will be overwritten on next exit", "Load configuration?", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (result == DialogResult.Yes)
          LoadLocalConfiguration();
      }
      Settings.ProjectConfig = !Settings.ProjectConfig;
      UpdateSettingsState();
      menuMain_dropSettings.ShowDropDown();
      menuMain_dropSettings_itemConfiguration.ShowDropDown();
      menuMain_dropSettings_itemProjectSettings.Select();
    }

    private void menuMain_dropSetting_itemAutoHideMenuBar_Click(object sender, EventArgs e)
    {
      Settings.AutoHideMenuBar = !Settings.AutoHideMenuBar;
      if (Settings.AutoHideMenuBar && menuMain_menuStrip.Visible) MenuBarDeactivate();
      else MenuBarActive();
      UpdateSettingsState();
    }

    private void menuMain_dropSettings_itemHideToolbar_Click(object sender, EventArgs e)
    {
      Settings.DistractionMode = new Serializable.DistracionMode(Settings.DistractionMode.Use, !Settings.DistractionMode.HideToolbar);
      UpdateSettingsState();
      menuMain_dropSettings.ShowDropDown();
      menuMain_dropSettings_itemViewConfig.ShowDropDown();
      menuMain_dropSettings_itemHideToolbar.Select();
    }

    private void menuMain_dropSettings_itemPioritizeRecent_Click(object sender, EventArgs e)
    {
      Settings.RecentPriority = !Settings.RecentPriority;
      UpdateSettingsState();
      menuMain_dropSettings.ShowDropDown();
      menuMain_dropSettings_itemAutoOpenProject.ShowDropDown();
      menuMain_dropSettings_itemPioritizeRecent.Select();
    }

    private void menuMain_dropSettings_itemAutoOpenOn_Click(object sender, EventArgs e)
    {
      Settings.AutoOpen = true;
      UpdateSettingsState();
      menuMain_dropSettings.ShowDropDown();
      menuMain_dropSettings_itemAutoOpenProject.ShowDropDown();
      menuMain_dropSettings_itemAutoOpenOn.Select();
    }

    private void menuMain_dropSettings_itemAutoOpenOff_Click(object sender, EventArgs e)
    {
      Settings.AutoOpen = false;
      UpdateSettingsState();
      menuMain_dropSettings.ShowDropDown();
      menuMain_dropSettings_itemAutoOpenProject.ShowDropDown();
      menuMain_dropSettings_itemAutoOpenOff.Select();
    }

    private void menuMain_dropSettings_itemCustomRuntime_Click(object sender, EventArgs e)
    {
      using (RunVarsForm dialog = new RunVarsForm())
        if (dialog.ShowDialog() == DialogResult.OK)
        {
          Settings.RuntimeExecutable = dialog.Executable;
          Settings.RuntimeArguments = dialog.Arguments;
        }
    }

    private void menuMain_dropSettings_itemAutoUpdate_Click(object sender, EventArgs e)
    {
      Settings.AutoCheckUpdates = !Settings.AutoCheckUpdates;
      UpdateSettingsState();
      menuMain_dropSettings.ShowDropDown();
      menuMain_dropSettings_itemUpdate.ShowDropDown();
      menuMain_dropSettings_itemAutoUpdate.Select();
    }

    /// <summary>
    /// Opens the version update window.
    /// </summary>
    private void menuMain_dropSettings_itemUpdateNow_Click(object sender, EventArgs e)
    {
      using (UpdateForm dialog = new UpdateForm())
        if (dialog.ShowDialog() == DialogResult.OK)
        {
          if (NeedSave())
          {
            DialogResult result = MessageBox.Show("Save changes before closing?",
                "Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (result == DialogResult.Yes)
              SaveScripts();
          }
          CloseProject(false);
          Close();
          dialog.StartProcess();
        }
    }

    /// <summary>
    /// Opens the channel selection window.
    /// </summary>
    private void menuMain_dropSettings_itemUpdateChannel_Click(object sender, EventArgs e)
    {
      using (UpdateChannelForm dialog = new UpdateChannelForm())
        if (dialog.ShowDialog() == DialogResult.OK)
        {
          Settings.UpdateChannel = dialog.Current;
          Settings.UpdateChannels = dialog.Channels;
        }
    }

    private void menuMain_dropSettings_itemToggleDistractionMode_Click(object sender, EventArgs e)
    {
      Settings.DistractionMode = new Serializable.DistracionMode(!Settings.DistractionMode.Use, Settings.DistractionMode.HideToolbar);
      UpdateSettingsState();
    }

    /// <summary>
    /// Displays the style editor dialog
    /// </summary>
    private void menuMain_dropSettings_itemStyleConfig_Click(object sender, EventArgs e)
    {
      using (StyleEditorForm dialog = new StyleEditorForm())
        if (dialog.ShowDialog() == DialogResult.OK)
        {
          Settings.ScriptStyles = dialog.Styles;
          foreach (Script script in _scripts)
            script.SetStyle();
        }
    }

    /// <summary>
    /// Calls the dialog for configuring the autocomplete function
    /// </summary>
    private void mainMenu_ToolStripMenuItem_AutoCompleteConfig_Click(object sender, EventArgs e)
    {
      using (AutoCompleteForm dialog = new AutoCompleteForm())
        if (dialog.ShowDialog() == DialogResult.OK)
        {
          Settings.AutoCompleteLength = (int)dialog.numericUpDownCharacters.Value;
          Settings.AutoCompleteCustomWords = dialog.textBoxList.Text;
          Settings.AutoCompleteFlag = 0;
          for (int i = 0; i < dialog.checkedListBoxGroups.Items.Count; i++)
            if (dialog.checkedListBoxGroups.GetItemChecked(i))
              Settings.AutoCompleteFlag |= 1 << i;
          UpdateAutoCompleteWords();
        }
    }

    /// <summary>
    /// Toggles auto-complete on/off
    /// </summary>
    private void mainMenu_ToolStripMenuItem_AutoComplete_Click(object sender, EventArgs e)
    {
      Settings.AutoComplete = !Settings.AutoComplete;
      UpdateSettingsState();
      if (Settings.AutoComplete && Settings.AutoCompleteFlag == 0)
      {
        DialogResult result = MessageBox.Show("Auto-complete word list is empty, would you like to configure it now?",
          "Configuration Required", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (result == DialogResult.Yes)
          mainMenu_ToolStripMenuItem_AutoCompleteConfig_Click(sender, e);
      }
      else if (sender == menuMain_dropSettings_itemAutoC)
      {
        menuMain_dropSettings.ShowDropDown();
        menuMain_dropSettings_itemAutoC.Select();
      }
    }

    /// <summary>
    /// Toggles the guide-lines on/off
    /// </summary>
    private void mainMenu_ToolStripMenuItem_IndentGuides_Click(object sender, EventArgs e)
    {
      Settings.GuideLines = !Settings.GuideLines;
      UpdateSettingsState();
      foreach (Script script in _scripts)
        script.UpdateSettings();
      if (sender == menuMain_dropSettings_itemIndentGuides)
      {
        menuMain_dropSettings.ShowDropDown();
        menuMain_dropSettings_itemIndentGuides.Select();
      }
    }

    /// <summary>
    /// Toggles auto-indenting on/off
    /// </summary>
    private void mainMenu_ToolStripMenuItem_AutoIndent_Click(object sender, EventArgs e)
    {
      Settings.AutoIndent = !Settings.AutoIndent;
      UpdateSettingsState();
      foreach (Script script in _scripts)
        script.UpdateSettings();
      if (sender == menuMain_dropSettings_itemAutoIndent)
      {
        menuMain_dropSettings.ShowDropDown();
        menuMain_dropSettings_itemAutoIndent.Select();
      }
    }

    /// <summary>
    /// Toggles the line highlighter on/off
    /// </summary>
    private void mainMenu_ToolStripMenuItem_LineHighlight_Click(object sender, EventArgs e)
    {
      Settings.LineHighLight = !Settings.LineHighLight;
      UpdateSettingsState();
      foreach (Script script in _scripts)
        script.UpdateSettings();
      if (sender == menuMain_dropSettings_itemHighlight)
      {
        menuMain_dropSettings.ShowDropDown();
        menuMain_dropSettings_itemHighlight.Select();
      }
    }

    /// <summary>
    /// Opens the dialog for changing the color/opacity of the line highlighter
    /// </summary>
    private void mainMenu_ToolStripMenuItem_HighlightColor_Click(object sender, EventArgs e)
    {
      using (ColorChooserForm dialog = new ColorChooserForm())
      {
        dialog.Color = Settings.LineHighLightColor;
        if (dialog.ShowDialog() == DialogResult.OK)
        {
          Settings.LineHighLightColor = dialog.Color;
          foreach (Script script in _scripts)
            script.UpdateSettings();
        }
      }
    }

    private void mainMenu_ToolStripMenuItem_CodeFolding_Click(object sender, EventArgs e)
    {
      Settings.CodeFolding = !Settings.CodeFolding;
      UpdateSettingsState();
      foreach (Script script in _scripts)
        script.UpdateSettings();
      if (sender == menuMain_dropSettings_itemFolding)
      {
        menuMain_dropSettings.ShowDropDown();
        menuMain_dropSettings_itemFolding.Select();
      }
    }

    //TODO: Cleanup code here.
    private void menuMain_dropSettings_itemUpdateSections_Click(object sender, EventArgs e)
    {
      DialogResult result = MessageBox.Show("If you proceed, all scripts will be saved with new sections. Proceed?",
          "Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
      if (result == DialogResult.No)
        return;
      //string _text, _file;
      //bool folder = _projectScriptsFolderPath != "" && Directory.Exists(_projectScriptsFolderPath);
      //TreeNode node;
      //List<string> files = !folder ? new List<string>() : new List<string>(Directory.GetFiles(_projectScriptsFolderPath, "*.rb", SearchOption.TopDirectoryOnly));
      foreach (Script s in _scripts)
      {
        int oldsection = s.Section;
        s.Section = GetRandomSection();
        _usedSections.Remove(oldsection);
        int i = _relations.FindIndex(delegate (ScriptList m) { return m.Section == oldsection; });
        if (i >= 0) _relations[i] = new ScriptList(s.Section, _relations[i].List);
        List<int> l; (l = GetParentList(oldsection).List)[l.IndexOf(oldsection)] = s.Section;

        //(node = GetNodeBySection(_oldSections[i])).Name = string.Format("{0:00000000}", s.Section);
        //node.ToolTipText = s.Name + " - " + string.Format("{0:00000000}", s.Section);
        //if (folder)
        //{
        //  _file = files.Find(delegate (string str)
        //  {
        //    return Regex.IsMatch(str, string.Format("{0:00000000}",
        //      oldsection), RegexOptions.Singleline | RegexOptions.CultureInvariant);
        //  });
        //  if (!string.IsNullOrEmpty(_file))
        //  {
        //    _text = File.ReadAllText(_file);
        //    File.Delete(_file);
        //    File.WriteAllText(_projectScriptsFolderPath + s.Name + "." + string.Format("{0:00000000}", s.Section) + ".rb", _text);
        //  }
        //}
      }
      BuildFromRoot();
      //SaveScripts();
    }

    #endregion Menu Settings Events

    /*\
     * ##     ##                                ######
     * ###   ###                               ##    ##
     * #### ####  #####  #######  ##    ##     ##
     * ## ### ## ##   ## ##    ## ##    ##     ##  ###
     * ##     ## ####### ##    ## ##    ##     ##    ##
     * ##     ## ##      ##    ## ##    ##     ##    ## ###
     * ##     ##  #####  ##    ##  ######       ######  ###
     * ====================================================
    \*/

    #region Menu Game Events

    private void mainMenu_ToolStripMenuItem_Help_Click(object sender, EventArgs e)
    {
      string projectEngine = _projectEngine.Replace("RM", "RPG");
      if (!File.Exists(projectEngine + ".chm"))
        CopyResource("Gemini.files.help." + projectEngine + ".chm", projectEngine + ".chm");
      Help.ShowHelp(this, projectEngine + ".chm");
    }

    /// <summary>
    /// Event raised that will begin execution of the game. Runs in test mode and auto-saves if configured to do so.
    /// </summary>
    private void menuMain_dropGame_itemRun_Click(object sender, EventArgs e)
    {
      if (string.IsNullOrEmpty(_projectScriptsFolderPath))
      {
        MessageBox.Show("You cannot test the game when editing a '.r*data' file.\nTo do so you must open the project's '.r*proj' file.",
            "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return;
      }
      if (NeedSave())
      {
        DialogResult result = MessageBox.Show("Save changes before running?",
          "Warning", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
        if (result == DialogResult.Cancel) return;
        else if (result == DialogResult.Yes)
          SaveScripts();
      }
      string arguments = "";
      if (!Settings.DebugMode) arguments = "";
      else if (!string.IsNullOrEmpty(Settings.RuntimeArguments)) arguments = Settings.RuntimeArguments;
      else if (_projectEngine == "RMXP") arguments = "debug";
      else if (_projectEngine == "RMVX") arguments = "test";
      else if (_projectEngine == "RMVXAce") arguments = "console test";
      try { Process.Start(Settings.ProjectDirectory + (!string.IsNullOrEmpty(Settings.RuntimeExecutable) ? Settings.RuntimeExecutable : "Game.exe"), arguments); }
      catch { MessageBox.Show("Cannot run game.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void mainMenu_ToolStripMenuItem_Debug_Click(object sender, EventArgs e)
    {
      Settings.DebugMode = !Settings.DebugMode;
      UpdateSettingsState();
      if (sender == menuMain_dropGame_itemDebug)
      {
        menuMain_dropGame.ShowDropDown();
        menuMain_dropGame_itemDebug.Select();
      }
    }

    private void mainMenu_ToolStripMenuItem_ProjectFolder_Click(object sender, EventArgs e)
    {
      if (string.IsNullOrEmpty(Settings.ProjectDirectory)) return;
      Process.Start(Settings.ProjectDirectory);
    }

    #endregion Menu Game Events

    /*\
     * ##     ##                                  ###
     * ###   ###                                 ## ##
     * #### ####  #####  #######  ##    ##      ##   ##
     * ## ### ## ##   ## ##    ## ##    ##     ##     ##
     * ##     ## ####### ##    ## ##    ##     #########
     * ##     ## ##      ##    ## ##    ##     ##     ## ###
     * ##     ##  #####  ##    ##  ######      ##     ## ###
     * =====================================================
    \*/

    #region Menu About Events

    private void mainMenu_ToolStripMenuItem_VersionHistory_Click(object sender, EventArgs e)
    { Process.Start("https://github.com/revam/Gemini/blob/master/CHANGELOG.md"); }

    private void mainMenu_ToolStripMenuItem_AboutGemini_Click(object sender, EventArgs e)
    { using (AboutForm dialog = new AboutForm()) dialog.ShowDialog(); }

    #endregion Menu About Events

    /*\
     * #######      ## ##  ##
     * ##           ##     ##
     * ##           ## ### #######  ######   ######
     * #####    ###### ##  ##      ##    ## ##    ##
     * ##      ##   ## ##  ##      ##    ## ##
     * ##      ##   ## ##  ##   ## ##    ## ##
     * #######  ###### ##   #####   ######  ##
     * ==
    \*/

    #region Script Editor Events

    /// <summary>
    /// Opens the native Character Map for creating special Unicode characters
    /// </summary>
    private void scriptsEditor_ToolStripButton_SpecialChars_Click(object sender, EventArgs e)
    {
      _charmap.StartInfo.FileName = "charmap.exe";
      try
      {
        if (!_charmap.HasExited)
        {
          SetForegroundWindow(_charmap.MainWindowHandle);
          return;
        }
      }
      catch { }
      try { _charmap.Start(); }
      catch
      {
        MessageBox.Show("\"C:/Windows/System32/charmap.exe\" could not be found on the system.",
            "File Not Found", MessageBoxButtons.OK, MessageBoxIcon.Error);
      }
    }

    private void scriptsEditor_ToolStripMenuItem_FindNext_Click(object sender, EventArgs e)
    { _findReplaceDialog.FindNext(); }

    private void scriptsEditor_ToolStripMenuItem_FindPrevious_Click(object sender, EventArgs e)
    { _findReplaceDialog.FindPrevious(); }

    private void scriptsEditor_ToolStripMenuItem_AddWordToAutoComplete_Click(object sender, EventArgs e)
    {
      Script script = GetActiveScript();
      if (script != null)
      {
        List<string> words = new List<string>();
        if (script.Scintilla.Selection.Length == 0)
        {
          string word = script.Scintilla.GetWordFromPosition(script.Scintilla.CurrentPos);
          if (word.Length > 1)
            words.Add(word);
        }
        else
          for (int pos = script.Scintilla.Selection.Range.Start; pos < script.Scintilla.Selection.Range.End; pos++)
          {
            string word = script.Scintilla.GetWordFromPosition(pos);
            if (word.Length > 1 && !words.Contains(word))
              words.Add(word);
          }
        if (words.Count > 0)
        {
          Settings.AutoCompleteCustomWords += " " + string.Join(" ", words);
          UpdateAutoCompleteWords();
        }
      }
    }

    private void scriptsEditor_TabControl_GotFocus(object sender, EventArgs e)
    {
      Script script = GetActiveScript();
      if (script != null)
        script.Scintilla.Focus();
    }

    /// <summary>
    /// Updates the labels accordingly with the activated script
    /// </summary>
    private void scriptsEditor_TabControl_SelectedIndexChanged(object sender, EventArgs e)
    {
      Script script = GetActiveScript();
      if (script == null)
        _findReplaceDialog.Hide();
      else
      {
        _findReplaceDialog.Scintilla = script.Scintilla;
      }
      UpdateScriptStatus();
    }

    private void scriptsEditor_TabControl_TabPageRemoving(object sender, TabControlCancelEventArgs e)
    {
      Script script = _scripts.Find(delegate (Script s) { return s.Opened && s.TabPage == e.TabPage; });
      if (script == null) return;
      if (script.NeedApplyChanges)
      {
        DialogResult result = MessageBox.Show(
            "Apply changes to this script before closing?\r\n\r\nNote: This does not save the data permanently",
            "Warning", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
        if (result == DialogResult.Yes)
          script.ApplyChanges();
        else if (result == DialogResult.Cancel)
        {
          e.Cancel = true;
          return;
        }
      }
      script.Dispose();
      UpdateNames();
      UpdateMenusEnabled();
    }

    /// <summary>
    /// Enables/disables the comment controls depending on the selection, as well as the selection length label.
    /// </summary>
    private void Scintilla_Changed(object sender, EventArgs e)
    {
      UpdateScriptStatus();
      UpdateMenusEnabled();
      if (GetActiveScript() != null)
        UpdateName(GetActiveScript().Section);
    }

    #endregion Script Editor Events

    /*\
     *  ######                  ##          ##           ######                                  ##
     * ##                                   ##          ##                                       ##
     * ##       ######  ######  ### ######  #######     ##       #####   #####   ######   ###### ## ####
     *  #####  ##      ##    ## ##  ##   ## ##           #####  ##   ##      ## ##    ## ##      ###   ##
     *      ## ##      ##       ##  ##   ## ##               ## #######  ###### ##       ##      ##    ##
     *      ## ##      ##       ##  ######  ##   ##          ## ##      ##   ## ##       ##      ##    ##
     * ######   ###### ##       ##  ##       #####      ######   #####  ####### ##        ###### ##    ##
     * ==================================================================================================
    \*/

    #region Script Search Events

    private void searches_ToolStripButton_Click(object sender, EventArgs e)
    {
      Search((SearchControl)((ToolStripButton)sender).GetCurrentParent().Parent);
    }

    /// <summary>
    /// Goes to the line in the script of the clicked result
    /// </summary>
    private void searches_ListView_ItemActivate(object sender, EventArgs e)
    {
      SearchResult result = (SearchResult)((ListView)sender).SelectedItems[0];
      if (ScriptExists(result.Section))
      {
        OpenScript(result.Section);
        GetScript(result.Section).Scintilla.GoTo.Line(result.Line);
      }
    }

    private void searches_TabControl_ControlRemoved(object sender, ControlEventArgs e)
    {
      if (searches_TabControl.TabCount == 1)
        splitView.Panel2Collapsed = true;
    }

    #endregion Script Search Events

    /*\
     * ##     ## ##
     * ##     ##
     * ##     ## ###  #####  ##       ##
     * ##     ## ##  ##   ## ##       ##
     *  ##   ##  ##  ####### ##   #   ##
     *   ## ##   ##  ##       ## ### ##
     *    ###    ##   #####    ### ###
     * =================================
    \*/

    #region Scripts View Events

    private void scriptsView_MouseDown(object sender, MouseEventArgs e)
    {
      if (scriptsView.SelectedNode != null && e.Button == MouseButtons.Left && e.Clicks == 2 && scriptsView.SelectedNode.Level == 1)
        scriptsView_itemOpen_Click(sender, e);
    }

    private void scriptsView_KeyDown(object sender, KeyEventArgs e)
    {
      if (e.KeyCode == Keys.Enter) scriptsView_itemOpen_Click(sender, e);
      else if (e.KeyCode == Keys.Insert) scriptsView_itemInsert_Click(sender, e);
      else if (e.KeyCode == Keys.Delete) scriptsView_itemDelete_Click(sender, e);
    }

    private void scriptsView_AfterSelect(object sender, TreeViewEventArgs e)
    {
      _updatingText = true;
      scriptName.Text = GetScript(int.Parse(e.Node.Name)).Name;
      _updatingText = false;
      UpdateMenusEnabled();
    }

    private void scriptsView_BeforeExpand(object sender, TreeViewCancelEventArgs e)
    {
      if (e.Node.Nodes.ContainsKey(VIRTUALNODE))
      {
        e.Node.Nodes.Clear();
        RebuildFrom(int.Parse(e.Node.Name));
      }
    }

    private void scriptsView_AfterCollapse(object sender, TreeViewEventArgs e)
    {
      if (!e.Node.Nodes.ContainsKey(VIRTUALNODE))
      {
        e.Node.Nodes.Clear();
        e.Node.Nodes.Add(CreateVNode());
      }
    }

    /// <summary>
    /// Either opens a new page of the selected script, or selects the appropriate tab if it is already open.
    /// </summary>
    private void scriptsView_itemOpen_Click(object sender, EventArgs e)
    {
      if (scriptsView.SelectedNode == null) return;
      OpenScript(int.Parse(scriptsView.SelectedNode.Name));
    }

    /// <summary>
    /// Inserts a new Script control at index
    /// </summary>
    private void scriptsView_itemInsert_Click(object sender, EventArgs e)
    {
      int section = int.Parse(scriptsView.SelectedNode.Name);
      int parent = GetParentList(section).Section;
      using (InsertForm dialog = new InsertForm())
        if (dialog.ShowDialog() == DialogResult.OK)
          // Create new script below current.
          if (dialog.State == 5)
            section = InsertScript(parent, scriptsView.SelectedNode.Index + 1, dialog.Title, "");
          // Import scripts below current.
          else if (dialog.State == 6)
            section = ImportScriptsFrom(parent, scriptsView.SelectedNode.Index + 1, dialog.Paths);
          // Create script under current.
          else if (dialog.State == 9)
            InsertScript(section, -1, dialog.Title, "");
          // Import scripts under current.
          else if (dialog.State == 10)
            section = ImportScriptsFrom(section, -1, dialog.Paths);
          else
            MessageBox.Show("Invalid state '" + dialog.State + "' returned.");
      scriptsView.BeginUpdate();
      RestichList(parent);
      scriptsView.SelectedNode = GetNode(section);
      scriptsView.EndUpdate();
    }

    /// <summary>
    /// Removes currently selected script from list, and copies to clipboard
    /// </summary>
    private void scriptsView_itemCut_Click(object sender, EventArgs e)
    {
      scriptsView_itemCopy_Click(sender, e);
      scriptsView_itemDelete_Click(sender, e);
    }

    /// <summary>
    /// Copies selected script to the clipboard
    /// </summary>
    private void scriptsView_itemCopy_Click(object sender, EventArgs e)
    {
      int section = int.Parse(scriptsView.SelectedNode.Name);
      if (section >= 0)
      {
        SetClipboardScript(GetScript(section));
        UpdateMenusEnabled();
      }
    }

    /// <summary>
    /// Paste the script from the clipboard to the selected index
    /// </summary>
    private void scriptsView_itemPaste_Click(object sender, EventArgs e)
    {
      if (scriptsView.SelectedNode == null)
        return;

      RubyArray rmScript = GetClipboardScript();

      if (rmScript == null)
        return;

      scriptsView.BeginUpdate();
      int section = int.Parse(scriptsView.SelectedNode.Name);
      int parent = GetParentList(section).Section;

      InsertScript(parent, scriptsView.SelectedNode.Index, rmScript);
    }

    /// <summary>
    /// Deletes the currently selectefd script
    /// </summary>
    private void scriptsView_itemDelete_Click(object sender, EventArgs e)
    {
      if (scriptsView.SelectedNode == null)
        return;
      scriptsView.BeginUpdate();
      int section = int.Parse(scriptsView.SelectedNode.Name);
      int parent = GetParentList(section).Section;
      RemoveScript(section);
      RestichList(parent);
      //scriptsView.SelectedNode = GetNode(section);
      _projectNeedSave = true;
      scriptsView.EndUpdate();
    }

    /// <summary>
    /// Exports the currently selected script
    /// </summary>
    private void scriptsView_itemExport_Click(object sender, EventArgs e)
    {
      ExportScript(int.Parse(scriptsView.SelectedNode.Name), true);
    }

    /// <summary>
    /// Moves selected scripts under first script's older sibling.
    /// </summary>
    private void scriptsView_itemMoveIn_Click(object sender, EventArgs e)
    {
      if (scriptsView.SelectedNode == null || scriptsView.SelectedNode.Index == 0)
        return;
      scriptsView.BeginUpdate();
      int section = int.Parse(scriptsView.SelectedNode.Name);
      int parent = GetParentList(section).Section;
      MoveScriptIn(section);
      RestichList(parent);
      scriptsView.SelectedNode = GetNode(section);
      _projectNeedSave = true;
      UpdateNames();
      scriptsView.EndUpdate();
    }

    /// <summary>
    /// Moves selected scripts under first script's older sibling.
    /// </summary>
    private void scriptsView_itemMoveOut_Click(object sender, EventArgs e)
    {
      if (scriptsView.SelectedNode == null || scriptsView.SelectedNode.Level == 0)
        return;
      scriptsView.BeginUpdate();
      int section = int.Parse(scriptsView.SelectedNode.Name);
      int parent = GetParentList(section).Section;
      MoveScriptOut(section);
      RestichList(parent);
      scriptsView.SelectedNode = GetNode(section);
      _projectNeedSave = true;
      UpdateNames();
      scriptsView.EndUpdate();
    }

    /// <summary>
    /// Moves selected scripts up
    /// </summary>
    private void scriptsView_contextMenu_itemMoveUp_Click(object sender, EventArgs e)
    {
      if (scriptsView.SelectedNode == null ||
        (scriptsView.SelectedNode.Level == 0 && scriptsView.SelectedNode.Index == 0) ||
        (scriptsView.SelectedNode.Level > 0 && scriptsView.SelectedNode.Index == 0))
        return;
      scriptsView.BeginUpdate();
      int section = int.Parse(scriptsView.SelectedNode.Name);
      int parent = GetParentList(section).Section;
      MoveScriptUp(section);
      //RebuildFrom(parent);
      RestichList(parent);
      scriptsView.SelectedNode = GetNode(section);
      _projectNeedSave = true;
      UpdateNames();
      scriptsView.EndUpdate();
    }

    /// <summary>
    /// Moves selected scripts down
    /// </summary>
    private void scriptsView_contextMenu_itemMoveDown_Click(object sender, EventArgs e)
    {
      if (scriptsView.SelectedNode == null ||
        (scriptsView.SelectedNode.Level == 0 && scriptsView.SelectedNode.Index == scriptsView.Nodes.Count - 1) ||
        (scriptsView.SelectedNode.Level > 0 && scriptsView.SelectedNode.Index == scriptsView.SelectedNode.Parent.Nodes.Count - 1))
        return;
      scriptsView.BeginUpdate();
      int section = int.Parse(scriptsView.SelectedNode.Name);
      int parent = GetParentList(section).Section;
      MoveScriptDown(section);
      //RebuildFrom(parent);
      RestichList(parent);
      scriptsView.SelectedNode = GetNode(section);
      _projectNeedSave = true;
      UpdateNames();
      scriptsView.EndUpdate();
    }

    /// <summary>
    /// Creates/Focuses the search form for the scripts
    /// </summary>
    private void scriptsView_contextMenu_itemBatchSearch_Click(object sender, EventArgs e)
    {
      ShowSearch();
    }

    /// <summary>
    /// Applies name change to all open documents and script title when text is changed
    /// </summary>
    private void scriptName_TextChanged(object sender, EventArgs e)
    {
      if (_updatingText) return;
      scriptName.Text = _invalidRegex.Replace(scriptName.Text, "");
      scriptName.Select(scriptName.Text.Length - 1, 0);
      int section = int.Parse(scriptsView.SelectedNode.Name);
      if (GetScript(section).Name != scriptName.Text)
      {
        GetScript(section).Name = scriptName.Text;
          UpdateName(section);
        _projectNeedSave = true;
      }
    }

    #endregion Scripts View Events

    /*\
     * ######                     ##                 ##
     * ##   ##                                       ##
     * ##   ##  ######   ######  ###  #####   ###### #######
     * ######  ##    ## ##    ##  ## ##   ## ##      ##
     * ##      ##       ##    ##  ## ####### ##      ##
     * ##      ##       ##    ##  ## ##      ##      ##   ##
     * ##      ##        ######   ##  #####   ######  #####
     * ==========================##=========================
    \*/

    #region Project Methods

    private void CreateProject(string engine, string title, string directory, bool library, bool open)
    {
      try
      {
        directory += @"\";
        if (engine == "RMXP")
        {
          foreach (string dir in Properties.Resources.RMXP_Directories.Split(' '))
            Directory.CreateDirectory(directory + dir);
          string data = "Gemini.files.RMXP.Data.";
          foreach (string resource in Assembly.GetExecutingAssembly().GetManifestResourceNames())
            if (resource.StartsWith(data))
              CopyResource(resource, directory + @"Data\" + resource.Remove(0, data.Length));
          CopyResource("Gemini.files.RMXP.Game.exe", directory + "Game.exe");
          if (library)
            CopyResource("Gemini.files.RMXP.RGSS104E.dll", directory + "RGSS104E.dll");
          File.WriteAllText(directory + "Game.ini", "[Game]\r\nRTP1=Standard\r\nLibrary=RGSS104E.dll\r\nScripts=Data\\Scripts.rxdata\r\nTitle=" + title);
          File.WriteAllText(directory + "Game.rxproj", "RPGXP 1.04");
          if (open)
            OpenProject(directory + "Game.rxproj");
        }
        else if (engine == "RMVX")
        {
          foreach (string dir in Properties.Resources.RMVX_Directories.Split(' '))
            Directory.CreateDirectory(directory + dir);
          string data = "Gemini.files.RMVX.Data.";
          foreach (string resource in Assembly.GetExecutingAssembly().GetManifestResourceNames())
            if (resource.StartsWith(data))
              CopyResource(resource, directory + @"Data\" + resource.Remove(0, data.Length));
          CopyResource("Gemini.files.RMVX.Game.exe", directory + "Game.exe");
          if (library)
            CopyResource("Gemini.files.RMVX.RGSS202E.dll", directory + "RGSS202E.dll");
          File.WriteAllText(directory + "Game.ini", "[Game]\r\nRTP=RPGVX\r\nLibrary=RGSS202E.dll\r\nScripts=Data\\Scripts.rvdata\r\nTitle=" + title);
          File.WriteAllText(directory + "Game.rvproj", "RPGVX 1.00");
          if (open)
            OpenProject(directory + "Game.rvproj");
        }
        else if (engine == "RMVXAce")
        {
          foreach (string dir in Properties.Resources.RMVXAce_Directories.Split(' '))
            Directory.CreateDirectory(directory + dir);
          string data = "Gemini.files.RMVXAce.Data.";
          foreach (string resource in Assembly.GetExecutingAssembly().GetManifestResourceNames())
            if (resource.StartsWith(data))
              CopyResource(resource, directory + @"Data\" + resource.Remove(0, data.Length));
          CopyResource("Gemini.files.RMVXAce.Game.exe", directory + "Game.exe");
          if (library)
            CopyResource("Gemini.files.RMVXAce.RGSS301.dll", directory + @"System\RGSS301.dll");
          File.WriteAllText(directory + "Game.ini", "[Game]\r\nRTP=RPGVXAce\r\nLibrary=System\\RGSS301.dll\r\nScripts=Data\\Scripts.rvdata2\r\nTitle=" + title);
          File.WriteAllText(directory + "Game.rvproj2", "RPGVXAce 1.02");
          if (open)
            OpenProject(directory + "Game.rvproj2");
        }
      }
      catch
      {
        MessageBox.Show("Failed to create new project.\nPlease make sure that you have sufficient privileges to create files at the specified directory.",
            "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
      }
    }

    private void OpenProject(string projectPath)
    {
      if (!CloseProject(true)) return;
      Settings.ProjectDirectory = Path.GetDirectoryName(projectPath) + @"\";
      switch (Path.GetExtension(projectPath))
      {
        case ".rxproj":
          _projectEngine = "RMXP";
          _projectScriptPath = GetScriptsPath();
          _projectScriptsFolderPath = Settings.ProjectDirectory + @"Scripts\";
          break;

        case ".rvproj":
          _projectEngine = "RMVX";
          _projectScriptPath = GetScriptsPath();
          _projectScriptsFolderPath = Settings.ProjectDirectory + @"Scripts\";
          break;

        case ".rvproj2":
          _projectEngine = "RMVXAce";
          _projectScriptPath = GetScriptsPath();
          _projectScriptsFolderPath = Settings.ProjectDirectory + @"Scripts\";
          break;

        case ".rxdata":
          _projectEngine = "RMXP";
          _projectScriptPath = projectPath;
          break;

        case ".rvdata":
          _projectEngine = "RMVX";
          _projectScriptPath = projectPath;
          break;

        case ".rvdata2":
          _projectEngine = "RMVXAce";
          _projectScriptPath = projectPath;
          break;
      }
      if (LoadScripts())
      {
        LoadLocalConfiguration();
        AddRecentProject(projectPath);
        UpdateTitle(projectPath);
        UpdateMenusEnabled();
        UpdateSettingsState();
        UpdateAutoCompleteWords();
        scriptName.TextChanged += new EventHandler(scriptName_TextChanged);
      }
      else
        CloseProject(false);
    }

    private void OpenRecentProject(int id, bool showErrorMessage)
    {
      if (id < 0 || id >= Settings.RecentlyOpened.Count) return;
      string path = Settings.RecentlyOpened[id];
      if (File.Exists(path))
        OpenProject(path);
      else
      {
        if (showErrorMessage)
          MessageBox.Show("File no longer exists and will be removed from the list.",
              "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Settings.RecentlyOpened.RemoveAt(id);
        UpdateRecentProjectList();
      }
    }

    /// <summary>
    /// Adds an entry to the recent file lists, ensuring there are no duplicates
    /// </summary>
    /// <param name="path">The path of the file to add</param>
    private void AddRecentProject(string path)
    {
      if (Settings.RecentlyOpened.Contains(path))
        Settings.RecentlyOpened.Remove(path);
      else if (Settings.RecentlyOpened.Count > 8)
        Settings.RecentlyOpened.RemoveAt(8);
      Settings.RecentlyOpened.Insert(0, path);
      UpdateRecentProjectList();
    }

    private bool CloseProject(bool showSaveMessage)
    {
      if (showSaveMessage && NeedSave())
      {
        DialogResult result = MessageBox.Show("Save changes before closing?",
          "Warning", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
        if (result == DialogResult.Cancel)
          return false;
        else if (result == DialogResult.Yes)
          SaveScripts();
      }

      scriptName.TextChanged -= scriptName_TextChanged;
      scriptsFileWatcher.EnableRaisingEvents = false;

      SaveLocalConfiguration();

      foreach (Script script in _scripts)
        script.Dispose();
      _scripts.Clear();
      _usedSections.Clear();
      scriptsEditor_tabs.TabPages.Clear();
      scriptsView.Nodes.Clear();
      scriptName.ResetText();
      Settings.ProjectDirectory = _projectScriptPath = _projectScriptsFolderPath = _projectEngine = "";
      _projectNeedSave = false;
      _projectLastSave = null;
      UpdateTitle();
      UpdateMenusEnabled();
      return true;
    }

    private bool IsProject(params string[] filenames)
    {
      foreach (string filename in filenames)
      {
        string ext = Path.GetExtension(filename);
        if (ext == ".rxproj" || ext == ".rvproj" || ext == ".rvproj2" ||
            ext == ".rxdata" || ext == ".rvdata" || ext == ".rvdata2")
          return true;
      }
      return false;
    }

    private void SaveConfiguration(bool showMessage)
    {
      try
      {
        Settings.SaveConfiguration();
        SaveLocalConfiguration();
        if (showMessage)
          MessageBox.Show("Configuration was successfully saved.", "Message");
      }
      catch
      {
        if (showMessage)
          MessageBox.Show("An error occurred attempting to save the configuration.\nPlease ensure that you have write access to:\n\t" +
            Application.StartupPath, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
      }
    }

    private void SaveLocalConfiguration()
    {
      if (Settings.ProjectConfig && !string.IsNullOrEmpty(_projectScriptsFolderPath))
      {
        Script s;
        if (GetActiveScript() != null)
          Settings.ActiveScript = new Serializable.Script((s = GetActiveScript()).Section, s.Scintilla.CurrentPos);
        for (int i = 0; i < scriptsEditor_tabs.TabCount; i++)
          Settings.OpenScripts.Add(new Serializable.Script((s =
            _scripts.Find(delegate (Script t) { return t.Opened && t.TabPage == scriptsEditor_tabs.TabPages[i]; })).Section,
            s.Scintilla.CurrentPos));
        Settings.SaveLocalConfiguration();
      }
    }

    private void LoadLocalConfiguration()
    {
      if (Settings.ProjectConfig && !string.IsNullOrEmpty(_projectScriptsFolderPath))
      {
        Settings.SetLocalDefaults();
        Settings.LoadLocalConfiguration();
        if (Settings.OpenScripts.Count > 0)
          foreach (Serializable.Script s in Settings.OpenScripts)
            if (ScriptExists(s.Section))
              OpenScript(s.Section, s.Position);
        if ((Settings.ActiveScript.Section <= 0) && ScriptExists(Settings.ActiveScript.Section))
          scriptsEditor_tabs.SelectedTab = GetScript(Settings.ActiveScript.Section).TabPage;
        Settings.OpenScripts.Clear();
      }
    }

    #endregion Project Methods

    /*\
     *  ######                  ##          ##
     * ##                                   ##
     * ##       ######  ######  ### ######  #######
     *  #####  ##      ##    ## ##  ##   ## ##
     *      ## ##      ##       ##  ##   ## ##
     *      ## ##      ##       ##  ######  ##   ##
     * ######   ###### ##       ##  ##       #####
     * =============================##===========
    \*/

    #region Script Methods

    /// <summary>
    /// Returns the currently active script
    /// </summary>
    /// <returns>The active script if there is one, else null</returns>
    private Script GetActiveScript()
    {
      return _scripts.Find(delegate (Script s) { return s.Opened && s.TabPage == scriptsEditor_tabs.SelectedTab; });
    }

    /// <summary>
    /// Ensure the file exists and is in the proper format, then loads the game's scripts
    /// </summary>
    private bool LoadScripts()
    {
      if (File.Exists(_projectScriptPath))
        try
        {
          LoadScriptsLoop((RubyArray)Ruby.MarshalLoad(_projectLastSave = File.ReadAllBytes(_projectScriptPath)), -1, 1);
          BuildFromRoot();
          _projectNeedSave = false;
          scriptsFileWatcher.Path = Path.GetDirectoryName(_projectScriptPath);
          scriptsFileWatcher.Filter = Path.GetFileName(_projectScriptPath);
          scriptsFileWatcher.EnableRaisingEvents = true;
          return true;
        }
        catch
        {
          MessageBox.Show("An error occurred when loading the scripts.\r\nPlease make sure the data is in the correct format.",
            "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
      MessageBox.Show("Cannot locate script file\r\n" + _projectScriptPath, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
      return false;
    }

    /// <summary>
    ///
    /// </summary>
    /// <param name="rmScripts"></param>
    /// <param name="section">Start section</param>
    /// <param name="i">Script index.</param>
    /// <returns></returns>
    private int LoadScriptsLoop(RubyArray rmScripts, int section, int i)
    {
      // Create a local sections list
      List<int> list = new List<int>();
      // For every script in the array, we process it.
      for (; i < rmScripts.Count; i++)
      {
        // Retrive script from array.
        Script script = new Script((RubyArray)rmScripts[i]);

        // If this script is unnamd AND empty, skip it. Since we don't allow unnamed empty scripts.
        if (i > 0 && string.IsNullOrWhiteSpace(script.Name) && string.IsNullOrWhiteSpace(script.Text))
          break;

        // If the section is NOT in use, add section to the used sections list
        if (!_usedSections.Contains(script.Section))
          _usedSections.Add(script.Section);

        // Add section to local list.
        list.Add(script.Section);

        // If the script name starts with a '▼', we have scripts under it. So load them too.
        if (script.Name.StartsWith("▼ "))
          i = LoadScriptsLoop(rmScripts, script.Section, ++i);

        // Remove the '▼' and all white spaces from script name.
        script.Name = script.Name.Replace("▼ ", "").Trim();
        // Add script to collection.
        _scripts.Add(script);
      }
      // Add section with local list to relation list
      _relations.Add(new ScriptList(section, list));
      // Return current index in array.
      return i;
    }

    private void SaveScripts()
    { SaveScripts(_projectScriptPath); }

    private void SaveScripts(string path)
    {
      if (string.IsNullOrEmpty(path)) return;
      bool saveCopy = path != _projectScriptPath;
      scriptsFileWatcher.EnableRaisingEvents = false;
      // Create a new array and
      RubyArray data = new RubyArray();
      // load our scripts.
      data = SaveScriptLoop(data, -1, saveCopy);
      // Also append an empty script after.
      data.Insert(0, new Script(GetRandomSection(), "", "").RMScript);

      // Try to save
      byte[] save = Ruby.MarshalDump(data);
      try
      {
        File.WriteAllBytes(path, save);
        scriptsFileWatcher.EnableRaisingEvents = true;
      }
      catch
      {
        MessageBox.Show("An error occurred attempting to save the scripts.\nPlease ensure that you have write access to:\n\t" +
            Path.GetDirectoryName(path), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
      }
      if (!saveCopy)
      {
        _projectLastSave = save;
        _projectNeedSave = false;
      }
    }

    private RubyArray SaveScriptLoop(RubyArray data, int root, bool saveCopy)
    {
      // Get our list,
      List<int> list = GetList(root).List;
      // and browse through it.
      foreach (int section in list)
      {
        // Unless we're saving a copy, we save our changes.
        if (!saveCopy)
        {
          GetScript(section).ApplyChanges();
          GetScript(section).NeedSave = false;
        }
        // Get the script.
        Script script = GetScript(section);

        // If current script is unnamed, empty and has no list assosiated with it then skip it,
        // since we don't allow unnamed empty scripts.
        if (string.IsNullOrWhiteSpace(script.Name) && string.IsNullOrWhiteSpace(script.Text) && !ListExists(section))
          continue;

        // Get the array.
        RubyArray rmScript = script.RMScript;
        
        // If script is assosiated with a list, create a new loop.
        if (ListExists(section))
        {
          // Append '▼' in front of listed script
          rmScript[1] = Ruby.ConvertString("▼ " + GetScript(section).Name);
          // before adding it to the list.
          data[data.Count] = rmScript;
          // Afterwards run next loop.
          data = SaveScriptLoop(data, section, saveCopy);
          // Always add an empty script after a listed script.
          data[data.Count] = new Script(GetRandomSection(), "", "").RMScript;
        }
        // If not, just add it.
        else
          data[data.Count] = rmScript;
      }

      // Return data, to either previous loop or origin.
      return data;
    }

    /// <summary>
    /// Inserts passed script into parent list.
    /// If index is provided, inserts at passed index, else adds script at bottom of the list.
    /// </summary>
    /// <returns>Section of new script</returns>
    private int InsertScript(int parent, int index, params object[] args)
    {
      // If parent doesn't exist, we can't use it.
      if (parent != -1 && !ScriptExists(parent))
        throw new ArgumentOutOfRangeException("Parent doesn't exist.");

      Script script;
      // Accept 1 argument,
      if (args.Length == 1)
        script = new Script((RubyArray)args[0]);
      // 2 arguments,
      else if (args.Length == 2)
        script = new Script(GetRandomSection(), (string)args[0], (string)args[1]);
      // or 3 arguments.
      else if (args.Length == 3)
        script = new Script(int.Parse((string)args[0]), (string)args[1], (string)args[2]);
      // For higher number, throw invald.
      else throw new Exception("No valid script passed");
      
      // Throw if we not have 2 arguments and section is in use.
      if (args.Length != 2 && _usedSections.Contains(script.Section))
        throw new InvalidCastException("Section already used.");

      // Trim name if nessecary.
      script.Name = script.Name.Trim().Replace("▼ ", "");

      // We may have already added it, but if not add section to list.
      if (!_usedSections.Contains(script.Section))
        _usedSections.Add(script.Section);
      // Add script to collection.
      _scripts.Add(script);
      
      // Create list if nessecary and get list.
      CreateListFor(parent);
      ScriptList list = GetList(parent);
      // If index is out of range, add script to bottom.
      if (index < 0 || index >= list.List.Count)
        list.List.Add(script.Section);
      // Else insert script at desired index.
      else
        list.List.Insert(index, script.Section);

      // Always return section of new script.
      return script.Section;
    }

    /// <summary>
    /// Removes script and relations for given section.
    /// </summary>
    /// <param name="section"></param>
    private void RemoveScript(int section)
    {
      if (ScriptExists(section))
      {
        // If we got children, ask what to do.
        if (ListExists(section))
        {
          DialogResult result = MessageBox.Show("Do you want to delete all scripts under selected script too?",
            "Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
          
          if (result == DialogResult.No)
            MoveOutAllScripts(section);
          else
            RemoveList(section);
        }
        
        GetParentList(section).List.Remove(section);
        _scripts.Remove(GetScript(section));
        _usedSections.Remove(section);
      }
    }
    
    /// <summary>
    /// Creates a new tab
    /// </summary>
    /// <param name="section">The script that will be loaded into the page</param>
    /// <param name="position">The cursor position in the script</param>
    private void OpenScript(int section, int position = 0)
    {
      Script script = GetScript(section);
      if (!script.Opened)
      {
        script.Scintilla.ContextMenuStrip = scriptsEditor_ContextMenuStrip;
        script.Scintilla.SelectionChanged += new EventHandler(Scintilla_Changed);
        if (position >= 0 && position < script.Scintilla.TextLength)
          script.Scintilla.CurrentPos = position;
        script.Scintilla.TextChanged += new EventHandler<EventArgs>(Scintilla_Changed);
        scriptsEditor_tabs.TabPages.Add(script.TabPage);
      }
      scriptsEditor_tabs.SelectedTab = script.TabPage;
      UpdateMenusEnabled();
    }

    /// <summary>
    /// Get the <see cref="Script"/> by the given <paramref name="section"/>
    /// </summary>
    /// <param name="section">Section to locate script from</param>
    /// <returns>The desired <see cref="Script"/> or <see cref="Nullable"/></returns>
    private Script GetScript(int section)
    {
      return _scripts.Find(delegate (Script s) { return s.Section == section; });
    }

    private void SetScript(Script s)
    {
      if (_scripts.Exists(delegate (Script d) { return d.Section == s.Section; }))
        _scripts[_scripts.FindIndex(delegate (Script d) { return d.Section == s.Section; })] = s;
    }

    private void SetScript(int section, string name, string value)
    {
      Script script = GetScript(section);

      if (script.Name != name.Trim())
        script.Name = name.Trim();
      script.Scintilla.Text = value;

      script.ApplyChanges();
      if (!script.Opened)
        script.Dispose();
    }

    private bool ScriptExists(int section)
    {
      // If section is used, our script exists.
      return _usedSections.Contains(section);
    }

    /// <summary>
    /// Moves a single script past its older sibling.
    /// </summary>
    private bool MoveScriptUp(int section)
    {
      ScriptList parent = GetParentList(section);

      // To move up, we need an older sibling.
      if (parent.List[0] == section)
        return false;

      int sibling = parent.List[parent.List.IndexOf(section) - 1];

      parent.List.Remove(section);

      parent.List.Insert(parent.List.IndexOf(sibling), section);

      return true;
    }

    /// <summary>
    /// Moves a single script past its younger sibling.
    /// </summary>
    private bool MoveScriptDown(int section)
    {
      ScriptList parent = GetParentList(section);

      // To move down, we need a younger sibling.
      if (parent.List[parent.List.Count - 1] == section)
        return false;

      int sibling = parent.List[parent.List.IndexOf(section) + 1];

      parent.List.Remove(section);

      parent.List.Insert(parent.List.IndexOf(sibling) + 1, section);

      return true;
    }

    /// <summary>
    /// Moves a single script into its older siblings' list, creating it if nonexistent.
    /// </summary>
    private bool MoveScriptIn(int section)
    {
      ScriptList parent = GetParentList(section);

      // To move in, we need an older sibling.
      if (parent.List[0] == section)
        return false;

      int sibling = parent.List[parent.List.IndexOf(section) - 1];

      parent.List.Remove(section);

      // Create a list for sibling if none exists,
      CreateListFor(sibling);
      // and insert section first.
      GetList(sibling).List.Insert(0, section);

      return true;
    }

    /// <summary>
    /// Moves a single script from curent list to its parents list.
    /// </summary>
    private bool MoveScriptOut(int section)
    {
      ScriptList parent = GetParentList(section);

      // To move out, we need a grandparent.
      if (parent.Section == -1)
        return false;

      ScriptList grandParent = GetParentList(parent.Section);

      parent.List.Remove(section);

      grandParent.List.Insert(GetIndexInList(parent.Section) + 1, section);

      return true;
    }

    /// <summary>
    ///Moves all scripts from a list to parent list.
    /// </summary>
    private bool MoveOutAllScripts(int section)
    {
      // If we don't have children, bail.
      if (!ListExists(section))
        return false;

      ScriptList parent = GetParentList(section);
      parent.List.InsertRange(parent.List.IndexOf(section), GetList(section).List);
      parent.List.Remove(section);

      // Since we just emptied our list, we can safely remove it.
      RemoveList(section);

      return true;
    }

    /// <summary>
    /// Imports the scripts from the given paths
    /// </summary>
    /// <param name="paths">A string array with paths to import</param>
    /// <returns>The section of the first imported script.</returns>
    private int ImportScriptsFrom(int parent, int index, params string[] paths)
    {
      int section = -1;

      // TODO: Reimplement logic...

      scriptsView.BeginUpdate();
      for (int i = 0; i < paths.Length; i++)
      {
        string path = paths[i];

        if (File.Exists(path))
          try
          {
            // TODO: Reimplement logic...

            // Something like:
            //if (i == 0)
            //  section = InsertScript();
            //else
            //  InsertScript();
          }
          catch
          {
            MessageBox.Show("There was an error while importing from '" + path + "'.",
              "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
          }
      }
      scriptsView.EndUpdate();
      _projectNeedSave = true;

      // Return the section of the first imported script.
      return section;
    }

    /// <summary>
    /// Exports the scripts using the passed filed extension to determine the file type
    /// </summary>
    /// <param name="extension">The extension to save the files as</param>
    private void ExportScriptsTo(string extension)
    {
      using (FolderBrowserDialog dialog = new FolderBrowserDialog())
      {
        dialog.ShowNewFolderButton = true;
        dialog.RootFolder = Environment.SpecialFolder.MyDocuments;
        dialog.Description = "Choose folder...";
        if (dialog.ShowDialog() == DialogResult.OK)
        {
          ExportScripts(dialog.SelectedPath, extension);
        }
      }
    }

    private void DeleteExportedScript(int section)
    {
      try
      {
        Script script = GetScript(section);
        if (File.Exists(_projectScriptsFolderPath + script.Name + "." + script.Section.ToString("{0:XXXXXXX}") + ".rb"))
          File.Delete(_projectScriptsFolderPath + script.Name + "." + script.Section.ToString("{0:XXXXXXX}") + ".rb");
      }
      catch
      {
        MessageBox.Show("An error occurred while deleting script-file.",
          "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
      }
    }

    /// <summary>
    /// Export the desired <see cref="Script"/> to a file in the Project Script Folder
    /// </summary>
    /// <param name="section">The <see cref="Script.Section"/> to use</param>
    /// <param name="prompt"></param>
    private void ExportScript(int section, bool prompt = false)
    {
      ExportScript(section, false, prompt);
    }

    /// <summary>
    /// Export the desired <see cref="Script"/>,
    /// and all the underlying <see cref="Script"/>s if bool <paramref name="alsoChildren"/> is true and an <see cref="ScriptList"/> root <see cref="ScriptList.Section"/> is passed,
    /// to file in the Project Scripts Folder.
    /// </summary>
    /// <param name="section">The <see cref="Script.Section"/> to use</param>
    /// <param name="alsoChildren"></param>
    /// <param name="promt"></param>
    /// <param name="dirPath">Used when reclusivly creating folders</param>
    /// <param name="dirRootPath">To be used if exporting to another folder than default</param>
    private void ExportScript(int section, bool alsoChildren, bool promt = false, string dirPath = null, string dirRootPath = null, string extension = ".rb")
    {
      //Return if script do not exist
      if (!ScriptExists(section))
        return;

      // Set root path for exporting if not set
      if (string.IsNullOrEmpty(dirRootPath))
        dirRootPath = _projectScriptsFolderPath;

      //TODO: A must-do: add level-control, as in create folders to suit path... or something simular
      // Set relativ path for exporting if not set
      if (string.IsNullOrEmpty(dirPath))
        dirPath = _projectScriptsFolderPath;

      Script script = GetScript(section);
      string name = script.Name + "." + script.Section.ToString("{0:00000000}");

      // Create directory if it does not exist
      if (!Directory.Exists(dirRootPath))
        Directory.CreateDirectory(dirRootPath);

      //Write to file and catch exeptions if any
      try { File.WriteAllText(dirPath + name + ".rb", script.Text); }
      catch (Exception e)
      {
        Debug.Write(e);
        MessageBox.Show("An error occurred while exporting the script;\n'" + name + ".rb'",
          "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
      }

      // If enabled and script has sub-scripts, create a directory and add sub-scripts reclusivly
      if (alsoChildren && ListExists(section))
      {
        string nxtDirPath = dirPath + name + @"\";
        // Create script-spesified directory
        if (!Directory.Exists(nxtDirPath))
          try { Directory.CreateDirectory(nxtDirPath); }
          catch (Exception e)
          {
            Debug.Write(e);
            MessageBox.Show("An error occurred while creating script directory;\n'" + nxtDirPath,
              "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
          }

        ScriptList list = GetList(section);

        list.List.ForEach(delegate (int s) { ExportScript(s, true, false, nxtDirPath, dirRootPath); });
      }
    }

    /// <summary>
    /// Exports all the <see cref="Script"/>s to the Project Scripts Folder
    /// </summary>
    private void ExportScripts(string dirRootPath = null, string extension = ".rb")
    {
      _scripts.ForEach(delegate (Script s) { ExportScript(s.Section, true, false, null, dirRootPath, extension); });
    }

    #endregion Script Methods

    /*\

     * ##       ##          ##
     * ##                   ##
     * ##       ###  ###### #######
     * ##       ##  ##      ##
     * ##       ##   #####  ##
     * ##       ##       ## ##   ##
     * ######## ##  ######   #####
    \*/

    #region List Methods

    /// <summary>
    /// Get the <see cref="ScriptList"/> by the given <paramref name="section"/>
    /// </summary>
    /// <param name="section">Section to locate list from</param>
    /// <returns>The desired <see cref="ScriptList"/> or <see cref="Nullable"/></returns>
    private ScriptList GetList(int section)
    {
      return _relations.Find(delegate (ScriptList s) { return s.Section == section; });
    }

    /// <summary>
    /// Get the <see cref="ScriptList"/> by the given <paramref name="section"/>
    /// </summary>
    /// <param name="section">Section to locate list from</param>
    /// <returns>The desired <see cref="ScriptList"/> or <see cref="Nullable"/></returns>
    private ScriptList GetParentList(int section)
    {
      return _relations.Find(delegate (ScriptList s) { return s.List.Contains(section); });
    }

    /// <summary>
    /// Retrives the index of given section in parent list.
    /// Relies on GetParentList.
    /// </summary>
    /// <param name="section">Section to locate list from</param>
    private int GetIndexInList(int section)
    {
      return GetParentList(section).List.IndexOf(section);
    }

    /// <summary>
    /// Adds an empty list for given section to collection.
    /// </summary>
    /// <param name="section">Section to locate list from</param>
    private void CreateListFor(int section)
    {
      AddListFor(section, new List<int>());
    }

    /// <summary>
    /// Adds a list for given section to collection.
    /// </summary>
    /// <param name="section">Section to locate list from</param>
    private void AddListFor(int section, List<int> list)
    {
      if (!ListExists(section))
        _relations.Add(new ScriptList(section, list));
    }

    /// <summary>
    /// Removes a list from collection.
    /// </summary>
    /// <param name="section">Section to locate list from</param>
    private void RemoveList(int section)
    {
      _relations.RemoveAll(delegate (ScriptList l) { return l.Section == section; });
    }

    /// <summary>
    /// Determines if the list for given section exsists.
    /// </summary>
    /// <param name="section">Section to locate list from</param>
    private bool ListExists(int section)
    {
      return _relations.Exists(delegate (ScriptList d) { return d.Section == section; });
    }

    /// <summary>
    /// Determines if the list for given section is empty.
    /// </summary>
    /// <param name="section">Section to locate list from</param>
    private bool ListEmpty(int section)
    {
      return GetList(section).List.Count == 0;
    }

    #endregion List Methods

    /*\
     * ##    ##               ##
     * ###   ##               ##
     * ####  ##  ######   ######  #####
     * ## ## ## ##    ## ##   ## ##   ##
     * ##  #### ##    ## ##   ## #######
     * ##   ### ##    ## ##   ## ##
     * ##    ##  ######   ######  #####
    \*/

    #region Node Methods

    /// <summary>
    /// Rebuilds view for section, creating new and discarding unwanted nodes.
    /// </summary>
    private void RestichList(int section)
    {
      RestichList(section, new List<int>());
    }

    /// <summary>
    /// Rebuilds view for section, creating new and discarding unwanted nodes.
    /// </summary>
    private void RestichList(int section, List<int> ran)
    {
      // If we already ran this loop, abort.
      if (ran.Contains(section))
        return;
      // Add current run to list
      ran.Add(section);

      if (!ListExists(section))
        return;


      TreeNodeCollection parent;
      // If we're in the root, we don't need a check.
      if (section == -1)
      {
        parent = scriptsView.Nodes;
      }
      // If we're not in the root, we need to check for our parent first.
      else
      {
        // Bail if node doesn't exist. If we haven't made it yet, it can't exist.
        if (!NodeExists(section))
          return;

        parent = GetNode(section).Nodes;

      }

      // Bail if parent has virtual node, because it is not populated yet.
      if (parent.Count == 1 && parent[0].Name == VIRTUALNODE)
        return;

      List<int> list = GetList(section).List;
      List<int> missing = new List<int>(list);
      List<int> overflow = new List<int>();
      List<TreeNode> nodes = new List<TreeNode>();

      // Browse through current children of parent
      foreach (TreeNode child in parent)
      {
        // Skip unwanted children.
        if (child == null) continue;

        int childSection = int.Parse(child.Name);

        // If current child is part of list,
        if (list.Contains(childSection))
        {
          // add it to node collection,
          nodes.Add(child);
          // and exclude it as missing.
          missing.Remove(childSection);
        }
        // If not part of list, we got an overflow.
        else
        {
          // For now add to collection.
          overflow.Add(childSection);
        }
      }

      // Since we may have emptied our list, we check...
      if (ListEmpty(section))
        RemoveList(section);

      // Disolve previouse family.
      parent.Clear();

      // For every lost child, create a replacement.
      foreach (int lost in missing)
      {
        nodes.Add(CreateNode(lost));
      }

      // Reconstruct the view in order.
      for (int i = 0; i < list.Count; i++)
      {
        // Find our child and extract it from collection.
        TreeNode child = nodes.Find(delegate (TreeNode c) { return int.Parse(c.Name) == list[i]; });

        parent.Add(child);
        nodes.Remove(child);
      }
      
      // Restich overflowed parts.
      foreach (int flow in overflow)
      {
        // Only if script still exists do we restich.
        if (ScriptExists(flow))
          RestichList(GetParentList(flow).Section, ran);
      }
    }

    /// <summary>
    /// Rebuilds view from section to deepest level.
    /// </summary>
    /// <param name="section"></param>
    /// <param name="recurse"></param>
    private void RebuildFrom(int section)
    {
      // If we're in the root, we do it differently.
      if (section == -1)
      {
        BuildFromRoot();
        return;
      }

      // Bail if list doesn't exist.
      if (!ListExists(section))
        return;

      // Bail if node doesn't exist.
      if (!NodeExists(section))
        return;

      // Get the list,
      List<int> list = GetList(section).List;

      // get the parent node,
      TreeNode parent = GetNode(section);

      TreeNode child;
      // and create each child node for parent.
      foreach (int sec in list)
      {
        child = CreateNode(sec);

        parent.Nodes.Add(child);

        // If child got children, add a placeholder until loaded.
        if (ListExists(sec))
          child.Nodes.Add(CreateVNode());
      }
    }

    /// <summary>
    /// Builds view from root.
    /// </summary>
    private void BuildFromRoot()
    {
      List<int> list = GetList(-1).List;

      TreeNode child;
      foreach (int section in list)
      {
        child = CreateNode(section);

        scriptsView.Nodes.Add(child);

        // If child got children, add a placeholder until loaded.
        if (ListExists(section))
          child.Nodes.Add(CreateVNode());
      }
    }

    private const string VIRTUALNODE = "VIRT";

    /// <summary>
    /// Creates and returns a new VIRTUALNODE node. Normally not visible.
    /// </summary>
    private TreeNode CreateVNode()
    {
      TreeNode node = new TreeNode();
      node.Text = "Loading...";
      node.Name = VIRTUALNODE;
      node.ForeColor = Color.Blue;
      node.NodeFont = new Font("Microsoft Sans Serif", 8.25F, FontStyle.Underline);
      return node;
    }

    /// <summary>
    /// Creates a new node for given section.
    /// As this method will always be fired after a script check, we don't manually check if script exists.
    /// </summary>
    /// <param name="section">Section for script</param>
    private TreeNode CreateNode(int section)
    {
      Script script = GetScript(section);
      TreeNode node = new TreeNode();
      node.Name = section.ToString();
      node.Text = script.TabName;
      node.ToolTipText = script.Name + " - " + section.ToString();

      return node;
    }

    private TreeNode GetNode(int section)
    {
      // If we request the root, return top node.
      if (section == -1)
        return scriptsView.TopNode;

      ScriptList parentList = GetParentList(section);

      // If we're at the root, we do it differently.
      if (parentList.Section == -1)
        return scriptsView.Nodes.Find(section.ToString(), false)[0]; ;

      // Get parent,
      TreeNode parent = GetNode(parentList.Section);
      // check if parent has virtual children, and bail if true,
      if (parent.Nodes.Count == 1 && parent.Nodes[0].Name == VIRTUALNODE)
        return null;
      // else return our child.
      return parent.Nodes.Find(section.ToString(), false)[0];
    }

    private bool NodeExists(int section)
    {
      ScriptList parentList = GetParentList(section);

      TreeNodeCollection parentCollection;
      // If we're at the root,
      if (parentList.Section == -1)
      {
        // we directly assign our collection.
        parentCollection = scriptsView.Nodes;
      }
      else
      {
        // If we don't have a parent, we don't exist.
        if (!NodeExists(parentList.Section))
          return false;

        // We found a parent, so retrive its collection.
        parentCollection = GetNode(parentList.Section).Nodes;
      }

      // Search for our child.
      if (!parentCollection.ContainsKey(section.ToString()))
        return false;

      return true;
    }

    #endregion Node Methods

    /*\
     *  ###### ##   ##          ##                                     ##
     * ##      ##               ##                                     ##
     * ##      ##   ### ######  ##       ######   #####   ######   ######
     * ##      ##   ##  ##   ## ######  ##    ##      ## ##    ## ##   ##
     * ##      ##   ##  ##   ## ##   ## ##    ##  ###### ##       ##   ##
     * ##      ##   ##  ######  ##   ## ##    ## ##   ## ##       ##   ##
     *  ######  ### ##  ##      ######   ######  ####### ##        ######
     * =================##===============================================
    \*/

    #region Clipboard Methods

    /// <summary>
    /// Get a script from the clipboard
    /// </summary>
    /// <returns>A script if there is one, else null</returns>
    private RubyArray GetClipboardScript()
    {
      String format = ClipboardContainsScript();
      if (format != null)
        try
        {
          MemoryStream stream = (MemoryStream)System.Windows.Forms.Clipboard.GetData(format);
          byte[] data = new byte[4];
          stream.Read(data, 0, data.Length);
          data = new byte[BitConverter.ToInt32(data, 0)];
          stream.Read(data, 0, data.Length);
          return (RubyArray)Ruby.MarshalLoad(data);
        }
        catch
        {
          MessageBox.Show("Clipboard error.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
      return null;
    }

    /// <summary>
    /// Set the passed script to the clipboard in the current project format
    /// </summary>
    /// <param name="script">The script to copy</param>
    private void SetClipboardScript(Script script)
    {
      RubyArray rmScript = script.RMScript;
      if (script.NeedApplyChanges)
        rmScript = new RubyArray() { rmScript[0], rmScript[1], Ruby.ZlibDeflate(script.Text) };
      byte[] data = Ruby.MarshalDump(rmScript);
      MemoryStream stream = new MemoryStream();
      stream.Write(BitConverter.GetBytes(data.Length), 0, 4);
      stream.Write(data, 0, data.Length);
      string format = "";
      if (_projectEngine == "RMXP") format = "RPGXP SCRIPT";
      else if (_projectEngine == "RMVX") format = "RPGVX SCRIPT";
      else if (_projectEngine == "RMVXAce") format = "VX Ace SCRIPT";
      System.Windows.Forms.Clipboard.SetData(format, stream);
    }

    /// <summary>
    /// Check if the clipboard contains a script
    /// </summary>
    /// <returns>The script format if there is one, else null</returns>
    private string ClipboardContainsScript()
    {
      foreach (string format in new string[] { "RPGXP SCRIPT", "RPGVX SCRIPT", "VX Ace SCRIPT" })
        if (System.Windows.Forms.Clipboard.ContainsData(format))
          return format;
      return null;
    }

    #endregion Clipboard Methods

    /*\
     *  ######                                  ##
     * ##                                       ##
     * ##       #####   #####   ######   ###### ## ####
     *  #####  ##   ##      ## ##    ## ##      ###   ##
     *      ## #######  ###### ##       ##      ##    ##
     *      ## ##      ##   ## ##       ##      ##    ##
     * ######   #####  ####### ##        ###### ##    ##
     * =================================================
    \*/

    #region Search Methods

    private void ShowFind()
    {
      Script script = GetActiveScript();
      if (script == null) return;
      _findReplaceDialog.Scintilla = script.Scintilla;
      if (!_findReplaceDialog.Visible)
        _findReplaceDialog.Show(this);
      _findReplaceDialog.tabAll.SelectedTab = _findReplaceDialog.tabAll.TabPages["tpgFind"];
      ScintillaNet.Range selRange = _findReplaceDialog.Scintilla.Selection.Range;
      if (selRange.IsMultiLine)
        _findReplaceDialog.chkSearchSelectionF.Checked = true;
      else if (selRange.Length > 0)
        _findReplaceDialog.cboFindF.Text = selRange.Text;
      _findReplaceDialog.cboFindF.Select();
      _findReplaceDialog.cboFindF.SelectAll();
    }

    private void ShowReplace()
    {
      Script script = GetActiveScript();
      if (script == null) return;
      _findReplaceDialog.Scintilla = script.Scintilla;
      if (!_findReplaceDialog.Visible)
        _findReplaceDialog.Show(this);
      _findReplaceDialog.tabAll.SelectedTab = _findReplaceDialog.tabAll.TabPages["tpgReplace"];
      ScintillaNet.Range selRange = _findReplaceDialog.Scintilla.Selection.Range;
      if (selRange.IsMultiLine)
        _findReplaceDialog.chkSearchSelectionR.Checked = true;
      else if (selRange.Length > 0)
        _findReplaceDialog.cboFindR.Text = selRange.Text;
      _findReplaceDialog.cboFindR.Select();
      _findReplaceDialog.cboFindR.SelectAll();
    }

    private void ShowSearch()
    {
      TabPage tabPage = new TabPage("New Search");
      SearchControl searchControl = new SearchControl();
      searchControl.toolStripButton_Search.Click += new EventHandler(searches_ToolStripButton_Click);
      searchControl.listView_Results.ItemActivate += new EventHandler(searches_ListView_ItemActivate);
      tabPage.Controls.Add(searchControl);
      searches_TabControl.TabPages.Add(tabPage);
      searches_TabControl.SelectedTab = tabPage;
      splitView.Panel2Collapsed = false;
      if (splitView.Panel2.ClientSize.Height < 200)
        splitView.SplitterDistance -= 200 - splitView.Panel2.ClientSize.Height;
      searchControl.toolStripTextBox_SearchString.Focus();
    }

    private void Search(SearchControl control)
    {
      // Set appropriate flag
      SearchFlags flags = SearchFlags.Empty;
      if (control.toolStripMenuItem_RegExp.Checked)
        flags |= SearchFlags.RegExp;
      if (control.toolStripMenuItem_MatchCase.Checked)
        flags |= SearchFlags.MatchCase;
      if (control.toolStripMenuItem_WholeWord.Checked)
        flags |= SearchFlags.WholeWord;
      if (control.toolStripMenuItem_WordStart.Checked)
        flags |= SearchFlags.WordStart;
      // Determine search location
      List<Script> searchLocation = new List<Script>();
      if (control.toolStripComboBox_Scope.SelectedIndex == 0)
      {
        Script script = GetActiveScript();
        if (script != null)
          searchLocation.Add(script);
      }
      else if (control.toolStripComboBox_Scope.SelectedIndex == 1)
      {
        foreach (Script script in _scripts)
          if (script.Opened)
            searchLocation.Add(script);
      }
      else
        searchLocation = _scripts;
      // Execute search
      if (searchLocation.Count > 0)
      {
        control.listView_Results.Items.Clear();
        control.Parent.Text = control.toolStripTextBox_SearchString.Text;
        control.label_Statistics.Text = "Searching...";
        control.label_Statistics.Update();
        int scriptCount = 0;
        Enabled = false;
        foreach (Script script in searchLocation)
        {
          SearchResult[] results = script.Search(control.toolStripTextBox_SearchString.Text, flags);
          if (results.Length > 0)
          {
            scriptCount++;
            control.listView_Results.Items.AddRange(results);
            control.label_Statistics.Text = string.Format(@"{0} result{1} found in {2} script{3}.",
                control.listView_Results.Items.Count, control.listView_Results.Items.Count > 1 ? "s" : "",
                scriptCount, scriptCount > 1 ? "s" : "");
            control.label_Statistics.Update();
          }
        }
        Enabled = true;
        if (scriptCount == 0)
          control.label_Statistics.Text = "No matching results were found in the search.";
      }
      else
        control.label_Statistics.Text = "There is currently no open document to search.";
    }

    #endregion Search Methods

    /*\
     * ##     ## ##
     * ###   ###
     * #### #### ###  ######  ######
     * ## ### ## ##  ##      ##
     * ##     ## ##   #####  ##
     * ##     ## ##       ## ##      ###
     * ##     ## ##  ######   ###### ###
     * =================================
    \*/

    #region Misc Methods

    /// <summary>
    /// Copies an embedded resource to an external place on the hard-drive
    /// </summary>
    /// <param name="resource">Rescource to copy</param>
    /// <param name="path">The path the resource will be saved to</param>
    private void CopyResource(string resource, string path)
    {
      using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
      using (FileStream resourceFile = new FileStream(path, FileMode.Create))
      {
        if (s == null) return;
        byte[] b = new byte[s.Length + 1];
        s.Read(b, 0, Convert.ToInt32(s.Length));
        resourceFile.Write(b, 0, Convert.ToInt32(b.Length - 1));
        resourceFile.Flush();
      }
    }

    /// <summary>
    /// Reads the game's Game.ini file and retrieves the title of the game, then returns it
    /// </summary>
    private string GetGameTitle()
    {
      string ini = Settings.ProjectDirectory + "Game.ini";
      if (File.Exists(ini))
        foreach (string line in File.ReadAllLines(ini))
          if (line.StartsWith("Title="))
            return line.Replace("Title=", "").Trim();
      return "Untitled";
    }

    /// <summary>
    /// Reads the game's Game.ini file and retrieves the scripts path, then returns it
    /// </summary>
    private string GetScriptsPath()
    {
      string ini = Settings.ProjectDirectory + "Game.ini";
      if (File.Exists(ini))
        foreach (string line in File.ReadAllLines(ini))
          if (line.StartsWith("Scripts="))
            return Settings.ProjectDirectory + line.Replace("Scripts=", "").Trim();
      if (_projectEngine == "RMXP") return Settings.ProjectDirectory + @"Data\Scripts.rxdata";
      else if (_projectEngine == "RMVX") return Settings.ProjectDirectory + @"Data\Scripts.rvdata";
      else if (_projectEngine == "RMVXAce") return Settings.ProjectDirectory + @"Data\Scripts.rvdata2";
      return "";
    }

    /// <summary>
    /// Retrieves a non-repeatable random integer
    /// </summary>
    /// <returns>Random integer</returns>
    private int GetRandomSection()
    {
      Random random = new Random();
      int section;
      do section = random.Next(99999999);
      while (_usedSections.Contains(section));
      _usedSections.Add(section);
      return section;
    }

    private bool NeedSave()
    {
      if (_projectNeedSave)
        return true;
      foreach (Script script in _scripts)
        if (script.NeedSave)
          return true;
      return false;
    }

    #endregion Misc Methods

    /*\
     * ##     ##              ##         ##
     * ##     ##              ##         ##
     * ##     ## ######       ##  #####  #######  #####
     * ##     ## ##   ##  ######      ## ##      ##   ##
     * ##     ## ##   ## ##   ##  ###### ##      #######
     * ##     ## ######  ##   ## ##   ## ##   ## ##
     *  #######  ##       ###### #######  #####   #####
     * ==========##=====================================
    \*/

    #region Update Methods

    private void UpdateAutoCompleteWords()
    {
      string words = "";
      if ((Settings.AutoCompleteFlag & (1 << 0)) != 0)
      {
        if (_projectEngine == "RMXP") words += global::Gemini.Properties.Resources.Ruby181_Constants + " ";
        else if (_projectEngine == "RMVX") words += global::Gemini.Properties.Resources.Ruby181_Constants + " ";
        else if (_projectEngine == "RMVXAce") words += global::Gemini.Properties.Resources.Ruby192_Constants + " ";
      }
      if ((Settings.AutoCompleteFlag & (1 << 1)) != 0)
        words += global::Gemini.Properties.Resources.Ruby_Keywords + " ";
      if ((Settings.AutoCompleteFlag & (1 << 2)) != 0)
      {
        if (_projectEngine == "RMXP") words += global::Gemini.Properties.Resources.Ruby181_KernelFunctions + " ";
        else if (_projectEngine == "RMVX") words += global::Gemini.Properties.Resources.Ruby181_KernelFunctions + " ";
        else if (_projectEngine == "RMVXAce") words += global::Gemini.Properties.Resources.Ruby192_KernelFunctions + " ";
      }
      if ((Settings.AutoCompleteFlag & (1 << 3)) != 0)
      {
        if (_projectEngine == "RMXP") words += global::Gemini.Properties.Resources.RMXP_Constants + " ";
        else if (_projectEngine == "RMVX") words += global::Gemini.Properties.Resources.RMVX_Constants + " ";
        else if (_projectEngine == "RMVXAce") words += global::Gemini.Properties.Resources.RMVXAce_Constants + " ";
      }
      if ((Settings.AutoCompleteFlag & (1 << 4)) != 0)
      {
        if (_projectEngine == "RMXP") words += global::Gemini.Properties.Resources.RMXP_Globals + " ";
        else if (_projectEngine == "RMVX") words += global::Gemini.Properties.Resources.RMVX_Globals + " ";
        else if (_projectEngine == "RMVXAce") words += global::Gemini.Properties.Resources.RMVXAce_Globals + " ";
      }
      if ((Settings.AutoCompleteFlag & (1 << 5)) != 0)
        words += Settings.AutoCompleteCustomWords;
      Settings.AutoCompleteWords.Clear();
      foreach (string word in words.Split(' '))
        if (word.Length != 0 && !Settings.AutoCompleteWords.Contains(word))
          Settings.AutoCompleteWords.Add(word);
      Settings.AutoCompleteWords.Sort();
    }

    private void UpdateTitle(string projectPath = "")
    {
      if (_projectEngine == "")
        Text = "Gemini";
      else if (_projectScriptsFolderPath == "")
        Text = _projectEngine + " - " + projectPath;
      else
        Text = _projectEngine + " - " + GetGameTitle() + " - " + projectPath;
      Bitmap icon = null;
      if (_projectEngine == "RMXP") icon = Properties.Resources.rmxp_script;
      else if (_projectEngine == "RMVX") icon = Properties.Resources.rmvx_script;
      else if (_projectEngine == "RMVXAce") icon = Properties.Resources.rmvxace_script;
      menuMain_dropFile_itemExoprtToRMData.Image = icon;
    }

    private void UpdateRecentProjectList()
    {
      while (menuMain_dropFile_itemOpenRecent.DropDownItems.Count > 0)
        menuMain_dropFile_itemOpenRecent.DropDownItems.RemoveAt(0);
      foreach (string path in Settings.RecentlyOpened)
      {
        string ext = Path.GetExtension(path);
        Bitmap icon = null;
        if (ext == ".rxproj") icon = Properties.Resources.rmxp_icon;
        else if (ext == ".rvproj") icon = Properties.Resources.rmvx_icon;
        else if (ext == ".rvproj2") icon = Properties.Resources.rmvxace_icon;
        else if (ext == ".rxdata") icon = Properties.Resources.rmxp_script;
        else if (ext == ".rvdata") icon = Properties.Resources.rmvx_script;
        else if (ext == ".rvdata2") icon = Properties.Resources.rmvxace_script;
        ToolStripMenuItem item = new ToolStripMenuItem(path, icon, new EventHandler(mainMenu_ToolStripMenuItem_OpenRecentProject_Click));
        menuMain_dropFile_itemOpenRecent.DropDownItems.Add(item);
      }
    }

    private void UpdateMenusEnabled()
    {
      Script script = GetActiveScript();
      bool project = _projectEngine != "";
      bool scriptsFolder = project && _projectScriptsFolderPath != "";
      bool editor = project && script != null;
      bool editorSelection = editor && script.Scintilla.Selection.Length > 0;
      bool editorUndo = editor && script.Scintilla.UndoRedo.CanUndo;
      bool editorRedo = editor && script.Scintilla.UndoRedo.CanRedo;
      bool editorPaste = editor && script.Scintilla.Clipboard.CanPaste;
      bool viewSelection = project && scriptsView.SelectedNode != null;
      // TODO: Fix booleans here
      bool viewMoveUp = viewSelection && (scriptsView.SelectedNode.Index > 0);
      bool viewMoveDown = viewSelection && ((scriptsView.SelectedNode.Level == 0 &&
        scriptsView.SelectedNode.Index < scriptsView.Nodes.Count - 1) ||
        (scriptsView.SelectedNode.Level > 0 && scriptsView.SelectedNode.Index <
        scriptsView.SelectedNode.Parent.Nodes.Count - 1));
      bool viewMoveIn = viewSelection && (scriptsView.SelectedNode.Index > 0);
      bool viewMoveOut = viewSelection && (scriptsView.SelectedNode.Level != 0);
      bool viewPaste = project && ClipboardContainsScript() != null;
      bool viewCopy = viewSelection && scriptsView.SelectedNode.Level == 1;

      menuMain_dropFile_itemClose.Enabled = project;
      menuMain_dropFile_itemSave.Enabled = project;
      menuMain_dropFile_itemImport.Enabled = project;
      menuMain_dropFile_itemExportTo.Enabled = project;

      menuMain_dropEdit_itemUndo.Enabled = editorUndo;
      menuMain_dropEdit_itemRedo.Enabled = editorRedo;
      menuMain_dropEdit_itemCut.Enabled = editorSelection;
      menuMain_dropEdit_itemCopy.Enabled = editorSelection;
      menuMain_dropEdit_itemPaste.Enabled = editorPaste;
      menuMain_dropEdit_itemDelete.Enabled = editorSelection;
      menuMain_dropEdit_itemSelectAll.Enabled = editor;
      menuMain_dropEdit_itemBatchSearch.Enabled = project;
      menuMain_dropEdit_itemIncrementalSearch.Enabled = editor;
      menuMain_dropEdit_itemFind.Enabled = editor;
      menuMain_dropEdit_itemReplace.Enabled = editor;
      menuMain_dropEdit_itemGoTo.Enabled = editor;
      menuMain_dropEdit_itemBatchComment.Enabled = project;
      menuMain_dropEdit_itemComment.Enabled = editor;
      menuMain_dropEdit_itemUnComment.Enabled = editor;
      menuMain_dropEdit_itemToggleComment.Enabled = editor;
      menuMain_dropEdit_itemStructureScript.Enabled = project;
      menuMain_dropEdit_itemSScriptCurrent.Enabled = editor;
      menuMain_dropEdit_itemSScriptOpen.Enabled = editor;
      menuMain_dropEdit_itemSScriptAll.Enabled = project;
      menuMain_dropEdit_itemRemoveEmpty.Enabled = project;
      menuMain_dropEdit_itemRemoveEmptyCurrent.Enabled = editor;
      menuMain_dropEdit_itemRemoveEmptyOpen.Enabled = editor;
      menuMain_dropEdit_itemRemoveEmptyAll.Enabled = project;

      menuMain_dropSettings_itemUpdateSections.Enabled = scriptsFolder;

      menuMain_dropGame_itemHelp.Enabled = project;
      menuMain_dropGame_itemRun.Enabled = project;
      menuMain_dropGame_itemRunWithF12.Enabled = project;
      menuMain_dropGame_itemDebug.Enabled = project;
      menuMain_dropGame_itemProjectFolder.Enabled = project;

      scriptsEditor_ToolStripMenuItem_AddWordToAutoComplete.Enabled = editor;

      scriptName.Enabled = viewSelection;
      scriptsView_contextMenu_itemExport.Enabled = scriptsFolder;
      scriptsView_contextMenu_itemImport.Enabled = scriptsFolder;
      scriptsView_contextMenu_itemOpen.Enabled = viewSelection;
      scriptsView_contextMenu_itemInsert.Enabled = project;
      scriptsView_contextMenu_itemCut.Enabled = viewCopy;
      scriptsView_contextMenu_itemCopy.Enabled = viewCopy;
      scriptsView_contextMenu_itemPaste.Enabled = viewPaste;
      scriptsView_contextMenu_itemDelete.Enabled = viewSelection;
      scriptsView_contextMenu_itemMoveUp.Enabled = viewMoveUp;
      scriptsView_contextMenu_itemMoveDown.Enabled = viewMoveDown;
      scriptsView_contextMenu_itemMoveIn.Enabled = viewMoveIn;
      scriptsView_contextMenu_itemMoveOut.Enabled = viewMoveOut;

      // below are just duplicates

      scriptsView_contextMenu_itemBatchSearch.Enabled = menuMain_dropEdit_itemBatchSearch.Enabled;

      toolsView_itemImport.Enabled = scriptsView_contextMenu_itemImport.Enabled;
      toolsView_itemExport.Enabled = scriptsView_contextMenu_itemExport.Enabled;
      toolsView_itemInsert.Enabled = scriptsView_contextMenu_itemInsert.Enabled;
      toolsView_itemDelete.Enabled = scriptsView_contextMenu_itemDelete.Enabled;
      toolsView_itemMoveUp.Enabled = scriptsView_contextMenu_itemMoveUp.Enabled;
      toolsView_itemMoveDown.Enabled = scriptsView_contextMenu_itemMoveDown.Enabled;
      toolsView_itemMoveIn.Enabled = scriptsView_contextMenu_itemMoveIn.Enabled;
      toolsView_itemMoveOut.Enabled = scriptsView_contextMenu_itemMoveOut.Enabled;
      toolsView_itemBatchSearch.Enabled = menuMain_dropEdit_itemBatchSearch.Enabled;

      scriptsEditor_ToolStripMenuItem_Undo.Enabled = menuMain_dropEdit_itemUndo.Enabled;
      scriptsEditor_ToolStripMenuItem_Redo.Enabled = menuMain_dropEdit_itemRedo.Enabled;
      scriptsEditor_ToolStripMenuItem_Cut.Enabled = menuMain_dropEdit_itemCut.Enabled;
      scriptsEditor_ToolStripMenuItem_Copy.Enabled = menuMain_dropEdit_itemCopy.Enabled;
      scriptsEditor_ToolStripMenuItem_Paste.Enabled = menuMain_dropEdit_itemPaste.Enabled;
      scriptsEditor_ToolStripMenuItem_Delete.Enabled = menuMain_dropEdit_itemDelete.Enabled;
      scriptsEditor_ToolStripMenuItem_SelectAll.Enabled = menuMain_dropEdit_itemSelectAll.Enabled;
      scriptsEditor_ToolStripMenuItem_IncrementalSearch.Enabled = menuMain_dropEdit_itemIncrementalSearch.Enabled;
      scriptsEditor_ToolStripMenuItem_Find.Enabled = menuMain_dropEdit_itemFind.Enabled;
      scriptsEditor_ToolStripMenuItem_FindNext.Enabled = menuMain_dropEdit_itemFind.Enabled;
      scriptsEditor_ToolStripMenuItem_FindPrevious.Enabled = menuMain_dropEdit_itemFind.Enabled;
      scriptsEditor_ToolStripMenuItem_Replace.Enabled = menuMain_dropEdit_itemReplace.Enabled;
      scriptsEditor_ToolStripMenuItem_GoToLine.Enabled = menuMain_dropEdit_itemGoTo.Enabled;
      scriptsEditor_ToolStripMenuItem_Comment.Enabled = menuMain_dropEdit_itemToggleComment.Enabled;

      toolsEditor_itemSaveProject.Enabled = menuMain_dropFile_itemSave.Enabled;
      toolsEditor_itemSearch.Enabled = menuMain_dropEdit_itemIncrementalSearch.Enabled;
      toolsEditor_itemFind.Enabled = menuMain_dropEdit_itemFind.Enabled;
      toolsEditor_itemReplace.Enabled = menuMain_dropEdit_itemReplace.Enabled;
      toolsEditor_itemGoToLine.Enabled = menuMain_dropEdit_itemGoTo.Enabled;
      toolsEditor_itemComment.Enabled = menuMain_dropEdit_itemToggleComment.Enabled;
      toolsEditor_itemStructureScript.Enabled = menuMain_dropEdit_itemSScriptCurrent.Enabled;
      toolsEditor_itemRemoveLines.Enabled = menuMain_dropEdit_itemRemoveEmptyCurrent.Enabled;
      toolsEditor_itemRun.Enabled = menuMain_dropGame_itemRunWithF12.Enabled;
      toolsEditor_itemDebug.Enabled = menuMain_dropGame_itemDebug.Enabled;
      toolsEditor_itemProjectFolder.Enabled = menuMain_dropGame_itemProjectFolder.Enabled;
      toolsEditor_itemCloseProject.Enabled = menuMain_dropFile_itemClose.Enabled;
    }

    private void UpdateSettingsState()
    {
      bool project = _projectScriptsFolderPath != "";

      splitMain.Panel1Collapsed = Settings.DistractionMode.Use;
      toolsEditor_toolStrip.Visible = !Settings.DistractionMode.HideToolbar || !Settings.DistractionMode.Use;
      if (Settings.DistractionMode.Use)
        menuMain_dropSettings_itemToggleDistractionMode.Image = Properties.Resources.reduce;
      else
        menuMain_dropSettings_itemToggleDistractionMode.Image = Properties.Resources.expand;

      if (Settings.AutoHideMenuBar)
      {
        menuMain_menuStrip.Leave += menuMain_menuStrip_Leave;
      }
      else
      {
        menuMain_menuStrip.Leave -= menuMain_menuStrip_Leave;
      }

      menuMain_dropSettings_itemAutoHideMenuBar.Checked = Settings.AutoHideMenuBar;
      menuMain_dropSettings_itemProjectSettings.Checked = Settings.ProjectConfig;
      menuMain_dropSettings_itemHideToolbar.Checked = Settings.DistractionMode.HideToolbar;
      menuMain_dropSettings_itemAutoOpenOn.Checked = Settings.AutoOpen;
      menuMain_dropSettings_itemAutoOpenOff.Checked = !Settings.AutoOpen;
      menuMain_dropSettings_itemPioritizeRecent.Checked = Settings.RecentPriority;
      menuMain_dropSettings_itemAutoSaveSettings.Checked = Settings.AutoSaveConfig;
      menuMain_dropSettings_itemAutoUpdate.Checked = Settings.AutoCheckUpdates;
      menuMain_dropSettings_itemAutoC.Checked = Settings.AutoComplete;
      toolsEditor_itemAutoC.Checked = Settings.AutoComplete;
      menuMain_dropSettings_itemAutoIndent.Checked = Settings.AutoIndent;
      toolsEditor_itemAutoIndent.Checked = Settings.AutoIndent;
      menuMain_dropSettings_itemIndentGuides.Checked = Settings.GuideLines;
      toolsEditor_itemIndentGuides.Checked = Settings.GuideLines;
      menuMain_dropSettings_itemHighlight.Checked = Settings.LineHighLight;
      toolsEditor_itemHighlight.Checked = Settings.LineHighLight;
      menuMain_dropSettings_itemFolding.Checked = Settings.CodeFolding;
      toolsEditor_itemFolding.Checked = Settings.CodeFolding;
      menuMain_dropGame_itemDebug.Checked = Settings.DebugMode;
      toolsEditor_itemDebug.Checked = Settings.DebugMode;
    }

    private void UpdateScriptStatus()
    {
      Script script = GetActiveScript();
      scriptsEditor_StatusStrip_itemCharacters.Text = script == null ? "" :
          string.Format("{0}:{1} {2}", script.Scintilla.Caret.LineNumber + 1,
          script.Scintilla.Caret.Position - script.Scintilla.Lines[script.Scintilla.Caret.LineNumber].StartPosition + 1,
          (script.Scintilla.Selection.Length == 0 ? "" : "(" + script.Scintilla.Selection.Length + ")"));
    }

    private void UpdateName(int section)
    {
      if (NodeExists(section) && GetScript(section).TabName != GetNode(section).Text)
        GetNode(section).Text = GetScript(section).TabName;
    }

    private void UpdateNames()
    {
      // TODO: Reimplement logic...
    }

    #endregion Update Methods
  }
}