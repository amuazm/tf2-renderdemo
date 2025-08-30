using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text.RegularExpressions;
using System.Management;

namespace RenderDemo
{
    class Program
    {

        /// <summary>
        /// </summary>
        /// <returns>0 if success, 1 if TF2 crashed, 2 if some error</returns>
        static int Main(string[] args)
        {
            // Process input.
            if (ParseArguments(args))
                return 0;

            Logger.Message(
                $"Demo: {Args.DemoPath}\n" +
                $"Output: {Args.OutputPath}\n" +
                $"Profile: {Args.ProfileName}\n" +
                $"Commands: \"{Args.UserCommands}\"\n"+
                $"OutputPath: \"{Args.OutputPath}\"",
                LogLevel.Info);

            Logger.Message("Ranges: ", LogLevel.Info);

            foreach (var tickRange in Args.TickRanges)
            {
                Logger.Message(tickRange.ToString(), LogLevel.Info);
            }

            // If in test mode, pause.
            if (Args.IsTest)
            {
                Console.Write("Test mode is on. Press any key to start . . .");
                Console.ReadKey(true);
                Console.Write('\n');
            }

            Logger.Message("Checking output file...", LogLevel.Debug);
            if (CheckOutputFile())
            {
                return 0;
            }

            // Get a random file name, remove the dot.
            // This key will be used for temporary files/directories that belong to this instance.
            string randomKey = Path.GetRandomFileName().Remove(8, 1);

            // Temporary folder to hold temporary files TF2 will load.
            var tempFolderPath = $"temp\\{randomKey}";

            // Create a temporary copy of the demo to be recorded.
            Logger.Message("Importing demo...", LogLevel.Debug);

            if (ImportDemo(tempFolderPath))
            {
                return 2;
            }

            // Create CFGs.
            Logger.Message("Generating CFGs...", LogLevel.Debug);

            if (GenerateCFGs(tempFolderPath, randomKey))
            {
                return 2;
            }

            Logger.Message("Generating VDMs...", LogLevel.Debug);

            if (GenerateVDM(tempFolderPath))
            {
                return 2;
            }


            Logger.Message("Generating SVR game launch file...", LogLevel.Debug);
            try
            {
                File.WriteAllText(Path.Combine(Args.SdrDirectory, "data", "games", "tf2_renderdemo.ini"),
                    "name=Team Fortress 2\n" +
                    "steam_path=common\\Team Fortress 2\\tf_win64.exe\n" +
                    $"args=-game tf -insert_search_path \"{Path.GetFullPath("config")},{Path.GetFullPath(tempFolderPath)}\" +exec renderdemo\\autoexec.cfg {Args.LaunchOptions}\n"
                    );
            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
                return 2;
            }

            Logger.Message("Generating SVR video render profile...", LogLevel.Debug);
            try
            {
                File.WriteAllText(Path.Combine(Args.SdrDirectory, "data", "profiles", "tf2_renderdemo_profile.ini"),
                    "video_fps=120\n"
                    );
            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
                return 2;
            }

            bool tf2Crashed;

            // Start TF2 via the SDR launcher.
            using (Process tf2 = StartSDR(tempFolderPath))
            {
                // Did TF2 start?
                if (tf2 == null)
                {
                    Logger.Message("TF2 was null!", LogLevel.Debug);
                    return 2;
                }

                // Sleep until there's a window to work with.
                Logger.Message("Waiting for a window to work with...", LogLevel.Debug);

                while (tf2.MainWindowHandle == IntPtr.Zero)
                {
                    Thread.Sleep(100);
                }

                //FixTF2sWindow(tf2);

                // Wait until CFGs finish recording and/or TF2 exits.
                Logger.Message("Waiting for recordings to finish and or TF2 exits...", LogLevel.Debug);
                tf2Crashed = !HandleCFGsCommunication(tf2, randomKey);
            }

            try
            {
                // Delete the console log file.
                Logger.Message($"Deleting...${Path.Combine(Args.TfDirectory, $"renderdemo_{randomKey}.log")}", LogLevel.Debug);
                File.Delete(Path.Combine(Args.TfDirectory, $"renderdemo_{randomKey}.log"));

                // Delete temp folder.
                Logger.Message("Deleting temp folder...", LogLevel.Debug);
                Directory.Delete(tempFolderPath, true);
            }
            catch { }

            // Return
            if (tf2Crashed)
            {
                return 1;
            }
            else
            {
                return 0;
            }
        }

