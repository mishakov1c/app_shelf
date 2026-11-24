using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Drawing;
using AppShelf;

namespace AppShelf.Sample
{
    internal static class NotesPlugin
    {
        private const string Id = "example.notes";
        private const string Version = "1.0.0";
        private const int MaxText = 16000;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static Control dispatcher;
        private static Form notesWindow, settingsWindow;
        private static string dataDirectory;
        private static bool notify, backgroundStarted;
        private static PluginNotice pending;
        private static int exitCode;

        [STAThread]
        private static int Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (dispatcher = new Control())
            {
                // Create the handle on the STA before the protocol worker starts.
                IntPtr handle = dispatcher.Handle;
                Thread worker = new Thread(RunProtocol);
                worker.IsBackground = true;
                worker.Start();
                Application.Run();
                // The normal exit is requested only after the final frame is flushed.
                worker.Join(1000);
            }
            return exitCode;
        }

        private static void RunProtocol()
        {
            try
            {
                using (Stream input = Console.OpenStandardInput())
                using (Stream output = Console.OpenStandardOutput())
                {
                    bool ready = false;
                    int previousId = 0;
                    while (true)
                    {
                        PluginRequest request = PluginFrame.Read<PluginRequest>(input);
                        if (request.ApiVersion != 1 || request.Id <= previousId ||
                            (!ready && request.Method != "hello") || (ready && request.Method == "hello"))
                            throw new InvalidDataException("Invalid request sequence.");
                        previousId = request.Id;
                        PluginResponse reply = new PluginResponse { ApiVersion = 1, Id = request.Id, Ok = true };
                        try
                        {
                            dispatcher.Invoke((MethodInvoker)delegate { Handle(request, reply); });
                        }
                        catch (Exception)
                        {
                            reply = new PluginResponse { ApiVersion = 1, Id = request.Id, Ok = false, Error = "Unable to perform the requested operation." };
                        }
                        PluginFrame.Write(output, reply);
                        if (request.Method == "hello")
                        {
                            if (!reply.Ok) break;
                            ready = true;
                        }
                        if (request.Method == "shutdown") break;
                    }
                }
            }
            catch (EndOfStreamException) { }
            catch (Exception) { exitCode = 1; }
            finally
            {
                try { dispatcher.BeginInvoke((MethodInvoker)delegate { Application.ExitThread(); }); }
                catch (InvalidOperationException) { }
            }
        }

        // All mutable state and windows belong to the same STA.
        private static void Handle(PluginRequest request, PluginResponse reply)
        {
            switch (request.Method)
            {
                case "hello":
                    if (request.PluginId != Id || request.PluginVersion != Version ||
                        String.IsNullOrEmpty(request.DataDirectory) || !Path.IsPathRooted(request.DataDirectory))
                        throw new InvalidDataException();
                    dataDirectory = request.DataDirectory;
                    Directory.CreateDirectory(dataDirectory);
                    notify = ReadSmall("settings.txt", 16) == "on";
                    reply.PluginId = Id;
                    reply.PluginVersion = Version;
                    reply.Commands = new[] { new PluginCommand { Id = "open", Label = "Заметки" } };
                    break;
                case "invoke":
                    if (request.CommandId != "open") throw new InvalidOperationException();
                    OpenNotes();
                    break;
                case "settings": OpenSettings(); break;
                case "startBackground":
                    if (backgroundStarted) throw new InvalidOperationException();
                    backgroundStarted = true;
                    break;
                case "poll":
                    if (pending != null)
                    {
                        reply.Notifications = new[] { pending };
                        pending = null;
                    }
                    break;
                case "ping":
                case "shutdown":
                    break;
                default: throw new InvalidOperationException();
            }
        }

        private static string ReadSmall(string name, int maxBytes)
        {
            string path = Path.Combine(dataDirectory, name);
            if (!File.Exists(path)) return "";
            using (FileStream stream = File.OpenRead(path))
            {
                if (stream.Length > maxBytes) throw new InvalidDataException("Data is too large.");
                using (StreamReader reader = new StreamReader(stream, Utf8, false))
                    return reader.ReadToEnd();
            }
        }

        private static void Save(string name, string value)
        {
            string path = Path.Combine(dataDirectory, name);
            string temporary = Path.Combine(dataDirectory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                byte[] bytes = Utf8.GetBytes(value);
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static void ShowExisting(Form window)
        {
            if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
            window.Show();
            window.Activate();
        }

        private static void OpenNotes()
        {
            if (notesWindow != null && !notesWindow.IsDisposed) { ShowExisting(notesWindow); return; }
            string saved = ReadSmall("notes.txt", MaxText * 4);
            if (saved.Length > MaxText) throw new InvalidDataException("Note is too large.");
            Form form = new Form { Text = "AppShelf — Заметки (пример плагина)", Size = new Size(560, 380), StartPosition = FormStartPosition.CenterScreen };
            TextBox editor = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, MaxLength = MaxText, Text = saved, AcceptsReturn = true, Font = new Font("Segoe UI", 11) };
            Button save = new Button { Text = "Сохранить", Dock = DockStyle.Bottom, Height = 34 };
            Label status = new Label { Text = "Сохраняйте изменения кнопкой.", Dock = DockStyle.Bottom, Height = 26 };
            form.Controls.Add(editor);
            form.Controls.Add(status);
            form.Controls.Add(save);
            save.Click += delegate
            {
                try
                {
                    if (editor.Text.Length > MaxText) throw new InvalidDataException();
                    Save("notes.txt", editor.Text);
                    saved = editor.Text;
                    status.Text = "Сохранено";
                    if (notify) pending = new PluginNotice { Title = "Заметки", Message = "Заметка сохранена." };
                }
                catch (Exception) { status.Text = "Не удалось сохранить заметку."; }
            };
            editor.TextChanged += delegate { status.Text = editor.Text == saved ? "Сохранено" : "Есть несохранённые изменения"; };
            // Ordinary close hides the window, preserving unsaved edits during this session.
            form.FormClosing += delegate(object sender, FormClosingEventArgs args)
            {
                if (args.CloseReason == CloseReason.UserClosing) { args.Cancel = true; form.Hide(); }
            };
            notesWindow = form;
            form.Show();
        }

        private static void OpenSettings()
        {
            if (settingsWindow != null && !settingsWindow.IsDisposed) { ShowExisting(settingsWindow); return; }
            Form form = new Form { Text = "Заметки — настройки", ClientSize = new Size(410, 140),
                Padding = new Padding(12), MinimumSize = new Size(380, 170), StartPosition = FormStartPosition.CenterScreen };
            CheckBox checkbox = new CheckBox { Text = "Уведомлять о сохранении заметки", Checked = notify, Dock = DockStyle.Top, Height = 42 };
            Button save = new Button { Text = "Сохранить", Dock = DockStyle.Bottom, Height = 34 };
            Label status = new Label { Dock = DockStyle.Fill };
            form.Controls.Add(status);
            form.Controls.Add(checkbox);
            form.Controls.Add(save);
            save.Click += delegate
            {
                try
                {
                    Save("settings.txt", checkbox.Checked ? "on" : "off");
                    notify = checkbox.Checked;
                    if (!notify) pending = null;
                    form.Close();
                }
                catch (Exception) { status.Text = "Не удалось сохранить настройки."; }
            };
            settingsWindow = form;
            form.Show();
        }
    }
}
