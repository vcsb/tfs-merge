﻿using Caliburn.Micro;
using Microsoft.TeamFoundation.VersionControl.Client;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TFSMergingTool.Resources;
using TFSMergingTool.OutputWindow;
using TFSMergingTool.Settings;
using TFSMergingTool.Shell;
using TFSMergingTool.Resources.FolderTree;
using System.IO;
using System.Diagnostics;
using System.Windows;

namespace TFSMergingTool.ConnectionSetup
{
    public interface IServerSetupViewModel : IScreen { }

    [Export(typeof(IServerSetupViewModel))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    class ConnectionSetupViewModel : Screen, IServerSetupViewModel
    {
        IEventAggregator EventAggregator { get; set; }
        IOutputWindow Output { get; set; }
        UserSettings UserSettings { get; set; }
        MyTFSConnection TfsConnection { get; set; }
        bool FirstActivated { get; set; }
        IPopupService Popups { get; set; }

        [ImportingConstructor]
        public ConnectionSetupViewModel(IEventAggregator eventAggregator, IOutputWindow output,
            UserSettings userSettings, MyTFSConnection tfsConnection, IPopupService popups)
        {
            EventAggregator = eventAggregator;
            Output = output;
            UserSettings = userSettings;
            TfsConnection = tfsConnection;
            Popups = popups;

            Branches = new BindableCollection<BranchViewModel>();

            FirstActivated = false;
            Activated += ConnectionSetupViewModel_Activated;
            Deactivated += ConnectionSetupViewModel_Deactivated;
        }

        #region Screen Implementation

        private void ConnectionSetupViewModel_Activated(object sender, ActivationEventArgs e)
        {
            if (!FirstActivated)
            {
                FirstActivated = true;
                string settingsFileName = UserSettings.DefaultLocalSettingsFileName;
                if (!System.IO.File.Exists(settingsFileName))
                {
                    Output.WriteLine("Default local settings file not found at \"{0}\", loading default settings", settingsFileName);
                    settingsFileName = UserSettings.DefaultSettingsFileName;
                }
                if (!System.IO.File.Exists(settingsFileName))
                {
                    throw new InvalidSettingsFileException("No default settings file found at application startup.");
                }
                loadSettingsFile(settingsFileName);
            }
            EvalButtonCanProperties();

            if (TfsConnection.IsConnected)
                DisconnectFromServer();
        }

        private void ConnectionSetupViewModel_Deactivated(object sender, DeactivationEventArgs e)
        {
        }

        #endregion

        #region Settings file handling

        public void SelectNewSettingsFile()
        {
            var fileDialog = new OpenFileDialog();
            fileDialog.Filter = "Settings files (settings.*.xml)|settings.*.xml";
            fileDialog.InitialDirectory = System.IO.Directory.GetCurrentDirectory();
            if (fileDialog.ShowDialog() == true)
            {
                loadSettingsFile(fileDialog.FileName);
            }
        }

        private void loadSettingsFile(string filename)
        {
            Output.WriteLine("Loading settings file {0}...", filename);
            var success = UserSettings.ReadFromFile(filename);
            if (!success || !UserSettings.IsValid)
            {
                var msg = "Failed to load settings file " + filename;
                Output.WriteLine(msg);
                Popups.ShowMessage(null, msg, MessageBoxImage.Asterisk, "Load failed");
            }
            ServerAddress = UserSettings.ServerUri.ToString();

            Branches.Clear();
            foreach (var branch in UserSettings.BranchPathList)
            {
                var branchWM = new BranchViewModel(branch.Item2, branch.Item1);
                Branches.Add(branchWM);
            }

            TfsExePath = UserSettings.TfsExecutable.FullName;

            Output.WriteLine("Reading settings finished.");
        }

        string _tfsExePath;
        public string TfsExePath
        {
            get { return _tfsExePath; }
            set
            {
                _tfsExePath = value;
                NotifyOfPropertyChange(() => TfsExePath);
                var fi = new FileInfo(_tfsExePath);
                if (fi.Exists)
                {
                    UserSettings.TfsExecutable = fi;
                }
            }
        }

        #endregion

        #region Connect to server

        string _serverAddress;
        public string ServerAddress
        {
            get { return _serverAddress; }
            set
            {
                _serverAddress = value;
                NotifyOfPropertyChange(() => ServerAddress);
                var serverUri = new Uri(ServerAddress);
                UserSettings.ServerUri = serverUri;
            }
        }

        public bool ConnectToServer()
        {
            var found = false;

            List<TeamProjectCollectionData> teamProjectCollections;
            ConnectionState connectionState = TfsConnection.ConnectToServer(UserSettings.ServerUri, out teamProjectCollections);
            if (connectionState == ConnectionState.Connected)
            {
                var numCollections = teamProjectCollections.Count;
                Output.WriteLine("Connected to server with {0} collectios:", numCollections);
                foreach (var collection in teamProjectCollections)
                    Output.WriteLine("  " + collection.Name);

                // Automatically find a collection and a workspace that contains all the enabled branchpaths.
                var branchPaths = Branches.Where(item => item.IsEnabled == true).Select(item => item.Path).ToList();
                if (branchPaths.Count == 0)
                {
                    Output.WriteLine("No branch paths selected, aborting.");
                    Popups.ShowMessage("Please add some branch paths before connecting to server.", MessageBoxImage.Exclamation);
                    return false;
                }

                foreach (var collection in teamProjectCollections)
                {
                    Output.WriteLine("Connecting to a Team Project Collection {0} at {1}...", collection.Name, collection.Uri);
                    connectionState = TfsConnection.ConnectToTeamProjectCollection(collection.Uri);
                    if (connectionState == ConnectionState.Connected)
                    {
                        Output.WriteLine("Connected.");
                        Workspace[] workspaces = TfsConnection.GetWorkspaces();
                        foreach (var workspace in workspaces)
                        {
                            bool hasFolders = DoesWorkspaceContainFolders(workspace, branchPaths);
                            if (hasFolders)
                            {
                                Output.WriteLine("Found all enabled branch folders in workspace " + workspace.Name);
                                TfsConnection.WorkSpace = workspace;
                                found = true;
                                break;
                            }
                        }
                        if (found)
                            break;
                        Output.WriteLine("Did not find all enabled branch folders in any one TFS workspace.");
                        Popups.ShowMessage("Did not find all enabled branch folders in any one TFS workspace.", MessageBoxImage.Exclamation);
                    }
                    else
                    {
                        Output.WriteLine("Connection to server failed.");
                        Popups.ShowMessage("Connection to server failed.", MessageBoxImage.Error);
                    }
                }
            }
            else
            {
                Output.WriteLine("Connection to server failed.");
                Popups.ShowMessage("Connection to server failed.", MessageBoxImage.Error);
            }
            if (!found)
                DisconnectFromServer();
            EvalButtonCanProperties();
            return found;
        }

        private bool DoesWorkspaceContainFolders(Workspace workspace, List<string> folders)
        {
            foreach (var folder in folders)
            {
                var dInfo = new DirectoryInfo(folder);
                if (!dInfo.Exists)
                    return false;
            }

            // Naive check; even if the workspace contains the parent folder, that folder might not be mapped.

            var foundMap = new Dictionary<string, bool>();
            foreach (var localFolder in folders)
            {
                DirectoryInfo localDi = new DirectoryInfo(localFolder);

                var parentFound = false;
                foreach (var wpFolder in workspace.Folders)
                {
                    DirectoryInfo wpDi = new DirectoryInfo(wpFolder.LocalItem);
                    parentFound = CheckIfIsSameOrParentOf(localDi, wpDi);
                    if (parentFound)
                    {
                        foundMap.Add(localDi.FullName, true);
                        break;
                    }
                }
                if (!parentFound)
                    foundMap.Add(localDi.FullName, false);


            }
            var allFound = foundMap.All(item => item.Value == true);
            if (allFound)
                Debug.Assert(foundMap.Keys.Count() == folders.Count);
            return allFound;
        }

        private bool CheckIfIsSameOrParentOf(DirectoryInfo item, DirectoryInfo parentCandidate)
        {
            bool ret = item.FullName == parentCandidate.FullName;
            while (item.Parent != null && ret == false)
            {
                if (item.Parent.FullName == parentCandidate.FullName)
                {
                    ret = true;
                    break;
                }
                else item = item.Parent;
            }
            return ret;
        }

        public void DisconnectFromServer()
        {
            Output.WriteLine("Disconnecting from server, if connected.");
            TfsConnection.Disconnect();
            EvalButtonCanProperties();
        }

        #endregion

        #region Button Can properties

        public bool CanGotoMergeFromList
        {
            get { return true; }
        }

        public bool CanGotoMergeSpecificId
        {
            get { return CanGotoMergeFromList; }
        }

        public bool CanConnectToServer
        {
            get { return !TfsConnection.IsConnected; }
        }

        public bool CanDisconnectFromServer
        {
            get { return TfsConnection.IsConnected; }
        }

        private void EvalButtonCanProperties()
        {
            NotifyOfPropertyChange(() => CanConnectToServer);
            NotifyOfPropertyChange(() => CanDisconnectFromServer);
            NotifyOfPropertyChange(() => CanGotoMergeFromList);
            NotifyOfPropertyChange(() => CanGotoMergeSpecificId);
        }

        #endregion

        #region Branchlist

        public IObservableCollection<BranchViewModel> Branches { get; set; }
        private BranchViewModel _selectedBranche;
        public BranchViewModel SelectedBranche
        {
            get { return _selectedBranche; }
            set
            {
                if (value != _selectedBranche)
                {
                    _selectedBranche = value;
                    NotifyOfPropertyChange(() => SelectedBranche);
                }
            }
        }

        public void AddBranch()
        {
            var item = new BranchViewModel("Write the local path here", true);
            Branches.Add(item);
            SelectedBranche = item;
        }

        public void RemoveBranch()
        {
            var index = Branches.IndexOf(SelectedBranche);
            if (index >= 0)
            {
                Branches.RemoveAt(index);
                if (Branches.Count > 0)
                {
                    if (index == Branches.Count)
                        SelectedBranche = Branches[index - 1];
                    else
                        SelectedBranche = Branches[index];
                }
            }
        }

        public void MoveBranchUp()
        {
            var item = SelectedBranche;
            var index = Branches.IndexOf(item);
            if (index > 0)
            {
                Branches.RemoveAt(index);
                Branches.Insert(index - 1, item);
                SelectedBranche = item;
            }
        }

        public void MoveBranchDown()
        {
            var item = SelectedBranche;
            var index = Branches.IndexOf(item);
            if (index < Branches.Count - 1)
            {
                Branches.RemoveAt(index);
                Branches.Insert(index + 1, item);
                SelectedBranche = item;
            }
        }

        public void RefreshBranches()
        {
            Branches.Clear();
            foreach (var branch in UserSettings.BranchPathList)
            {
                var branchWM = new BranchViewModel(branch.Item2, branch.Item1);
                Branches.Add(branchWM);
            }
        }

        public void SaveBranches()
        {
            if (Branches.Count > 0)
            {
                var pathList = new List<Tuple<bool, string>>();
                foreach (var branch in Branches)
                {
                    pathList.Add(Tuple.Create(branch.IsEnabled, branch.Path));
                }
                UserSettings.BranchPathList = pathList;
                UserSettings.WriteToFile(UserSettings.DefaultLocalSettingsFileName);
            }
        }
        #endregion

        #region Start merging

        public void GotoMergeFromList(int mode)
        {
            if (UserSettings.TfsExecutable.Exists == false)
            {
                Popups.ShowMessage("tfs.exe does not exist in:\n" + UserSettings.TfsExecutable.FullName, MessageBoxImage.Error);
                return;
            }

            MainMode newMode;
            if (mode == 1)
                newMode = MainMode.MergeFromList;
            else if (mode == 2)
                newMode = MainMode.MergeSpecificId;
            else
                return;

            var activeBranches = from branch in Branches
                                 where branch.IsEnabled
                                 select branch;

            if (activeBranches.Count() < 2)
            {
                Popups.ShowMessage("Add at least 2 active branches to start merging.", MessageBoxImage.Exclamation);
            }
            else
            {
                // (re)connect to make sure the correct branches are set.
                if (TfsConnection.IsConnected)
                    DisconnectFromServer();
                ConnectToServer();

                if (TfsConnection.IsConnected)
                {
                    var branchList = new List<DirectoryInfo>();
                    foreach (var activeBranch in activeBranches)
                    {
                        var dInfo = new DirectoryInfo(activeBranch.Path);
                        branchList.Add(dInfo);
                    }
                    EventAggregator.PublishOnUIThread(new ChangeMainModeEvent(newMode, branchList));
                }

            }
        }

        #endregion

    }
}
