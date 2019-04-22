﻿using GitScc;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace VSIXProject2019
{
    /// <summary>
    /// Interaction logic for GitConsole.xaml
    /// </summary>
    public partial class GitConsole : UserControl
    {
        private Brush BRUSH_PROMPT = new SolidColorBrush(Colors.Black);
        private Brush BRUSH_ERROR = new SolidColorBrush(Colors.Red);
        private Brush BRUSH_OUTPUT = new SolidColorBrush(Colors.Green);
        private Brush BRUSH_HELP = new SolidColorBrush(Colors.Black);

        private BackgroundWorker outputWorker = new BackgroundWorker();
        private BackgroundWorker errorWorker = new BackgroundWorker();
        private Process process;
        private StreamWriter inputWriter;
        private TextReader outputReader;
        private TextReader errorReader;

        private GitRepository _repository;
        private GitRepository repository
        {
            get { return _repository; }
            set
            {
                this._repository = value;
                this.WorkingDirectory = this._repository == null ?
                    Environment.GetFolderPath(Environment.SpecialFolder.Personal) :
                    this.repository.WorkingDirectory;
            }
        }

        public string workingDirectory;
        public string WorkingDirectory
        {
            get { return workingDirectory; }
            set
            {
                if (string.Compare(workingDirectory, value) != 0)
                {
                    workingDirectory = value;
                    this.richTextBox1.Document.Blocks.Clear();
                    prompt = ""; //force re-write prompt
                }
            }
        }

        public string GitExePath { get; set; }

        string prompt = ">";

        List<string> commandHistory = new List<string>();
        int commandIdx = -1;

        public GitConsole()
        {
            InitializeComponent();

            outputWorker.WorkerReportsProgress = true;
            outputWorker.WorkerSupportsCancellation = true;
            outputWorker.DoWork += outputWorker_DoWork;

            errorWorker.WorkerReportsProgress = true;
            errorWorker.WorkerSupportsCancellation = true;
            errorWorker.DoWork += errorWorker_DoWork;
        }


        #region keydown event

        string lastText = "";

        private void richTextBox1_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (lstOptions.Visibility == Visibility.Visible)
            {
                if (e.Key == Key.Escape)
                {
                    this.HideOptions();
                }
            }

            if (!IsCaretPositionValid())
            {
                this.richTextBox1.CaretPosition = this.richTextBox1.CaretPosition.DocumentEnd;
                return;
            }

            if (e.Key == Key.Tab)
            {
                if (lstOptions.Visibility == Visibility.Visible)
                {
                    InsertText(lstOptions.SelectedValue as string);
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                if (lstOptions.Visibility == Visibility.Visible)
                {
                    InsertText(lstOptions.SelectedValue as string);
                    e.Handled = true;
                }
                else
                {
                    var command = GetCommand();
                    if (this.IsProcessRunning)
                    {
                        this.WriteInput(command);
                    }
                    else
                    {
                        RunCommand(command);
                        e.Handled = true;
                    }
                }
            }
            else if (e.Key == Key.Up)
            {
                if (lstOptions.Visibility == Visibility.Visible)
                {
                    if (lstOptions.SelectedIndex > 0) lstOptions.SelectedIndex -= 1;
                    lstOptions.ScrollIntoView(lstOptions.SelectedItem);
                }
                else
                {
                    GetCommand(--commandIdx);
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                if (lstOptions.Visibility == Visibility.Visible)
                {
                    lstOptions.SelectedIndex += 1;
                    if (lstOptions.SelectedIndex < lstOptions.Items.Count - 1) lstOptions.SelectedIndex += 1;
                }
                else
                {
                    GetCommand(++commandIdx);
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                ChangePrompt("", BRUSH_PROMPT);
                this.HideOptions();
            }
            else if (e.Key == Key.Back)
            {
                var text = new TextRange(richTextBox1.CaretPosition.GetLineStartPosition(0),
                    richTextBox1.CaretPosition).Text;
                if (text.EndsWith(">") && text.IndexOf(">") == text.Length - 1) e.Handled = true;
            }
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (IsProcessRunning)
                {
                    SendShutdownToConsole();
                    e.Handled = true;
                }
            }
        }

        private void richTextBox1_KeyUp(object sender, KeyEventArgs e)
        {
            FilterOptions();
        }

        private string GetCommand()
        {
            var command = new TextRange(
                richTextBox1.CaretPosition.GetLineStartPosition(0)
                .GetPositionAtOffset(lastText.Length + 1, LogicalDirection.Forward) ??
                richTextBox1.CaretPosition.GetLineStartPosition(0),
                richTextBox1.CaretPosition.GetLineStartPosition(1) ?? this.richTextBox1.CaretPosition.DocumentEnd).Text;

            command = command.Replace("\r", "").Replace("\n", "");
            return command;
        }

        private bool IsCaretPositionValid()
        {
            var text = new TextRange(richTextBox1.CaretPosition, richTextBox1.CaretPosition.DocumentEnd).Text;
            return !text.Contains(">");
        }

        private bool ShowWaring()
        {
            var result = GitBash.Run("config credential.helper", this.WorkingDirectory);

            if (string.IsNullOrWhiteSpace(result.Output))
            {
                WriteError("Git credential helper is not installed. Please download and installed from https://gitcredentialstore.codeplex.com/");
                WritePrompt();
                return true;
            }
            return false;
        }

        private void WriteHelp()
        {
            WriteText(@"Git console commands:

    cls:     clear the screen
    clear:   clear the screen
    dir:     windows shell command dir 
    git:     launch git bash
    git xxx: launch supported git command xxx
", BRUSH_HELP);

        }

        #endregion

        #region run command and command history

        private void GetCommand(int idx)
        {
            if (commandHistory.Count > 0)
            {
                if (idx < 0) idx = 0;
                else if (idx > commandHistory.Count - 1) idx = commandHistory.Count - 1;
                var command = commandHistory[idx];
                commandIdx = idx;
                ChangePrompt(command, BRUSH_PROMPT);
            }
        }


        private void RunCommand(string command)
        {
            BRUSH_HELP = BRUSH_PROMPT = this.richTextBox1.Foreground;

            if (!string.IsNullOrWhiteSpace(command) &&
               (commandHistory.Count == 0 || commandHistory.Last() != command))
            {
                commandHistory.Add(command);
                commandIdx = commandHistory.Count;
            }

            if (!ProcessInternalCommand(command))
            {
                if (command == "git")
                {
                    GitBash.OpenGitBash(string.IsNullOrWhiteSpace(this.WorkingDirectory) ?
                        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) :
                        this.WorkingDirectory);
                    WritePrompt();
                    return;
                }


                var idx = command.IndexOf(' ');

                if (idx < 0)
                {
                    StartProcess(command, null);
                }
                else
                {
                    var cmd = command.Substring(0, idx);
                    var param = command.Substring(idx);
                    StartProcess(cmd, param);
                }
            }
        }

        #endregion

        #region From Console Control
        public void StartProcess(string fileName, string arguments)
        {
            ShowStatusMessage("Running command ...");

            //  Create the process start info.
            var processStartInfo = new ProcessStartInfo(fileName, arguments);

            //  Set the options.
            processStartInfo.UseShellExecute = false;
            processStartInfo.ErrorDialog = false;
            processStartInfo.CreateNoWindow = true;
            processStartInfo.WorkingDirectory = this.WorkingDirectory;

            //  Specify redirection.
            processStartInfo.RedirectStandardError = true;
            processStartInfo.RedirectStandardInput = true;
            processStartInfo.RedirectStandardOutput = true;

            processStartInfo.StandardOutputEncoding = Encoding.UTF8;
            processStartInfo.StandardErrorEncoding = Encoding.UTF8;

            //  Create the process.
            process = new Process();
            process.EnableRaisingEvents = true;
            process.StartInfo = processStartInfo;
            process.Exited += currentProcess_Exited;


            try
            {
                processStartInfo.CreateNoWindow = false;
                processStartInfo.WindowStyle = ProcessWindowStyle.Minimized;

                var args = " /C " + fileName;
                if (!string.IsNullOrEmpty(arguments)) args += " " + arguments;
                processStartInfo.FileName = "cmd.exe";
                processStartInfo.Arguments = args;
                process.StartInfo = processStartInfo;
                process.Start();
                HideConsoleWindow();
            }
            catch (Exception)
            {
                process = null;
            }

            if (process == null)
            {
                this.WriteError("Failed to start process \"" + fileName + "\" with arguments \"" + arguments + "\"");
                this.WritePrompt();
                return;
            }

            //  Create the readers and writers.
            inputWriter = process.StandardInput;
            outputReader = TextReader.Synchronized(process.StandardOutput);
            errorReader = TextReader.Synchronized(process.StandardError);

            //  Run the workers that read output and error.
            if (outputWorker.IsBusy) outputWorker.CancelAsync();
            if (errorWorker.IsBusy) errorWorker.CancelAsync();

            outputWorker.RunWorkerAsync();
            errorWorker.RunWorkerAsync();
        }

        private void currentProcess_Exited(object sender, EventArgs e)
        {
            //  Disable the threads.
            outputWorker.CancelAsync();
            errorWorker.CancelAsync();
            inputWriter = null;
            outputReader = null;
            errorReader = null;
            process = null;

            WritePrompt();

            ShowStatusMessage("");
        }

        public bool IsProcessRunning
        {
            get
            {
                try
                {
                    return (process != null && process.HasExited == false);
                }
                catch
                {
                    return false;
                }
            }
        }


        private void errorWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            var buffer = new char[1024];
            var sb = new StringBuilder();
            while (errorWorker.CancellationPending == false && errorReader != null)
            {
                var count = errorReader.Read(buffer, 0, 1024);
                if (count > 0)
                {
                    sb.Append(buffer, 0, count);
                    if (buffer[count - 1] == '\n')
                    {
                        this.WriteError(sb.ToString().TrimEnd());
                        sb.Clear();
                    }
                }
                else break;
            }
            //System.Threading.Thread.Sleep(200);
        }

        private void outputWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            var buffer = new char[1024];
            var sb = new StringBuilder();
            while (outputWorker.CancellationPending == false && outputReader != null)
            {
                var count = outputReader.Read(buffer, 0, 1024);
                if (count > 0)
                {
                    sb.Append(buffer, 0, count);
                    this.WriteOutput(sb.ToString().TrimEnd());
                    sb.Clear();
                }
                else break;
            }
            //System.Threading.Thread.Sleep(200);
        }

        public void WriteInput(string input)
        {
            if (IsProcessRunning)
            {
                inputWriter.WriteLine(input);
                inputWriter.Flush();
            }
        }

        #endregion

        #region Write output/error and prompt

        void WritePrompt()
        {
            Action act = () =>
            {
                WritePromptText();
            };
            this.Dispatcher.BeginInvoke(act, DispatcherPriority.Normal);
        }

        void WriteError(string data)
        {
            Action act = () =>
            {
                WriteText(data, BRUSH_ERROR);
            };
            this.Dispatcher.BeginInvoke(act, DispatcherPriority.Normal);
        }

        void WriteOutput(string data)
        {
            Action act = () =>
            {
                WriteText(data, BRUSH_OUTPUT);
            };
            this.Dispatcher.BeginInvoke(act, DispatcherPriority.Normal);
        }

        void ChangePrompt(string command, Brush brush)
        {
            this.richTextBox1.CaretPosition = this.richTextBox1.CaretPosition.DocumentEnd;
            var range = this.richTextBox1.Selection;
            range.Select(
                richTextBox1.CaretPosition.GetLineStartPosition(0).GetPositionAtOffset(prompt.Length + 1, LogicalDirection.Forward),
                richTextBox1.CaretPosition.GetLineStartPosition(1) ?? this.richTextBox1.CaretPosition.DocumentEnd);
            range.Text = command;
            range.ApplyPropertyValue(ForegroundProperty, brush);
            this.richTextBox1.ScrollToEnd();
            this.richTextBox1.CaretPosition = this.richTextBox1.CaretPosition.DocumentEnd;
        }

        void RefreshPrompt()
        {
            var newprompt = _repository == null || !_repository.IsGit ?
                string.Format("[{0}]>", "Not a Git repostiory") :
                string.Format("[{0}]>", repository.ChangedFilesStatus);

            if (prompt != newprompt)
            {
                prompt = newprompt;
                WritePrompt();
            }
        }

        private void WritePromptText()
        {
            Paragraph para = new Paragraph();
            para.Margin = new Thickness(0, 10, 0, 0);
            para.FontFamily = new FontFamily("Lucida Console");
            para.LineHeight = 10;
            para.Inlines.Add(new Run(prompt));
            this.richTextBox1.Document.Blocks.Add(para);

            this.richTextBox1.ScrollToEnd();
            this.richTextBox1.CaretPosition = this.richTextBox1.CaretPosition.DocumentEnd;
            this.HideOptions();

            lastText = prompt;
        }

        private void WriteText(string text, Brush brush)
        {
            if (text == "") return;
            Paragraph para = new Paragraph();
            para.Margin = new Thickness(0, 2, 0, 0);
            para.FontFamily = new FontFamily("Lucida Console");
            para.LineHeight = 10;
            para.Inlines.Add(new Run(text) { Foreground = brush });
            this.richTextBox1.Document.Blocks.Add(para);

            this.richTextBox1.ScrollToEnd();
            this.richTextBox1.CaretPosition = this.richTextBox1.CaretPosition.DocumentEnd;

            lastText = text;
        }

        private bool ProcessInternalCommand(string command)
        {
            command = command.ToLower();
            if (string.IsNullOrWhiteSpace(command))
            {
                this.WritePrompt();
                return true;
            }
            else if (command == "help" || command == "?")
            {
                WriteHelp();
                this.WritePrompt();
                return true;
            }
            else if (command == "clear" || command == "cls")
            {
                this.richTextBox1.Document.Blocks.Clear();
                this.WritePrompt();
                return true;
            }
            return false;
        }

        #endregion

        #region intellisense

        private void ShowOptions(string command)
        {
            var options = GetOptions(command);
            if (options != null && options.Any())
            {
                Rect rect = this.richTextBox1.CaretPosition.GetCharacterRect(LogicalDirection.Forward);
                double d = this.ActualHeight - (rect.Y + lstOptions.Height + 12);
                double left = rect.X + 6;
                double top = d > 0 ? rect.Y + 12 : rect.Y - lstOptions.Height;
                left += this.Padding.Left;
                top += this.Padding.Top;
                lstOptions.SetCurrentValue(ListBox.MarginProperty, new Thickness(left, top, 0, 0));
                lstOptions.ItemsSource = options;
                ShowOptions();
            }
        }

        private void FilterOptions()
        {
            var command = GetCommand();
            if (command != null && command.IndexOf(' ') > 0)
            {
                var idx = command.LastIndexOf(' ');
                var options = GetOptions(command.Substring(0, idx));
                if (options != null && options.Any())
                {
                    Rect rect = this.richTextBox1.CaretPosition.GetCharacterRect(LogicalDirection.Forward);
                    double d = this.ActualHeight - (rect.Y + lstOptions.Height + 12);
                    double left = rect.X + 6;
                    double top = d > 0 ? rect.Y + 12 : rect.Y - lstOptions.Height;
                    left += this.Padding.Left;
                    top += this.Padding.Top;
                    lstOptions.SetCurrentValue(ListBox.MarginProperty, new Thickness(left, top, 0, 0));
                    lstOptions.ItemsSource = options;

                    var subcommand = command.Substring(idx + 1);
                    var opts = options.Where(item => item.StartsWith(subcommand));
                    lstOptions.ItemsSource = opts;
                    lstOptions.Visibility = (opts.Count() > 0 && opts.First() != subcommand) ?
                        Visibility.Visible : Visibility.Collapsed;

                    if (opts.Count() == 1) lstOptions.SelectedIndex = 0;
                }
                else
                {
                    HideOptions();
                }
            }
            else
            {
                HideOptions();
            }
        }

        private void lstOptions_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            InsertText(lstOptions.SelectedValue as string);
        }

        private void InsertText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var current = new TextRange(richTextBox1.CaretPosition.GetLineStartPosition(0),
                richTextBox1.CaretPosition).Text;
            var offset = current.LastIndexOf(' ') - current.Length + 1;
            var textRange = new TextRange(this.richTextBox1.CaretPosition.GetPositionAtOffset(offset),
                this.richTextBox1.CaretPosition.DocumentEnd);
            textRange.Text = text;
            this.richTextBox1.CaretPosition = this.richTextBox1.CaretPosition.DocumentEnd;
            this.HideOptions();
        }

        #endregion

        #region git command intellisense
        private IEnumerable<string> GetOptions(string command)
        {
            return GitIntellisenseHelper.GetOptions(repository, command);
        }
        #endregion

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            this.richTextBox1.Focus();
        }

        internal void Refresh(GitRepository repository)
        {
            this.repository = repository;
            RefreshPrompt();
        }

        #region native
        [DllImport("User32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
        public static extern IntPtr SetWindowPos(IntPtr hWnd, int hWndInsertAfter, int x, int Y, int cx, int cy, int wFlags);


        [DllImport("user32.dll")]
        static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, uint dwExtraInfo);

        private static void SendCtrlC(IntPtr hWnd)
        {
            const uint keyeventfKeyup = 2;
            const byte vkControl = 0x11;
            //hWnd == handle to console window
            //set it to foreground or u can not send commands
            SetForegroundWindow(hWnd);
            //sending keyboard event Ctrl+C
            keybd_event(vkControl, 0, 0, 0);
            keybd_event(0x43, 0, 0, 0);
            keybd_event(0x43, 0, keyeventfKeyup, 0);
            keybd_event(vkControl, 0, keyeventfKeyup, 0);
        }


        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        #endregion

        #region Ctrl + C

        private void HideConsoleWindow()
        {
            if (process.HasExited) return;
            while (process.MainWindowHandle == IntPtr.Zero)
            {
                //wait (do not thread.sleep here, it will auto release on !IntPtr.Zero(when it gets the handle))
            }
            //process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            //ShowWindow(process.MainWindowHandle, 0);
            SetWindowPos(process.MainWindowHandle, 0, 0, 0, 0, 0, 0);
            Window window = Window.GetWindow(this);
            window.Activate();
        }

        private void SendShutdownToConsole()
        {
            if (process.HasExited) return;
            SendCtrlC(process.MainWindowHandle);
            Window window = Window.GetWindow(this);
            window.Activate();
        }
        #endregion


        private void ShowOptions()
        {
            lstOptions.Visibility = Visibility.Visible;
        }

        private void HideOptions()
        {
            lstOptions.Visibility = Visibility.Collapsed;
            this.richTextBox1.Focus();
        }

        public Action<string> ShowStatusMessage { get; set; }

    }
}
