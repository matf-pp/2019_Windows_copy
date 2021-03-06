﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PasteApp
{
    class Program
    {
        [Flags]
        public enum ThreadAccess : int
        {
            TERMINATE = (0x0001),
            SUSPEND_RESUME = (0x0002),
            GET_CONTEXT = (0x0008),
            SET_CONTEXT = (0x0010),
            SET_INFORMATION = (0x0020),
            QUERY_INFORMATION = (0x0040),
            SET_THREAD_TOKEN = (0x0080),
            IMPERSONATE = (0x0100),
            DIRECT_IMPERSONATION = (0x0200)
        }

        [DllImport("kernel32.dll")]
        static extern IntPtr OpenThread(ThreadAccess dwDesiredAccess, bool bInheritHandle, uint dwThreadId);

        [DllImport("kernel32.dll")]
        static extern uint SuspendThread(IntPtr hThread);

        [DllImport("kernel32.dll")]
        static extern int ResumeThread(IntPtr hThread);

        [DllImport("kernel32", CharSet = CharSet.Auto, SetLastError = true)]
        static extern bool CloseHandle(IntPtr handle);


        private static void SuspendProcess(int pid)
        {
            try
            {               
                var process = Process.GetProcessById(pid);
           
                if (process.ProcessName == string.Empty)
                    return;
                          
                foreach (ProcessThread pT in process.Threads)
                {
                    IntPtr pOpenThread = OpenThread(ThreadAccess.SUSPEND_RESUME, false, (uint)pT.Id);

                    if (pOpenThread == IntPtr.Zero)
                    {
                        continue;
                    }

                    SuspendThread(pOpenThread);

                    CloseHandle(pOpenThread);
                }

                Debug.WriteLine(CurrentTime() + "Process " + process.ProcessName + "with id " + process.Id + " suspended.");
            }
            catch (Exception e)
            {
                // Process has already ended
                Debug.WriteLine(CurrentTime() + " [ERROR] " + e.Message + Environment.NewLine + e.StackTrace);
            }
        }

        public static void ResumeProcess(int pid)
        {
            try
            { 
                var process = Process.GetProcessById(pid);

                if (process.ProcessName == string.Empty)
                    return;

                foreach (ProcessThread pT in process.Threads)
                {
                    IntPtr pOpenThread = OpenThread(ThreadAccess.SUSPEND_RESUME, false, (uint)pT.Id);

                    if (pOpenThread == IntPtr.Zero)
                    {
                        continue;
                    }

                    var suspendCount = 0;
                    do
                    {
                        suspendCount = ResumeThread(pOpenThread);
                    } while (suspendCount > 0);

                    CloseHandle(pOpenThread);
                }

                Debug.WriteLine(CurrentTime() + "Process " + process.ProcessName + "with id " + process.Id + " resumed.");
            }
            catch (Exception e)
            {
                // Process has already ended
                Debug.WriteLine(CurrentTime() + " [ERROR] " + e.Message + Environment.NewLine + e.StackTrace);
            }
        }

        private static readonly string productName = "MultithreadWindowsCopy";
        private static int currentRobocopyProcessId = 0;
        private static bool operationAborted = false;
        private static string copyOrCutOption;
        
        public static void PauseCopying()
        {
            SuspendProcess(currentRobocopyProcessId);
        }

        public static void ResumeCopying()
        {
           ResumeProcess(currentRobocopyProcessId);
        }

        public static void AbortCopying()
        {
            try
            {
                operationAborted = true;
                var process = Process.GetProcessById(currentRobocopyProcessId);
                process.Kill();
            }
            catch (Exception e)
            {
                // Process has already ended
                Debug.WriteLine(CurrentTime() + " [ERROR] " + e.Message + Environment.NewLine + e.StackTrace);
            }
        }

        public static string[] ReadFromMMF()
        {
            const int mmfMaxSize = 16 * 1024 * 1024;  // allocated memory for this memory mapped file (bytes)
            const int mmfViewSize = 16 * 1024 * 1024; // how many bytes of the allocated memory can this process access

            MemoryMappedFile mmf = MemoryMappedFile.CreateOrOpen("ClipboardAppMemoryMappedFile", mmfMaxSize, MemoryMappedFileAccess.ReadWrite);
            MemoryMappedViewStream mmvStream = mmf.CreateViewStream(0, mmfViewSize);
            BinaryFormatter formatter = new BinaryFormatter();

            string[] lines = (string[]) formatter.Deserialize(mmvStream);
            mmvStream.Seek(0, SeekOrigin.Begin);
            mmvStream.Close();
            return lines;
        }

        /// <summary>
        /// Extracts paths of folders and files which are to be copied.
        /// /// </summary>
        /// <returns>
        /// First string is the path of the root folder of folders and files to be copied.
        /// Remaining strings represent local paths of folders and files. 
        /// </returns>
        private static string[] GetFoldersAndFilesToCopyOrCut()
        {
            if (Process.GetProcessesByName("ClipboardApp.exe").Length == 0)
            {
                string[] contentFromMMF = ReadFromMMF();
                copyOrCutOption = contentFromMMF[0];
                return contentFromMMF.Skip(1).ToArray();
            }
            else
                return new string[] { }; // return empty array
        }

        /// <summary>
        /// Calculates the size of a directory. 
        /// </summary>
        /// <param name="dirInfo">DirectoryInfo instance.</param>
        /// <returns>Directory size in bytes.</returns>
        private static long GetDirectorySize(DirectoryInfo dirInfo)
        {
            long sizeInBytes = 0;
            sizeInBytes += dirInfo.EnumerateFiles().Sum(fi => fi.Length);
            sizeInBytes += dirInfo.EnumerateDirectories().Sum(di => GetDirectorySize(di));
            return sizeInBytes;
        }

        /// <summary>
        /// Returns the total file size in all directories and subdirectories.
        /// </summary>
        /// <param name="root">Absoulte path of root folder of items to be copied.</param>
        /// <param name="itemsToCopy">Names of folders and files to be copied.</param>
        /// <returns>Total file size, including files in subdirectories.</returns>
        private static long GetTotalFileSize(string root, string[] itemsToCopy)
        {
            long sizeInBytes = 0;
            foreach(string item in itemsToCopy)
            {
                string path = Path.Combine(root, item);
                if (File.Exists(path) == true)
                    sizeInBytes += (new FileInfo(path)).Length;
                else if (Directory.Exists(path) == true)
                    sizeInBytes += GetDirectorySize(new DirectoryInfo(path));
                else
                    throw new Exception("Given path is not a file nor a directory: " + path + "!");
            }

            return sizeInBytes;
        }

        /// <summary>
        /// Returns the total file count in all directories and subdirectories.
        /// </summary>
        /// <param name="root">Absoulte path of root folder of items to be copied.</param>
        /// <param name="itemsToCopy">Names of folders and files to be copied.</param>
        /// <returns>Total file count, including files in subdirectories.</returns>
        private static long GetTotalFileCount(string root, string[] itemsToCopy)
        {
            long count = 0;
            foreach(string item in itemsToCopy)
            {
                string path = Path.Combine(root, item);
                if (File.Exists(path) == true)
                    count++;
                else if (Directory.Exists(path) == true)
                    count += Directory.GetFiles(path, "*", SearchOption.AllDirectories).LongLength;
                else
                    throw new Exception("Given path is not a file nor a directory: " + path + "!");
            }

            return count;
        }

        /// <summary>
        /// Encloses path in double quotes in case the path contains blanks.
        /// </summary>
        /// <param name="path">Path to enclose.</param>
        /// <returns>Path enclosed in double quotes.</returns>
        private static string QuoteEnclose(string path)
        {
            return '"' + path + '"';
        }

        /// <summary>
        /// Formatted current time.
        /// </summary>
        /// <returns>String representing date and tim in format "hh:mm:ss.ffffff ".</returns>
        public static string CurrentTime()
        {
            return DateTime.Now.ToString("HH:mm:ss.ffffff ");
        }

        static void Main(string[] args)
        {             
            // Used for debbuging.
            Debug.Listeners.Add(new TextWriterTraceListener(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), productName, "debug.log")));
            Debug.AutoFlush = true;

            Debug.WriteLine(Environment.NewLine + CurrentTime() + "PasteApp started");

            // File extensions must not be hidden in windows explorer itself
            // because "SelectedItems()" will return names of selected items 
            // as they are displayed in windows explorer.
            string regKey_ExplorerAdvanced = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
            var userSetting = (int)Registry.GetValue(regKey_ExplorerAdvanced, "HideFileExt", null);
            if (userSetting == 0x00000001)
            {
                string message =
                    "File name extensions must be visible before selecting items to copy! " + 
                    "Don't worry, we'll turn them on for you. Refresh folder view and repeat copy-paste process.";
                MessageBox.Show( message, "Error!", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                
                // Creates the key and value if they don't exist.
                Registry.SetValue(regKey_ExplorerAdvanced, "HideFileExt", 0, RegistryValueKind.DWord);
                return;
            }

            // Obtain destination folder from arguments.
            string destination;
            try
            {
                destination = args[0];
                Debug.WriteLine(CurrentTime() + "Paste path: " + args[0]);
            }
            catch(Exception e)
            {
                Debug.WriteLine(CurrentTime() + "[ERROR]" + e.Message);
                Debug.WriteLine(e.StackTrace);
                return ;
            }

            List<string> fileContents = GetFoldersAndFilesToCopyOrCut().ToList<string>();

            // There is nothing to copy.
            if (fileContents.Count == 0)
                return;

            string root = fileContents[0];
            fileContents.RemoveAt(0);
            string[] foldersAndFiles = fileContents.ToArray<string>();
            string[] files = foldersAndFiles.Where(item => File.Exists(Path.Combine(root, item))).ToArray();
            string[] folders = foldersAndFiles.Where(item => Directory.Exists(Path.Combine(root, item))).ToArray();

            Debug.WriteLine(CurrentTime() + "Items scheduled for pasting:");
            Debug.WriteLine(root + @"\");
            Debug.Indent();
            foreach (string file in files)
                Debug.WriteLine(file);
            foreach (string folder in folders)
                Debug.WriteLine(folder);
            Debug.Unindent();

            long totalFileCount = GetTotalFileCount(root, foldersAndFiles);
            long totalFileSize = GetTotalFileSize(root, foldersAndFiles);

            Debug.WriteLine(CurrentTime() + "Total file count: " + totalFileCount);
            Debug.WriteLine(CurrentTime() + "Total file size: " + totalFileSize);

            // Create robocopy script.
            
            // Commands to execute.
            List<string> commands = new List<string>();

            // Robocopy options for both files and folders.
            // nc - do not display item class
            // ndl - do not display copied directories
            // fp - display full file path
            // bytes - display file sizes in bytes
            // v - verbose output (includes skipped files in the output)
            // is - include (overwrite) same files
            // it - include (overwrite) "tweaked" files
            string options = @"/nc /ndl /fp /bytes /v /is /it";

            // Create commands to copy folders.
            foreach (string folder in folders)
            {
                string folderPath = Path.Combine(root, folder);
                if (Directory.Exists(folderPath) == true)
                    commands.Add(QuoteEnclose(folderPath) + " " + QuoteEnclose(Path.Combine(destination, folder)) + " /e" + " " + options);
                else
                    throw new Exception("The folder " + folderPath + " doesn't exist!");
            }

            // Create commands to copy files.
            foreach (string file in files)
            {
                string filePath = Path.Combine(root, file);
                if (File.Exists(filePath) == true)
                    commands.Add(QuoteEnclose(root) + " " + QuoteEnclose(destination) + " " + QuoteEnclose(file) + " " + options);
                else
                    throw new Exception("The file " + filePath + " doesn't exist!");
            }

            string copyScript = string.Join(Environment.NewLine, commands.ToArray());
            Debug.WriteLine(CurrentTime() + "Script to execute:");
            Debug.WriteLine(copyScript);

            // GUI
            CopyDialog copyDialog = new CopyDialog(totalFileCount, totalFileSize, root, destination);
            Thread UIThread = new Thread(() => Application.Run(copyDialog));
            UIThread.Start();
            
            Debug.WriteLine(CurrentTime() + "Executing script...");
            
            // Run robocopy script
            // For each robocopy command, a new robocopy.exe must be started.
            foreach (string command in commands) {
                if (operationAborted == true)
                {
                    Debug.WriteLine(CurrentTime() + "PasteApp aborted.");
                    return;
                }
                using (Process p = new Process())
                {
                    p.StartInfo.FileName = "robocopy.exe";
                    p.StartInfo.Arguments = command;
                    p.StartInfo.CreateNoWindow = true;
                    p.StartInfo.UseShellExecute = false;
                    p.StartInfo.RedirectStandardInput = true;
                    p.StartInfo.RedirectStandardError = true;
                    p.StartInfo.RedirectStandardOutput = true;

                    // Handlers that parse robocopy output and update GUI.
                    p.OutputDataReceived += copyDialog.RobocopyOutputHandler;
                    p.ErrorDataReceived += copyDialog.RobocopyErrorHandler;

                    p.OutputDataReceived += (s, e) => Debug.WriteLine(e.Data);
                    p.ErrorDataReceived += (s, e) => Debug.WriteLine(e.Data);

                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    currentRobocopyProcessId = p.Id;

                    p.WaitForExit();
                }
            }

            
            Debug.WriteLine(CurrentTime() + "Done");
            Debug.WriteLine(CurrentTime() + "PasteApp finished.");

            UIThread.Join();


            // Delete items if robo-cut was requested.
            // Deletion ocurs after GUI closes.
            // This prevents user from aborting delete process.
            // If cut operation is canceled, not a single file gets deleted.
            if (copyOrCutOption == "cut" && operationAborted == false)
            {
                foreach (string file in files)
                {
                    try
                    {
                        File.Delete(Path.Combine(root, file));
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine(CurrentTime() + "[ERROR]" + e.Message);
                        Debug.WriteLine(e.StackTrace);
                        return;
                    }
                }
                    
                foreach (string folder in folders)
                {
                    try
                    {
                        Directory.Delete(Path.Combine(root, folder), true);
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine(CurrentTime() + "[ERROR]" + e.Message);
                        Debug.WriteLine(e.StackTrace);
                        return;
                    }
                }
            }

        }

        private static void P_Exited(object sender, EventArgs e)
        {
            throw new NotImplementedException();
        }
    }
}