        /// <summary>
        /// Parse arguments passed to the program.
        /// </summary>
        /// <returns>If to exit.</returns>
        private static bool ParseArguments(string[] args)
        {
            // If no arguments were passed, show info message and exit.
            if (args.Length == 0)
            {
                Console.WriteLine("RenderDemo - Automatically render out TF2 demos using SourceDemoRender.\nTry 'renderdemo -help' for more information.");
                return true;
            }

            // If -help was passed, print help and exit.
            if (Array.IndexOf(args, "-help") != -1)
            {
                PrintHelp();
                return true;
            }

            // Make sure autoexec exists.
            if (!File.Exists(@"config\cfg\renderdemo\autoexec.cfg"))
            {
                Logger.Message($"ERROR: Missing file '{Directory.GetCurrentDirectory()}\\config\\cfg\\renderdemo\\autoexec.cfg'.", LogLevel.Error);
                return true;
            }

            // Parse arguments.
            try
            {
                Args.Parse(args);
            }
            // If arguments were invalid, print error message and exit.
            catch (InvalidInputException e)
            {
                Logger.Message(e.Message, LogLevel.Error);
                return true;
            }

            // Everything OK.
            return false;
        }

        /// <summary>
        /// Print out a help message to the console.
        /// </summary>
        private static void PrintHelp() => Console.WriteLine(
            "RenderDemo v1.0\n" +
            "Automatically render out TF2 demos using SourceDemoRender.\n" +
            "https://github.com/juniorsgithub/tf2-renderdemo\n" +
            "\nusage: renderdemo [arguments]\n" +
            "\nRequired arguments:\n" +
            "-exepath <path>\t\t\tTF2's hl2.exe path\n" +
            "-demo <path>\t\t\tdemo to record (relative to tf directory or full path)\n" +
            "-start <int>\t\t\ttick to start recording at\n" +
            "-end <int>|max\t\t\ttick to stop recording at ('max' to record until the end)\n" +
            "-out <path>\t\t\toutput movie path (relative to working directory or full path)\n" +
            "\nOptional arguments:\n" +
            "-sdrdir <path>\t\t\tSourceDemoRender directory (default [working dir]\\SDR)\n" +
            "-tfdir <path>\t\t\tTF2's write directory (default [exepath dir]\\tf)\n" +
            "-profile video|audio|both\trecording profile (default both)\n" +
            "\t\t\t\t   video - 1 pass, video only\n" +
            "\t\t\t\t   audio - 1 pass, audio only\n" +
            "\t\t\t\t   both  - 2 passes, video and audio\n" +
            "-cmd \"[commands]\"\t\tTF2 console commands to execute before recording starts\n" +
            "-launch \"[launch options]\"\tadditional TF2 launch options\n" +
            "-window hidden|broken|fixed\tTF2's window state (default fixed)\n" +
            "\t\t\t\t   hidden - inaccessible, no taskbar icon\n" +
            "\t\t\t\t   broken - inaccessible, taskbar icon\n" +
            "\t\t\t\t   fixed  - accessible, taskbar icon\n" +
            "-overwrite yes|no|ask\t\toutput overwrite action (default ask)\n" +
            "-test\t\t\t\tpause before starting TF2\n" +
            "-loglevel debug|info|brief|progress|error|quiet\n" +
            "\nexample: renderdemo -exepath \"C:\\Program Files (x86)\\Steam\\steamapps\\common\\Team Fortress 2\\hl2.exe\" -demo \"demos\\my demo\" -start 4100 -end 4700 -out airshot.avi -launch \"-width 1920 -height 1080\" -cmd \"spec_player myname; spec_mode 5\"\n"
        );

