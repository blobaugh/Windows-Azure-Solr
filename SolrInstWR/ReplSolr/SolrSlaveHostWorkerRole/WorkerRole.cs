﻿#region Copyright Notice
/*
Copyright © Microsoft Open Technologies, Inc.
All Rights Reserved
Apache 2.0 License

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

     http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.

See the Apache Version 2.0 License for specific language governing permissions and limitations under the License.
*/
#endregion

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using Microsoft.WindowsAzure;
using Microsoft.WindowsAzure.Diagnostics;
using Microsoft.WindowsAzure.ServiceRuntime;
using Microsoft.WindowsAzure.StorageClient;
using System.IO;
using System.Xml.Linq;
using System.Xml;
using System.Globalization;
using HelperLib;

namespace SolrSlaveHostWorkerRole
{
    public class WorkerRole : RoleEntryPoint
    {
        private static CloudDrive _solrStorageDrive;
        private static String _logFileLocation;
        private static Process _solrProcess;
        private static string _port = null;
        private static string _masterUrl;
        private static string _mySolrUrl;
        private static SolrFileLocations _fileLocationResolver;

        public override void Run()
        {
            Log("SolrSlaveHostWorkerRole Run() called", "Information");

            while (true)
            {
                Thread.Sleep(10000);

                string masterUrl = HelperLib.Util.GetMasterEndpoint();
                if (masterUrl != _masterUrl) // master changed?
                {
                    Log("Master Url changed, recycling slave role", "Information");
                    RoleEnvironment.RequestRecycle();
                    return;
                }

                if ((_solrProcess != null) && (_solrProcess.HasExited == true))
                {
                    Log("Solr Process Exited. Hence recycling slave role.", "Information");
                    RoleEnvironment.RequestRecycle();
                    return;
                }

                Log("Working", "Information");
            }
        }

        public override bool OnStart()
        {
            Log("SolrSlaveHostWorkerRole Start() called", "Information");

            // Set the maximum number of concurrent connections 
            ServicePointManager.DefaultConnectionLimit = 12;

            RoleEnvironment.Changing += (sender, arg) =>
            {
                RoleEnvironment.RequestRecycle();
            };

            _fileLocationResolver = new SolrFileLocations(RoleEnvironment.GetConfigurationSettingValue("SolrMajorVersion"));
            InitDiagnostics();
            StartSolr();

            return base.OnStart();
        }

        public override void OnStop()
        {
            Log("SolrSlaveHostWorkerRole OnStop() called", "Information");

            if (_solrProcess != null)
            {
                try
                {
                    _solrProcess.Kill();
                    _solrProcess.WaitForExit(2000);
                }
                catch { }
            }

            if (_solrStorageDrive != null)
            {
                try
                {
                    _solrStorageDrive.Unmount();
                }
                catch { }
            }

            base.OnStop();
        }