        /// <summary>
        /// Handles overwriting of the output file.
        /// </summary>
        /// <returns>If to exit.</returns>
        private static bool CheckOutputFile()
        {
            if (!File.Exists(Args.OutputPath))
                return false;

            // If -overwrite was set.
            switch (Args.OverwriteAction)
            {
                case OverwriteAction.AutoYes:
                    Logger.Message($"Output file '{Args.OutputPath}' already exists and will be overwitten.", LogLevel.Brief, ConsoleColor.Yellow);
                    return false;
                case OverwriteAction.AutoNo:
                    Logger.Message($"Output file '{Args.OutputPath}' already exists. Not overwriting.", LogLevel.Brief);
                    return true;
            }

            // Ask the user.
            while (true)
            {
                Console.WriteLine($"Output file '{Args.OutputPath}' already exists.\nOverwrite? [y/n]");

                switch (Console.ReadLine().ToLower())
                {
                    case "y":
                    case "yes":
                        return false;
                    case "n":
                    case "no":
                        return true;
                }
            }
        }

        /// <summary>
        /// Copy the demo to the temporary folder.
        /// </summary>
        /// <returns>If to exit.</returns>
        private static bool ImportDemo(string tempFolder)
        {
            try
            {
                Directory.CreateDirectory(Path.Combine(tempFolder, "renderdemo"));
                File.Copy(Args.DemoPath, Path.Combine(tempFolder, @"renderdemo\demo_to_record.dem"));
            }
            catch (Exception e)
            {
                Logger.Message($"ERROR: Couldn't copy demo: {e.Message}", LogLevel.Error);
                return true;
            }

            // Everything OK.
            return false;
        }

        /// <summary>
        /// Creates required CFG files in a temporary folder.
        /// </summary>
        /// <returns>If to  exit.</returns>
        private static bool GenerateCFGs(string _tempFolderPath, string _randomKey)
        {

            var cfgDirectory = Path.Combine(_tempFolderPath, @"cfg\renderdemo");

            try
            {
                Directory.CreateDirectory(cfgDirectory);

                // Sets cvars and aliases.
                File.WriteAllText(
                    Path.Combine(cfgDirectory, "cvars.cfg"),
                    $"sdr_outputdir \"{Path.GetDirectoryName(Args.OutputPath)}\"\n" +
                    $"alias renderdemo_log \"con_logfile renderdemo_{_randomKey}.log\"\n" +
                    $"alias renderdemo_startmovie \"startmovie {Path.GetFileName(Args.OutputPath)} profile=tf2_renderdemo_profile\"\n" +
                    $"alias renderdemo_profile exec \"renderdemo/profiles/{Args.ProfileName}\"\n" +
                    $"alias renderdemo_user_commands \"{Args.UserCommands}\""
                );
            }
            catch (Exception e)
            {
                Logger.Message($"Error creating CFGs: {e.Message}", LogLevel.Error);
                return true;
            }

            // Everthing OK.
            return false;
        }

        private static string GameLog(string logMessage)
        {
            return $"renderdemo_log; echo renderdemo_message={logMessage}\\n; renderdemo_nolog;";
        }

        /// <summary>
        /// Creates a VDM file to control the recording process.
        /// </summary>
        /// <returns>If to exit.</returns>
        private static bool GenerateVDM(string tempFolder)
        {
            var vdm = new VDM();

            var firstStart = Args.TickRanges[0].Start;
            vdm.Add(1, GameLog($"Running_prep_and_user_commands") +
                $"hud_reloadscheme;" +
                $"{Args.UserCommands};");
            vdm.Add(2, GameLog($"Going_to_{firstStart}") +
                $"demo_gototick {firstStart};");

            for (int i = 0; i < Args.TickRanges.Count; i++)
            {
                var tickRange = Args.TickRanges[i];

                vdm.Add(tickRange.Start, GameLog($"Recording_range_{i + 1}/{Args.TickRanges.Count}") +
                    $"snd_restart;" +
                    $"startmovie {Args.OutputPath}-{i + 1}.mov;");

                string command = GameLog($"Ending_range_{i + 1}") +
                    $"endmovie;";

                // Is there a next tickRange?
                if (i + 1 < Args.TickRanges.Count)
                {
                    var nextTickRange = Args.TickRanges[i + 1];
                    command += GameLog($"Going_to_{nextTickRange.Start}") +
                    $"demo_gototick {nextTickRange.Start};";
                } else
                {
                    command += GameLog($"Quitting") +
                    $"quit;";
                }

                vdm.Add(tickRange.End, command);
            }

            //// Tick 0 seemed to crash more often.
            //vdm.Add(1, $"renderdemo_load", "Demo loaded");

            //// Fast forward to start tick if it's not right at the beginning.
            //int startTick = Args.TickRanges[0].Start;

            //if (startTick > 3)
            //{
            //    vdm.Add(2, $"renderdemo_ff; " +
            //        $"demo_gototick {startTick - 2}; " +
            //        $"renderdemo_log; " +
            //        $"echo renderdemo_message=[debug]_test2;" +
            //        $"renderdemo_nolog",
            //        "Fast forward");
            //    vdm.Add(startTick - 1, "renderdemo_prep", "Prepare recording");
            //}

            //// Log recording progress in 5% steps.
            //int endTick = Args.TickRanges[0].End;
            //double step = (endTick - startTick) / 100.0;

            //int lastTick = -1;

            //for (var i = 0; i <= 100; i++)
            //{
            //    int tick = startTick + (int)(i * step);

            //    // Avoid adding multiple entries at the same tick.
            //    if (tick == lastTick)
            //    {
            //        continue;
            //    }

            //    lastTick = tick;

            //    vdm.Add(tick, $"renderdemo_{i}perc", $"Recording: {i}%");
            //}

            try
            {
                // Save the VDM.
                vdm.Save(Path.Combine(tempFolder, @"renderdemo\demo_to_record.vdm"));
            }
            catch (Exception e)
            {
                Logger.Message($"Error saving VDM: {e.Message}.", LogLevel.Error);
                return true;
            }

            // Everything OK.
            return false;
        }