        private void StartSolr()
        {
            try
            {
                // we use an Azure drive to store the solr index and conf data
                String vhdPath = CreateSolrStorageVhd();

                InitializeLogFile(vhdPath);

                InitRoleInfo();

                // Create the necessary directories in the Azure drive.
                CreateSolrStoragerDirs(vhdPath);

                //Set IP Endpoint and Port Address.
                //ConfigureIPEndPointAndPortAddress();

                // Copy solr files such as configuration and additional libraries etc.
                CopySolrFiles(vhdPath);

                Log("Done - Creating storage dirs and copying conf files", "Information");

                string cmdLineFormat =
                    @"%RoleRoot%\approot\jre6\bin\java.exe -Dsolr.solr.home={0}SolrStorage -Djetty.port={1} -Denable.slave=true -DmasterUrl={2} -DdefaultCoreName=slaveCore -jar %RoleRoot%\approot\Solr\example\start.jar";

                _masterUrl = HelperLib.Util.GetMasterEndpoint();
                Log("GetMasterUrl: " + _masterUrl, "Information");

                string cmdLine = String.Format(CultureInfo.InvariantCulture, cmdLineFormat, vhdPath, _port, _masterUrl + "replication");
                Log("Solr start command line: " + cmdLine, "Information");

                _solrProcess = ExecuteShellCommand(cmdLine, false, Path.Combine(Environment.GetEnvironmentVariable("RoleRoot") + @"\", @"approot\Solr\example\"));
                _solrProcess.Exited += new EventHandler(_solrProcess_Exited);

                Log("Done - Starting Solr", "Information");
            }
            catch (Exception ex)
            {
                Log("Exception occured in StartSolr " + ex.Message, "Error");
            }
        }

        void _solrProcess_Exited(object sender, EventArgs e)
        {
            Log("Solr Exited", "Information");
            RoleEnvironment.RequestRecycle();
        }

        private static String CreateSolrStorageVhd()
        {
            CloudStorageAccount storageAccount;
            LocalResource localCache;
            CloudBlobClient client;
            CloudBlobContainer drives;

            localCache = RoleEnvironment.GetLocalResource("AzureDriveCache");
            Log(String.Format(CultureInfo.InvariantCulture, "AzureDriveCache {0} {1} MB", localCache.RootPath, localCache.MaximumSizeInMegabytes - 50), "Information");
            CloudDrive.InitializeCache(localCache.RootPath.TrimEnd('\\'), localCache.MaximumSizeInMegabytes - 50);

            storageAccount = CloudStorageAccount.Parse(RoleEnvironment.GetConfigurationSettingValue("DataConnectionString"));
            client = storageAccount.CreateCloudBlobClient();

            string roleId = RoleEnvironment.CurrentRoleInstance.Id;
            string containerAddress = ContainerNameFromRoleId(roleId);
            drives = client.GetContainerReference(containerAddress);

            try { drives.CreateIfNotExist(); }
            catch (StorageClientException) { };

            string vhdName = string.Format(CultureInfo.InvariantCulture, "SolrStorage_{0}.vhd", RoleEnvironment.GetConfigurationSettingValue("SolrMajorVersion"));
            var vhdUrl = client.GetContainerReference(containerAddress).GetBlobReference(vhdName).Uri.ToString();
            Log(String.Format(CultureInfo.InvariantCulture, "{0} {1}", vhdName, vhdUrl), "Information");
            _solrStorageDrive = storageAccount.CreateCloudDrive(vhdUrl);

            int cloudDriveSizeInMB = int.Parse(RoleEnvironment.GetConfigurationSettingValue("CloudDriveSize"), CultureInfo.InvariantCulture);
            try { _solrStorageDrive.Create(cloudDriveSizeInMB); }
            catch (CloudDriveException) { }

            Log(String.Format(CultureInfo.InvariantCulture, "CloudDriveSize {0} MB", cloudDriveSizeInMB), "Information");

            var dataPath = _solrStorageDrive.Mount(localCache.MaximumSizeInMegabytes - 50, DriveMountOptions.Force);
            Log(String.Format(CultureInfo.InvariantCulture, "Mounted as {0}", dataPath), "Information");

            return dataPath;
        }

        // follow container naming conventions to generate a unique container name
        private static string ContainerNameFromRoleId(string roleId)
        {
            return roleId.Replace('(', '-').Replace(").", "-").Replace('.', '-').Replace('_', '-').ToLowerInvariant();
        }

        private static void CreateSolrStoragerDirs(String vhdPath)
        {
            String solrStorageDir = Path.Combine(vhdPath, "SolrStorage");
            string[] directoriesToCreate = new string[] 
            {
                solrStorageDir,
                Path.Combine(solrStorageDir, _fileLocationResolver.VhdLangDir),
                Path.Combine(solrStorageDir, _fileLocationResolver.VhdConfDir),
                Path.Combine(solrStorageDir, "data"),
                Path.Combine(solrStorageDir, "lib")
            };

            foreach (string eachDir in directoriesToCreate)
            {
                if (Directory.Exists(eachDir) == false)
                {
                    Directory.CreateDirectory(eachDir);
                }
            }
        }

        private static void InitializeLogFile(string vhdPath)
        {
            String logFileName;
            String logFileDirectoryLocation;

            logFileDirectoryLocation = Path.Combine(vhdPath, "LogFiles");
            if (Directory.Exists(logFileDirectoryLocation) == false)
            {
                Directory.CreateDirectory(logFileDirectoryLocation);
            }

            logFileName = String.Format(CultureInfo.InvariantCulture, "Log_{0}.txt", DateTime.Now.ToString("MM_dd_yyyy_HH_mm_ss", CultureInfo.InvariantCulture));
            using (FileStream logFileStream = File.Create(Path.Combine(logFileDirectoryLocation, logFileName)))
            {
                _logFileLocation = Path.Combine(logFileDirectoryLocation, logFileName);
            }
        }

        private void CopySolrFiles(String vhdPath)
        {
            string modifiedSolrFileSrc = Path.Combine(Environment.GetEnvironmentVariable("RoleRoot") + @"\", @"approot\SolrFiles\");

            //Updated Solr files are the files containing some minor changes like -> Replication Enabled, Fields related to wikipedia.
            List<string> updatedSolrFiles = new List<string>() { "schema.xml", "solrconfig.xml" };
            //Get list of files to be replicated.
            List<string> replicatedFiles = GetReplicatedConfFiles(Path.Combine(modifiedSolrFileSrc, _fileLocationResolver.ConfigXml));

            // Copy solr conf files.
            IEnumerable<String> confFiles = Directory.EnumerateFiles(Path.Combine(Environment.GetEnvironmentVariable("RoleRoot") + @"\", _fileLocationResolver.SolrConfDir));
            foreach (String sourceFile in confFiles)
            {
                String confFileName = System.IO.Path.GetFileName(sourceFile);
                String fileCopyDestination = Path.Combine(vhdPath, "SolrStorage", _fileLocationResolver.VhdConfDir, confFileName);
                {
                    //Don't copy the files which are part of updated file list..because we copy them later in same routine.
                    if (updatedSolrFiles.Contains(confFileName) == false)
                    {
                        //Don't copy the files which are part of replicated file list as well..Otherwise they would be backed up with same timestamp after every recycle/reboot causing replcation to fail.
                        if (File.Exists(fileCopyDestination) == false || replicatedFiles.Contains(confFileName.ToUpperInvariant()) == false)
                        {
                            File.Copy(sourceFile, fileCopyDestination, true);
                        }
                    }
                }
            }

            // Copy lang Directory.
            IEnumerable<String> langFiles = Directory.EnumerateFiles(Path.Combine(Environment.GetEnvironmentVariable("RoleRoot") + @"\", _fileLocationResolver.SolrLangDir));
            foreach (String sourceFile in langFiles)
            {
                String confFileName = System.IO.Path.GetFileName(sourceFile);
                File.Copy(sourceFile, Path.Combine(vhdPath, "SolrStorage", _fileLocationResolver.VhdLangDir, confFileName), true);
            }

            // Add updated versions of SOLR files.
            string modifiedSolrFileDestination = Path.Combine(vhdPath, "SolrStorage", _fileLocationResolver.VhdConfDir);
            File.Copy(Path.Combine(modifiedSolrFileSrc, "data-config.xml"), Path.Combine(modifiedSolrFileDestination, "data-config.xml"), true);

            //Don't copy the files which are part of replicated file list as well..Otherwise they would be backed up with same timestamp after every recycle/reboot causing replcation to fail.
            string schemaFileDest = Path.Combine(modifiedSolrFileDestination, "schema.xml");
            if (File.Exists(schemaFileDest) == false || replicatedFiles.Contains("SCHEMA.XML") == false)
            {
                File.Copy(Path.Combine(modifiedSolrFileSrc, _fileLocationResolver.SchemaXml), schemaFileDest, true);
            }

            string configFileDest = Path.Combine(modifiedSolrFileDestination, "solrconfig.xml");
            if (File.Exists(configFileDest) == false || replicatedFiles.Contains("SOLRCONFIG.XML") == false)
            {
                File.Copy(Path.Combine(modifiedSolrFileSrc, _fileLocationResolver.ConfigXml), Path.Combine(modifiedSolrFileDestination, "solrconfig.xml"), true);
            }

            CopyLibFiles(Path.Combine(vhdPath, "SolrStorage"));
            CopyExtractionFiles(Path.Combine(vhdPath, "SolrStorage"));
        }

        private void CopyExtractionFiles(string solrStorage)
        {
            String libDir = Path.Combine(solrStorage, "lib");
            String sourceExtractionFilesDir = Path.Combine(Environment.GetEnvironmentVariable("RoleRoot") + @"\", @"approot\Solr\contrib\extraction\lib");
            ExecuteShellCommand(String.Format(CultureInfo.InvariantCulture, "XCOPY \"{0}\" \"{1}\"  /E /Y", sourceExtractionFilesDir, libDir), true);
        }

        private static void CopyLibFiles(String solrStorage)
        {
            String libFileName, libFileLocation;
            IEnumerable<String> libFiles;

            libFiles = Directory.EnumerateFiles(Path.Combine(Environment.GetEnvironmentVariable("RoleRoot") + @"\", @"approot\Solr\dist"));
            libFileLocation = Path.Combine(solrStorage, "lib");
            foreach (String sourceFile in libFiles)
            {
                libFileName = System.IO.Path.GetFileName(sourceFile);
                File.Copy(sourceFile, Path.Combine(libFileLocation, libFileName), true);
            }
        }

        // figure out and set port, master / slave, master Url etc.
        private static void InitRoleInfo()
        {
            IPEndPoint endpoint = RoleEnvironment.CurrentRoleInstance.InstanceEndpoints["SolrSlaveEndpoint"].IPEndpoint;
            _port = endpoint.Port.ToString(CultureInfo.InvariantCulture);
            _mySolrUrl = string.Format(CultureInfo.InvariantCulture, "http://{0}/solr/", endpoint);

            HelperLib.Util.AddRoleInfoEntry(RoleEnvironment.CurrentRoleInstance.Id, endpoint.Address.ToString(), endpoint.Port, false);

            Log("My SolrURL: " + _mySolrUrl, "Information");
        }

        private Process ExecuteShellCommand(String command, bool waitForExit, String workingDir = null)
        {
            Process processToExecuteCommand = new Process();

            processToExecuteCommand.StartInfo.FileName = "cmd.exe";
            if (workingDir != null)
            {
                processToExecuteCommand.StartInfo.WorkingDirectory = workingDir;
            }

            processToExecuteCommand.StartInfo.Arguments = @"/C " + command;
            processToExecuteCommand.StartInfo.RedirectStandardInput = true;
            processToExecuteCommand.StartInfo.RedirectStandardError = true;
            processToExecuteCommand.StartInfo.RedirectStandardOutput = true;
            processToExecuteCommand.StartInfo.UseShellExecute = false;
            processToExecuteCommand.StartInfo.CreateNoWindow = true;
            processToExecuteCommand.EnableRaisingEvents = false;
            processToExecuteCommand.Start();

            processToExecuteCommand.OutputDataReceived += new DataReceivedEventHandler(processToExecuteCommand_OutputDataReceived);
            processToExecuteCommand.ErrorDataReceived += new DataReceivedEventHandler(processToExecuteCommand_ErrorDataReceived);
            processToExecuteCommand.BeginOutputReadLine();
            processToExecuteCommand.BeginErrorReadLine();

            if (waitForExit == true)
            {
                processToExecuteCommand.WaitForExit();
                processToExecuteCommand.Close();
                processToExecuteCommand.Dispose();
                processToExecuteCommand = null;
            }

            return processToExecuteCommand;
        }

        private void processToExecuteCommand_ErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            Log(e.Data, "Message");
        }

        private void processToExecuteCommand_OutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            Log(e.Data, "Message");
        }

        private static void InitDiagnostics()
        {
#if DEBUG
            // Get the default initial configuration for DiagnosticMonitor.
            DiagnosticMonitorConfiguration diagnosticConfiguration = DiagnosticMonitor.GetDefaultInitialConfiguration();

            // Filter the logs so that only error-level logs are transferred to persistent storage.
            diagnosticConfiguration.Logs.ScheduledTransferLogLevelFilter = LogLevel.Undefined;

            // Schedule a transfer period of 30 minutes.
            diagnosticConfiguration.Logs.ScheduledTransferPeriod = TimeSpan.FromMinutes(2.0);

            // Specify a buffer quota of 1GB.
            diagnosticConfiguration.Logs.BufferQuotaInMB = 1024;

            // Start the DiagnosticMonitor using the diagnosticConfig and our connection string.
            DiagnosticMonitor.Start("Microsoft.WindowsAzure.Plugins.Diagnostics.ConnectionString", diagnosticConfiguration);
#endif
        }

        private static void Log(string message, string category)
        {
#if DEBUG
            message = RoleEnvironment.CurrentRoleInstance.Id + "=> " + message;

            try
            {
                if (String.IsNullOrWhiteSpace(_logFileLocation) == false)
                {
                    File.AppendAllText(_logFileLocation, String.Concat(message, Environment.NewLine));
                }
            }
            catch
            { }

            Trace.WriteLine(message, category);
#endif
        }

        private static List<string> GetReplicatedConfFiles(string solrConfFileLoc)
        {
            List<string> confFiles = new List<string>();

            XmlDocument solrConfig = new XmlDocument();
            solrConfig.Load(solrConfFileLoc);
            XmlNode confFileNode = solrConfig.SelectSingleNode(@"/config/requestHandler[@class='solr.ReplicationHandler']/lst[@name='master']/str[@name='confFiles']");

            if (confFiles != null && string.IsNullOrEmpty(confFileNode.InnerText) == false)
            {
                confFiles.AddRange(confFileNode.InnerText.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries).
                                   Select(e => e.Trim().ToUpperInvariant()));
            }
            return confFiles;
        }
    }
}