        /// <summary>
        /// Start TF2 via the SDR launcher.
        /// </summary>
        /// <returns>The TF2 process.</returns>
        private static Process StartSDR(string _tempFolderPath)
        {
            var sdrStartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Args.SdrDirectory, "svr_launcher64.exe"),
                //Arguments =
                //	$"/GAME \"{Args.ExePath}\" " +
                //	$"/PATH \"{Path.Combine(Path.GetDirectoryName(Args.ExePath), "tf")}\" " +
                //	$"/PARAMS \"-novid -nojoy -nosteamcontroller -condebug" +
                //		// -y 9999 to hide the window
                //		$"-window -y 9999 {Args.LaunchOptions} " +
                //		// add our CFGs, ""escaping"" quotes like this seems do work fine
                //		$"-insert_search_path \"\"{Path.GetFullPath("config")},{Path.GetFullPath(_tempFolderPath)}\"\" " +
                //		$"+exec renderdemo/autoexec",
                Arguments = "tf2_renderdemo.ini",
                WorkingDirectory = Args.SdrDirectory,
                UseShellExecute = false
            };

            Logger.Message($"Process start info: {sdrStartInfo}...", LogLevel.Debug);
            Logger.Message("Launching SVR...", LogLevel.Brief);

            using (var sdr = Process.Start(sdrStartInfo))
            {
                // Search for child processes.
                Logger.Message("Searching for child processes...", LogLevel.Debug);
                ManagementObjectSearcher mos = new ManagementObjectSearcher(
                    $"Select * From Win32_Process Where ParentProcessID={sdr.Id}");
                ManagementObjectCollection moc;

                // Wait until there is a child process.
                do
                {
                    moc = mos.Get();
                    Logger.Message("Waiting for child process...", LogLevel.Debug);
                    Thread.Sleep(100);
                }
                while (moc.Count != 1);

                var mo = new ManagementObject[1];
                moc.CopyTo(mo, 0);

                // TF2 is the only child process of SDR
                Logger.Message("Getting TF2 process...", LogLevel.Debug);
                var tf2 = Process.GetProcessById(Convert.ToInt32(mo[0]["ProcessID"]));

                return tf2;
            }
        }

        private static void FixTF2sWindow(Process tf2Process)
        {
            switch (Args.WindowState)
            {
                case WindowState.Broken:
                    return;

                case WindowState.Fixed:
                    //// Move window to X 0, Y 0, Z bottom
                    //SetWindowPos(tf2Process.MainWindowHandle, (IntPtr)1, 0, 0, 0, 0, 0x0001);
                    //// Minimize window
                    //ShowWindow(tf2Process.MainWindowHandle, 6);
                    break;

                case WindowState.Hidden:
                    // Hide window
                    ShowWindow(tf2Process.MainWindowHandle, 0);
                    break;
            }
        }

        /// <summary>
        /// Read and process output from CFGs. Wait until CFGs are finished or TF2 exits.
        /// </summary>
        /// <returns>If CFGs were able to finish.</returns>
        private static bool HandleCFGsCommunication(Process _tf2Process, string _randomKey)
        {
            Logger.Message($"Log file name: {Path.Combine(Args.TfDirectory, $"renderdemo_{_randomKey}.log")}", LogLevel.Debug);
            var logPath = Path.Combine(Args.TfDirectory, $"renderdemo_{_randomKey}.log");

            // Wait for the log file.
            Logger.Message("Waiting for log file to exist...", LogLevel.Debug);

            while (!File.Exists(logPath))
            {
                if (_tf2Process.HasExited)
                {
                    Logger.Message("TF2 crashed on launch.", LogLevel.Error, ConsoleColor.Red);
                    return false;
                }

                Thread.Sleep(100);
            }

            Logger.Message("Using log file...", LogLevel.Debug);
            using (var log = new ConsoleLog(logPath))
            {
                // If TF2 didn't crash and recording was finished.
                Logger.Message("Parsing console log...", LogLevel.Debug);

                if (!ParseConsoleLog(log, _tf2Process))
                {
                    // Kill the process to prevent config.cfg from being updated with settings that were changed.
                    Logger.Message("Killing TF2 process...", LogLevel.Debug);
                    _tf2Process.Kill();
                    // Return success.
                    return true;
                }
            }

            // If it got here, that means TF2 has exited before the final message from CFGs.

            // Make sure cursor is at a new line.
            if (Console.CursorLeft != 0)
            {
                Console.Write('\n');
            }

            Logger.Message("TF2 has exited before CFGs could finish. Recording might have failed.", LogLevel.Error, ConsoleColor.Red);

            // Return that CFGs were not able to finish (TF2 crashed).
            return false;
        }

        /// <summary>
        /// Parses console log messages until TF2 exits.
        /// </summary>
        /// <returns>If TF2 crashed.</returns>
        private static bool ParseConsoleLog(ConsoleLog consoleLog, Process tf2Process)
        {
            var isTf2Running = true;

            while (isTf2Running)
            {
                // Update the condition late to get one last loop after TF2 closes.
                isTf2Running = !tf2Process.HasExited;

                // Sleep and repeat if there are no new lines.
                if (consoleLog.EndOfStream)
                {
                    Thread.Sleep(100);
                    continue;
                }

                // Parse all new lines.
                do
                {
                    string line = consoleLog.ReadLine();
                    //Logger.Message($"Found log line: {line}", LogLevel.Debug);
                    var match = Regex.Match(line, "(?<=renderdemo_message=)[^ ;:'\"]+");

                    if (!match.Success)
                    {
                        continue;
                    }

                    // Message signaling CFGs have finished.
                    if (match.Value == "renderdemo_quit")
                    {
                        Logger.Message("Found \"renderdemo_quit\"", LogLevel.Debug);
                        Logger.Message("Recording finished successfully.", LogLevel.Progress, ConsoleColor.Green);
                        return false;
                    }
                    // Some other message. Print it out if log level is set low enough.
                    else if (Args.LogLevel <= LogLevel.Progress)
                    {
                        Console.Write(ParseMessage(match.Value));
                    }
                }
                while (!consoleLog.EndOfStream);
            }

            // If it got there, that means TF2 crashed.
            return true;
        }

        /// <summary>
        /// Replaces escaped symbols with corresponding characters.
        /// </summary>
        /// <returns>Parsed string to print out.</returns>
        private static string ParseMessage(string line)
        {
            // Replace underscores with spaces.
            line = line.Replace("_", " ");

            // Newlines.
            line = line.Replace(@"\n", Environment.NewLine);

            // Carriage returns.
            line = line.Replace(@"\r", "\r");

            // Escaped unicode character codes (\0000).
            line = Regex.Replace(line, @"\\[0-9a-fA-f]{4}", (m) => Char.ConvertFromUtf32(int.Parse(m.Value.Substring(1), System.Globalization.NumberStyles.HexNumber)));

            // Parsed.
            return line;
        }


        #region Imported Functions

        /// <summary>
        /// Change window state.
        /// </summary>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        /// <summary>
        /// Change window position.
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        #endregion
    }
}
