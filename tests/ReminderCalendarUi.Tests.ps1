# Windows PowerShell 5.1 -STA. Real WPF windows/rendering, isolated storage, no startup monitors.
param([switch]$Baseline)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use Windows PowerShell 5.1 -STA.' }
$root = Split-Path $PSScriptRoot -Parent
$temp = Join-Path $env:LOCALAPPDATA ('Temp\opencode\AppShelf-ReminderCalendar-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temp)
$old = $env:APPSHELF_TEST_DATA
$env:APPSHELF_TEST_DATA = $temp
$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
try {
    $sources = @()
    foreach ($file in @('AppShelf.cs','AppShelfExtras.cs','FluentUi.cs','Reminders.cs','WeatherProviders.cs','BackupManager.cs','SiteLinks.cs','WindowsIntegration.cs','Collections.cs','CollectionsWindow.cs','AppUpdate.cs','AppUpdateWindow.cs','PluginPackages.cs','PluginManagerWindow.cs')) {
        if ($Baseline -and $file -in @('PluginPackages.cs','PluginManagerWindow.cs')) { continue }
    if ($Baseline -and $file -in @('AppUpdate.cs','AppUpdateWindow.cs')) { continue }
        $text = [IO.File]::ReadAllText((Join-Path $root $file))
        if ($Baseline -and $file -in @('Collections.cs','CollectionsWindow.cs')) { continue }
        if ($Baseline -and $file -in @('FluentUi.cs','Reminders.cs','BackupManager.cs')) {
            $git = New-Object Diagnostics.Process
            $git.StartInfo.FileName = 'git'
            $git.StartInfo.Arguments = "show cf2b23de7f87f1543c1d5617a17cc7e552f5e62b:$file"
            $git.StartInfo.WorkingDirectory = $root
            $git.StartInfo.UseShellExecute = $false
            $git.StartInfo.RedirectStandardOutput = $true
            $git.StartInfo.RedirectStandardError = $true
            $git.StartInfo.StandardOutputEncoding = [Text.Encoding]::UTF8
            try {
                [void]$git.Start(); $text = $git.StandardOutput.ReadToEnd(); [void]$git.StandardError.ReadToEnd(); $git.WaitForExit()
                if ($git.ExitCode -ne 0) { throw 'Cannot read baseline.' }
            } finally { $git.Dispose() }
        }
        if ($file -eq 'AppShelf.cs') {
            $anchor = 'Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppShelf"'
            if ($text.Split(@($anchor), [StringSplitOptions]::None).Length -ne 2) { throw 'Isolation anchor missing.' }
            $text = $text.Replace($anchor, 'Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "AppShelf"')
        }
        if ($file -eq 'Reminders.cs') {
            $text = $text.Replace('DateTime.Now', 'ReminderUiTests.Now')
            $text = $text.Replace('            dialog.ShowDialog();', '            ReminderUiTests.Drive(dialog);')
            $pattern = 'MessageBox.Show\(this, "[^"\r\n]*\\n" \+ error.Message,\s*"AppShelf", MessageBoxButton.OK, MessageBoxImage.Error\);'
            if ([regex]::Matches($text,$pattern).Count -ne 2) { throw 'Save error anchors missing.' }
            $text = [regex]::Replace($text,$pattern,'ReminderUiTests.SaveError();')
            $text = [regex]::Replace($text, 'MessageBox.Show\(dialog, "[^"\r\n]*",\s*"AppShelf", MessageBoxButton.OK, MessageBoxImage.Warning\);', 'ReminderUiTests.ValidationError();')
        }
        $text = $text.Replace('WindowsStartup.Apply(enable, hidden);', '').Replace('WindowsStartup.WriteRaw(previousRunValue);', '')
        $text = $text.Replace('System.Media.SystemSounds.Asterisk.Play();', '').Replace('System.Media.SystemSounds.Exclamation.Play();', '')
        $sources += $text.Replace('SiteLinks.FetchFavicon(site)', 'false') # No live websites in legacy tests.
    }
    $sources += @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AppShelf;
using Calendar = System.Windows.Controls.Calendar;

public static class ReminderUiTests
{
    public static DateTime Now = new DateTime(2030,1,31,18,0,0);
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int passed, failed, errors, validation;
    private static Action<Window> editor;
    private static bool loggedDpi;
    public static void SaveError() { errors++; }
    public static void ValidationError() { validation++; }
    private static void Assert(bool value,string message) { if (!value) throw new Exception(message); }
    private static object Get(MainWindow w,string name) { return typeof(MainWindow).GetField(name,Private).GetValue(w); }
    private static void Set(MainWindow w,string name,object value) { typeof(MainWindow).GetField(name,Private).SetValue(w,value); }
    private static object Call(MainWindow w,string name,params object[] args)
    {
        try { return typeof(MainWindow).GetMethod(name,Private).Invoke(w,args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static List<T> Controls<T>(DependencyObject root) where T : DependencyObject
    {
        List<T> result = new List<T>(); if (root is T) result.Add((T)root);
        foreach (object child in LogicalTreeHelper.GetChildren(root)) if (child is DependencyObject) result.AddRange(Controls<T>((DependencyObject)child));
        return result;
    }
    private static T Visual<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T) return (T)root;
        for (int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) { T found=Visual<T>(VisualTreeHelper.GetChild(root,i)); if(found!=null)return found; }
        return null;
    }
    private static Button Button(Window w,string text)
    {
        foreach(Button b in Controls<Button>(w)) if(Convert.ToString(b.Content)==text)return b;
        throw new Exception("Button missing: "+text);
    }
    private static void Click(Button b) { b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Drain(); }
    private static void Drain() { Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle,new Action(delegate{})); }
    public static void Drive(Window dialog)
    {
        dialog.ShowInTaskbar=false; dialog.ShowActivated=false; dialog.Left=30; dialog.Top=30;
        dialog.Show(); Drain();
        if(!loggedDpi){PresentationSource source=PresentationSource.FromVisual(dialog);if(source!=null&&source.CompositionTarget!=null){Console.WriteLine("Native WPF device scale="+source.CompositionTarget.TransformToDevice.M11.ToString(CultureInfo.InvariantCulture));loggedDpi=true;}}
        try { editor(dialog); }
        finally { dialog.Close(); Drain(); }
    }
    private static MainWindow Open(bool clear = true)
    {
        ConfigStore.Save(new AppConfig { ShowWeather=false,ShowNetwork=false,MinimizeToTray=false });
        if (clear) ReminderStore.Save(new ReminderDocument());
        MainWindow w=new MainWindow();
        w.SourceInitialized -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler),w,typeof(MainWindow).GetMethod("OnWindowSourceInitialized",Private));
        new WindowInteropHelper(w).EnsureHandle();
        System.Windows.Forms.NotifyIcon tray=(System.Windows.Forms.NotifyIcon)Get(w,"_trayIcon"); if(tray!=null){tray.Dispose();Set(w,"_trayIcon",null);}
        return w;
    }
    private static void Edit(MainWindow w,ReminderItem item,Action<Window> action) { editor=action;Call(w,"EditReminder",new object[]{item}); }
    private static TextBox Title(Window w) { return Controls<TextBox>(w)[0]; }
    private sealed class ClockInput
    {
        public readonly List<TextBox> Fields = new List<TextBox>();
        public ClockInput(Window w)
        {
            foreach(TextBox input in Controls<TextBox>(w)) if(input.Name=="ReminderHours"||input.Name=="ReminderMinutes")Fields.Add(input);
            if(Fields.Count==0)Fields.Add(Controls<TextBox>(w)[1]); // Historical baseline.
        }
        public string Text
        {
            get { return Fields.Count==1?Fields[0].Text:Fields[1].Text.Length==0?Fields[0].Text:Fields[0].Text+":"+Fields[1].Text; }
            set { if(Fields.Count==1){Fields[0].Text=value;return;}string[] parts=value.Split(':');Fields[0].Text=parts[0];Fields[1].Text=parts.Length==2?parts[1]:""; }
        }
    }
    private static ClockInput Time(Window w) { return new ClockInput(w); }
    private static T Named<T>(Window w,string name) where T : FrameworkElement
    {
        foreach(T item in Controls<T>(w))if(item.Name==name)return item;
        throw new Exception("Missing control: "+name);
    }
    private static void ArrowKey(TextBox field,Key key)
    {
        KeyEventArgs args=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(field),0,key){RoutedEvent=Keyboard.PreviewKeyDownEvent};
        field.RaiseEvent(args);Assert(args.Handled,"Spinner did not handle arrow key");Drain();
    }
    private static void Type(TextBox field,Window dialog,string text)
    {
        dialog.Activate();field.Focus();Keyboard.Focus(field);field.SelectAll();
        TextCompositionManager.StartComposition(new TextComposition(InputManager.Current,field,text));Drain();
    }
    private static bool Paste(TextBox field,string text)
    {
        field.SelectAll();DataObject data=new DataObject(DataFormats.UnicodeText,text);
        DataObjectPastingEventArgs args=new DataObjectPastingEventArgs(data,false,DataFormats.UnicodeText){RoutedEvent=DataObject.PastingEvent};
        field.RaiseEvent(args);if(!args.CommandCancelled)field.SelectedText=text;Drain();return !args.CommandCancelled;
    }
    private static void Fill(Window w,string mode,DateTime due)
    {
        Title(w).Text="Название — встреча"; Controls<DatePicker>(w)[0].SelectedDate=due.Date; Time(w).Text=due.ToString("HH:mm",CultureInfo.InvariantCulture);
        ComboBox combo=Controls<ComboBox>(w)[0];
        foreach(ComboBoxItem option in combo.Items)if((string)option.Tag==mode){combo.SelectedItem=option;return;}
        throw new Exception("Repeat option missing: "+mode);
    }
    private static ReminderItem Item(string mode,DateTime due) { return new ReminderItem { Id=Guid.NewGuid().ToString("N"),Title="Test",Repeat=mode,DueLocal=ReminderStore.ToValue(due),Active=true }; }
    private static ReminderItem Next(ReminderItem item,DateTime now)
    {
        MethodInfo method=typeof(ReminderStore).GetMethod("AfterOccurrence",BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public);
        Assert(method!=null,"Calendar recurrence helper is absent");
        try{return (ReminderItem)method.Invoke(null,new object[]{item,now});}catch(TargetInvocationException ex){throw ex.InnerException;}
    }
    private static void Due(ReminderItem item,DateTime expected) { Assert(item.DueLocal==ReminderStore.ToValue(expected),"Expected "+expected+", got "+item.DueLocal); }
    private static object Anchor(ReminderItem item,string name) { FieldInfo f=typeof(ReminderItem).GetField(name);return f==null?null:f.GetValue(item); }
    private static void SetAnchor(ReminderItem item,string name,int value) { FieldInfo f=typeof(ReminderItem).GetField(name);Assert(f!=null,"Anchor field missing");f.SetValue(item,value); }
    private static ReminderDocument Load() { string warning;bool safe;ReminderDocument d=ReminderStore.Load(out warning,out safe);Assert(safe&&warning==null,"Reminder load rejected: "+warning);return d; }
    private static void Picture(FrameworkElement view,string name)
    {
        view.UpdateLayout();
        RenderTargetBitmap bmp=new RenderTargetBitmap((int)Math.Ceiling(view.RenderSize.Width),(int)Math.Ceiling(view.RenderSize.Height),96,96,PixelFormats.Pbgra32);
        DrawingVisual drawing=new DrawingVisual();using(DrawingContext context=drawing.RenderOpen()){
            VisualBrush brush=new VisualBrush(view){ViewboxUnits=BrushMappingMode.Absolute,Viewbox=new Rect(view.RenderSize)};
            context.DrawRectangle(brush,null,new Rect(view.RenderSize));
        }
        bmp.Render(drawing);PngBitmapEncoder encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bmp));
        using(FileStream stream=File.Create(Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"),name+".png")))encoder.Save(stream);
    }
    private static void Layout(double scale)
    {
        MainWindow w=Open();try{Edit(w,null,delegate(Window d){
            ((FrameworkElement)d.Content).LayoutTransform=new ScaleTransform(scale,scale);Drain();
            Title(d).Text="Видимый текст";
            Picture(d,"editor-"+scale.ToString(CultureInfo.InvariantCulture));
            TextBox t=Title(d);ScrollViewer host=(ScrollViewer)t.Template.FindName("PART_ContentHost",t);
            Rect caret=t.GetRectFromCharacterIndex(0);Rect viewport=host.TransformToAncestor(t).TransformBounds(new Rect(host.RenderSize));
            Assert(!caret.IsEmpty&&caret.Height>10&&caret.Top>=viewport.Top-1&&caret.Bottom<=viewport.Bottom+1,"Text clipped: caret="+caret+" host="+viewport);
            Grid clock=Named<Grid>(d,"ReminderTime");Rect clockBounds=clock.TransformToAncestor(d).TransformBounds(new Rect(clock.RenderSize));
            foreach(TextBox input in Time(d).Fields){Rect r=input.TransformToAncestor(d).TransformBounds(new Rect(input.RenderSize));Assert(r.Left>=clockBounds.Left-0.5&&r.Right<=clockBounds.Right+0.5&&r.Bottom<=clockBounds.Bottom+0.5,"Time field clipped at scale "+scale);}
            foreach(string name in new string[]{"ReminderHoursUp","ReminderHoursDown","ReminderMinutesUp","ReminderMinutesDown"}){Button arrow=Named<Button>(d,name);Rect r=arrow.TransformToAncestor(d).TransformBounds(new Rect(arrow.RenderSize));Assert(arrow.IsVisible&&arrow.ActualHeight>=16&&r.Right<=clockBounds.Right+0.5&&r.Bottom<=clockBounds.Bottom+0.5,"Spinner arrow clipped at scale "+scale);}
            foreach(string text in new string[]{"Сохранить","Отмена"}){
                Button b=Button(d,text);Rect r=b.TransformToAncestor(d).TransformBounds(new Rect(b.RenderSize));
                FrameworkElement client=(FrameworkElement)d.Content;Rect bounds=client.TransformToAncestor(d).TransformBounds(new Rect(client.RenderSize));
                Assert(b.IsVisible&&b.ActualHeight>=30&&r.Bottom<=bounds.Bottom+0.5&&r.Right<=bounds.Right+0.5,"Button clipped at content scale "+scale+": "+r);
            }
        });}finally{w.Close();}
    }
    private static void CalendarSequence(int day,bool leap)
    {
        int year=leap?2028:2027;ReminderItem item=Item("monthly",new DateTime(year,1,day,19,0,0));
        item=Next(item,new DateTime(year,1,day,19,0,0));Due(item,new DateTime(year,2,Math.Min(day,DateTime.DaysInMonth(year,2)),19,0,0));
        item=Next(item,DateTime.ParseExact(item.DueLocal,"yyyy-MM-dd'T'HH:mm:ss",CultureInfo.InvariantCulture));Due(item,new DateTime(year,3,day,19,0,0));
        item=Next(item,new DateTime(year,3,day,19,0,0));Due(item,new DateTime(year,4,Math.Min(day,30),19,0,0));
        Assert(Convert.ToInt32(Anchor(item,"RepeatDay"))==day,"Original month day drifted");
    }
    private static void Case(string name,Action action)
    {
        try{action();passed++;Console.WriteLine("PASS "+name);}catch(Exception ex){failed++;Console.WriteLine("FAIL "+name+": "+ex.Message);}
    }
    private static ReminderDocument Live(MainWindow w) { return (ReminderDocument)Get(w,"_reminderDocument"); }
    private static string Snapshot(ReminderDocument doc)
    {
        using(MemoryStream stream=new MemoryStream()){new DataContractJsonSerializer(typeof(ReminderDocument)).WriteObject(stream,doc);return Convert.ToBase64String(stream.ToArray());}
    }
    private static bool InvalidVisible(Window d)
    {
        foreach(TextBlock text in Controls<TextBlock>(d))if(text.Name=="ReminderValidation")return text.IsVisible&&!String.IsNullOrWhiteSpace(text.Text);
        return validation>0;
    }
    private static void CreateEdit(string mode)
    {
        MainWindow w=Open();try{
            Edit(w,null,delegate(Window d){Fill(d,mode,new DateTime(2030,3,31,19,23,0));Click(Button(d,"Сохранить"));Assert(!d.IsVisible,"Valid save kept editor open");});
            Assert(Live(w).Reminders.Count==1,"Create produced wrong count");ReminderItem original=Live(w).Reminders[0];Due(original,new DateTime(2030,3,31,19,23,0));Assert(original.Repeat==mode&&original.Title.Contains("встреча"),"Create lost values");
            string id=original.Id;string editedMode=mode=="yearly"?"once":"yearly";
            Edit(w,original,delegate(Window d){Assert(Time(d).Text=="19:23"&&Title(d).Text==original.Title,"Editor did not restore title/time");Fill(d,editedMode,new DateTime(2030,4,30,9,5,0));Title(d).Text="Другое название";Click(Button(d,"Сохранить"));});
            Assert(Object.ReferenceEquals(original,Live(w).Reminders[0])&&original.Id==id&&original.Title=="Другое название"&&original.Repeat==editedMode,"Edit replaced identity or lost parameters");Due(original,new DateTime(2030,4,30,9,5,0));Assert(Snapshot(Load())==Snapshot(Live(w)),"Edit disk/live mismatch");
        }finally{w.Close();}
    }
    private static void WriteFailure(string mode,bool editing,bool cancel)
    {
        MainWindow w=Open();try{
            ReminderItem original=Item(mode,new DateTime(2030,2,28,12,0,0));SetAnchor(original,"RepeatDay",mode=="monthly"?31:29);if(mode=="yearly")SetAnchor(original,"RepeatMonth",2);
            Live(w).Reminders.Add(original);ReminderStore.Save(Live(w));string before=Snapshot(Live(w)),disk=File.ReadAllText(ReminderStore.FilePath),bak=File.ReadAllText(ReminderStore.FilePath+".bak");
            Edit(w,editing?original:null,delegate(Window d){Fill(d,mode,new DateTime(2030,4,30,20,17,0));
                using(FileStream locked=new FileStream(ReminderStore.FilePath,FileMode.Open,FileAccess.Read,FileShare.Read)){
                    int count=errors;Click(Button(d,"Сохранить"));Click(Button(d,"Сохранить"));Assert(errors==count+2&&d.IsVisible,"Write failure did not keep editor usable");Assert(Snapshot(Live(w))==before&&File.ReadAllText(ReminderStore.FilePath)==disk&&File.ReadAllText(ReminderStore.FilePath+".bak")==bak,"Failed edit changed live data or .bak");Assert(Title(d).Text.Contains("встреча")&&Time(d).Text=="20:17","Failed save lost entered values");
                }
                Click(Button(d,cancel?"Отмена":"Сохранить"));
            });
            if(cancel){Assert(Snapshot(Live(w))==before,"Cancel retained changes");ReminderStore.Save(Live(w));Assert(Snapshot(Load())==before,"A later save persisted cancelled values");}
            else{Assert(Live(w).Reminders.Count==(editing?1:2),"Retry created duplicates");Assert(Snapshot(Live(w))==Snapshot(Load()),"Retry disk/live mismatch");}
        }finally{w.Close();}
    }
    private static void Deliver(string mode,bool fail)
    {
        MainWindow w=Open();DateTime savedNow=Now;try{
            ReminderItem item=Item(mode,mode=="monthly"?new DateTime(2028,1,31,19,23,0):new DateTime(2028,2,29,12,0,0));
            Live(w).Reminders.Add(item);ReminderStore.Save(Live(w));string before=Snapshot(Live(w));Set(w,"_reminderAlertWindow",new Window());
            Now=new DateTime(2030,4,15,12,0,0);
            if(fail){using(FileStream locked=new FileStream(ReminderStore.FilePath,FileMode.Open,FileAccess.Read,FileShare.Read)){
                Call(w,"CheckDueReminders");Call(w,"CheckDueReminders");Assert(Snapshot(Live(w))==before&&((List<string>)Get(w,"_pendingReminderNames")).Count==0,"Failed due write changed anchors or queued notifications");
            }}
            Call(w,"CheckDueReminders");Call(w,"CheckDueReminders");
            Assert(((List<string>)Get(w,"_pendingReminderNames")).Count==1,"Missed periods generated duplicates");Assert(Live(w).Reminders.Count==2&&Live(w).Reminders[1].ParentId==item.Id&&!Live(w).Reminders[1].Active,"Recurring occurrence history missing");
            Due(item,mode=="monthly"?new DateTime(2030,4,30,19,23,0):new DateTime(2031,2,28,12,0,0));Assert(Convert.ToInt32(Anchor(item,"RepeatDay"))==(mode=="monthly"?31:29),"Delivery lost calendar anchor");Assert(Snapshot(Live(w))==Snapshot(Load()),"Delivery disk/live mismatch");
            w.Close();w=Open(false);Set(w,"_reminderAlertWindow",new Window());Call(w,"CheckDueReminders");Assert(((List<string>)Get(w,"_pendingReminderNames")).Count==0,"Restart duplicated delivered notification");
        }finally{w.Close();Now=savedNow;}
    }
    public static void Run()
    {
        Assert(ConfigStore.DirectoryPath==Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"),"AppShelf"),"Isolation failed");
        Application app=new Application();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;FluentUi.Install(app);
        Console.WriteLine("WPF runtime="+Environment.Version+"; real HWNDs and RenderTargetBitmap; scaling tests are content-layout simulations, not OS DPI changes.");
        foreach(double s in new double[]{1,1.25,1.5}){double scale=s;Case("rendered editor layout scale "+s,delegate{Layout(scale);});}
        Case("reminder editor uses dark compact vertical scrollbar",delegate{
            MainWindow w=Open();try{Edit(w,null,delegate(Window d){
                d.Width=d.MinWidth;d.Height=d.MinHeight;d.UpdateLayout();Drain();
                List<ScrollViewer> viewers=Controls<ScrollViewer>(d);
                Assert(viewers.Count==1,"Reminder editor scroll viewer missing");
                ScrollViewer scroll=viewers[0];scroll.ApplyTemplate();scroll.UpdateLayout();Drain();
                ScrollBar bar=scroll.Template.FindName("PART_VerticalScrollBar",scroll) as ScrollBar;
                Assert(bar!=null,"Reminder editor vertical scrollbar missing");
                Track track=bar.Template.FindName("PART_Track",bar) as Track;
                Assert(track!=null&&track.Orientation==Orientation.Vertical,
                    "Reminder editor dark scrollbar track is not vertical");
                Border surface=Visual<Border>(bar);
                SolidColorBrush brush=surface==null?null:surface.Background as SolidColorBrush;
                Assert(brush!=null&&brush.Color.R<100&&brush.Color.G<100&&brush.Color.B<120,
                    "Reminder editor still uses default light scrollbar");
                Assert(bar.Width<=12.5,"Reminder editor scrollbar unexpectedly wide");
                Assert(scroll.ScrollableHeight>0,"Reminder editor did not overflow at minimum size");
                scroll.ScrollToEnd();Drain();
                Assert(scroll.VerticalOffset>0,"Reminder editor cannot scroll");
                scroll.ScrollToHome();Drain();
                scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,-120)
                    {RoutedEvent=Mouse.MouseWheelEvent});Drain();
                Assert(scroll.VerticalOffset>0,"Reminder editor mouse wheel did not scroll");
                scroll.ScrollToHome();Drain();
                track.Thumb.RaiseEvent(new DragDeltaEventArgs(0,25){RoutedEvent=Thumb.DragDeltaEvent});Drain();
                Assert(scroll.VerticalOffset>0,"Reminder editor thumb did not scroll");
            });}finally{w.Close();}
        });
        Case("initial time is populated and fits viewport",delegate{
            MainWindow w=Open();try{Edit(w,null,delegate(Window d){Assert(Time(d).Text=="18:30","Initial HH:mm missing");foreach(TextBox t in Time(d).Fields){Rect caret=t.GetRectFromCharacterIndex(0);ScrollViewer host=(ScrollViewer)t.Template.FindName("PART_ContentHost",t);Rect bounds=host.TransformToAncestor(t).TransformBounds(new Rect(host.RenderSize));Assert(!caret.IsEmpty&&caret.Bottom<=bounds.Bottom+1,"Initial time is clipped despite nonempty Text");}});}finally{w.Close();}
        });
        Case("title supports input, selection, replacement and deletion",delegate{
            MainWindow w=Open();try{Edit(w,null,delegate(Window d){TextBox t=Title(d);d.Activate();t.Focus();Keyboard.Focus(t);TextCompositionManager.StartComposition(new TextComposition(InputManager.Current,t,"Name Название"));Assert(t.Text=="Name Название","Text input did not reach editor");t.SelectAll();t.SelectedText="Edited Изменено";Assert(t.Text=="Edited Изменено","Replacement failed");ApplicationCommands.SelectAll.Execute(null,t);EditingCommands.Delete.Execute(null,t);Assert(t.Text.Length==0,"Delete failed");});}finally{w.Close();}
        });
        Case("dark date calendar and repeat popup",delegate{
            MainWindow w=Open();try{Edit(w,null,delegate(Window d){DatePicker date=Controls<DatePicker>(d)[0];ComboBox combo=Controls<ComboBox>(d)[0];Assert(date.Background.ToString()==FluentUi.Surface.ToString()&&combo.Background.ToString()==FluentUi.Surface.ToString(),"Light input chrome remains");date.IsDropDownOpen=true;Drain();Popup popup=(Popup)date.Template.FindName("PART_Popup",date);Calendar calendar=Visual<Calendar>(popup.Child);Assert(calendar!=null&&calendar.Background.ToString()==FluentUi.Surface.ToString(),"Calendar is not dark");Picture(calendar,"calendar");date.IsDropDownOpen=false;combo.IsDropDownOpen=true;Drain();Popup list=(Popup)combo.Template.FindName("PART_Popup",combo);Assert(list!=null&&list.Child!=null,"Repeat popup missing");Picture((FrameworkElement)list.Child,"repeat-popup");
                HashSet<string> repeatKeys = new HashSet<string>(new string[] {
                    "once", "hourly", "daily", "weekly", "monthly", "yearly",
                    "weekdays", "weekends", "custom_weekdays" }, StringComparer.Ordinal);
                Assert(combo.Items.Count == repeatKeys.Count, "Nine repeat choices missing");
                foreach (object value in combo.Items)
                {
                    ComboBoxItem option = value as ComboBoxItem;
                    Assert(option != null && repeatKeys.Remove(Convert.ToString(option.Tag)),
                        "Unexpected or duplicate repeat choice tag");
                }
                Assert(repeatKeys.Count == 0, "Expected repeat choice tags missing");
                combo.IsDropDownOpen=false;});}finally{w.Close();}
        });
        foreach(int day in new int[]{28,29,30,31})foreach(bool leap in new bool[]{false,true}){int n=day;bool l=leap;Case("monthly day="+day+" leap="+leap,delegate{CalendarSequence(n,l);});}
        Case("yearly leap day returns after ordinary years",delegate{
            ReminderItem item=Item("yearly",new DateTime(2028,2,29,12,0,0));for(int year=2029;year<=2032;year++){item=Next(item,new DateTime(year-1,3,1));Due(item,new DateTime(year,2,DateTime.IsLeapYear(year)?29:28,12,0,0));}Assert(Convert.ToInt32(Anchor(item,"RepeatDay"))==29&&Convert.ToInt32(Anchor(item,"RepeatMonth"))==2,"Leap anchor drifted");
        });
        foreach(string mode in new string[]{"daily","weekly","monthly","yearly"}){string m=mode;Case("skip missed "+m+" periods",delegate{DateTime start=new DateTime(2020,1,31,19,23,0),now=new DateTime(2030,6,17,15,0,0);ReminderItem item=Next(Item(m,start),now);DateTime due;Assert(ReminderStore.TryParse(item.DueLocal,out due)&&due>now&&due.TimeOfDay==start.TimeOfDay,"Missed periods lost time or remain overdue");if(m=="monthly")Due(item,new DateTime(2030,6,30,19,23,0));if(m=="yearly")Due(item,new DateTime(2031,1,31,19,23,0));});}
        Case("old JSON remains readable",delegate{Directory.CreateDirectory(ConfigStore.DirectoryPath);File.WriteAllText(ReminderStore.FilePath,"{\"version\":1,\"reminders\":[{\"id\":\"legacy\",\"title\":\"Old\",\"dueLocal\":\"2030-02-01T12:00:00\",\"repeat\":\"weekly\",\"active\":true}]}");Assert(Load().Reminders[0].Repeat=="weekly","Legacy JSON rejected");});
        Case("invalid typed date remains visible and cannot save",delegate{
            MainWindow w=Open();try{Edit(w,null,delegate(Window d){Fill(d,"once",new DateTime(2030,2,5,19,0,0));DatePicker day=Controls<DatePicker>(d)[0];day.Text="31.02.2030";Drain();Click(Button(d,"Сохранить"));Assert(d.IsVisible&&((ReminderDocument)Get(w,"_reminderDocument")).Reminders.Count==0,"Invalid date saved the previous calendar value");Assert(day.Text.Contains("31.02.2030"),"Invalid date text was discarded");});}finally{w.Close();}
        });
        foreach(string mode in new string[]{"monthly","yearly"}){string m=mode;Case("calendar JSON and ZIP roundtrip "+m,delegate{
            ReminderDocument doc=new ReminderDocument();ReminderItem item=Item(m,new DateTime(2028,2,29,12,0,0));SetAnchor(item,"RepeatDay",29);if(m=="yearly")SetAnchor(item,"RepeatMonth",2);doc.Reminders.Add(item);ReminderStore.Save(doc);Assert(Load().Reminders[0].Repeat==m,"New JSON rejected");string json=Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"),m+".json");BackupStore.ExportReminders(json,doc);ReminderDocument imported=BackupStore.ImportReminders(json);Assert(imported.Reminders[0].Repeat==m&&Convert.ToInt32(Anchor(imported.Reminders[0],"RepeatDay"))==29,"JSON import lost anchor");ConfigStore.Save(new AppConfig{ShowWeather=false,ShowNetwork=false});string zip=json+".zip";BackupStore.ExportAll(zip,new AppConfig(),doc);BackupStore.RestoreAll(zip);Assert(Load().Reminders[0].Repeat==m,"ZIP restore rejected recurrence");
        });}
        foreach(string mode in new string[]{"once","daily","weekly","monthly","yearly"}){string m=mode;Case("create and edit all parameters "+m,delegate{CreateEdit(m);});}
        foreach(string mode in new string[]{"monthly","yearly"}){string m=mode;
            Case("create write failure and retry "+m,delegate{WriteFailure(m,false,false);});
            Case("edit write failure and retry "+m,delegate{WriteFailure(m,true,false);});
            Case("cancel failed edit and later save "+m,delegate{WriteFailure(m,true,true);});
            Case("missed delivery without duplicates "+m,delegate{Deliver(m,false);});
            Case("missed delivery write failure and restart "+m,delegate{Deliver(m,true);});
            Case("edit clamped occurrence keeps anchor, date change resets it "+m,delegate{
                MainWindow w=Open();try{ReminderItem item=Item(m,new DateTime(2030,2,28,12,0,0));SetAnchor(item,"RepeatDay",m=="monthly"?31:29);if(m=="yearly")SetAnchor(item,"RepeatMonth",2);Live(w).Reminders.Add(item);ReminderStore.Save(Live(w));
                    Edit(w,item,delegate(Window d){Title(d).Text="Только имя и время";Time(d).Text="13:17";Click(Button(d,"Сохранить"));});
                    Assert(Convert.ToInt32(Anchor(item,"RepeatDay"))==(m=="monthly"?31:29),"Editing text reset clamped day");
                    ReminderItem next=Next(item,new DateTime(2030,2,28,14,0,0));Due(next,m=="monthly"?new DateTime(2030,3,31,13,17,0):new DateTime(2031,2,28,13,17,0));
                    Edit(w,item,delegate(Window d){Fill(d,m,new DateTime(2030,5,30,11,0,0));Click(Button(d,"Сохранить"));});Assert(Convert.ToInt32(Anchor(item,"RepeatDay"))==30,"Changed date did not reset day");if(m=="yearly")Assert(Convert.ToInt32(Anchor(item,"RepeatMonth"))==5,"Changed date did not reset month");
                }finally{w.Close();}
            });
        }
        foreach(string value in new string[]{"24:00","12:60","9:555","abc","","99:99"}){string bad=value;Case("invalid time preserves input: "+value,delegate{
            MainWindow w=Open();try{Edit(w,null,delegate(Window d){Fill(d,"once",new DateTime(2030,2,5,19,0,0));Time(d).Text=bad;Click(Button(d,"Сохранить"));Assert(d.IsVisible&&Live(w).Reminders.Count==0&&Time(d).Text==bad&&InvalidVisible(d),"Invalid time was saved, discarded, or lacked explanation");});}finally{w.Close();}
        });}
        foreach(string value in new string[]{"00:00","9:05","09:05","23:59"}){string good=value;Case("valid minute precision: "+value,delegate{
            MainWindow w=Open();try{Edit(w,null,delegate(Window d){Fill(d,"once",new DateTime(2030,2,5,19,0,0));Time(d).Text=good;Click(Button(d,"Сохранить"));Assert(!d.IsVisible,"Valid time rejected");});DateTime time;Assert(DateTime.TryParseExact(good,new string[]{"H:mm","HH:mm"},CultureInfo.InvariantCulture,DateTimeStyles.None,out time),"Bad test time");Due(Live(w).Reminders[0],new DateTime(2030,2,5,time.Hour,time.Minute,0));}finally{w.Close();}
        });}
        foreach(string problem in new string[]{"empty title","long title","no date","past date"}){string p=problem;Case("validation "+p,delegate{
            MainWindow w=Open();try{Edit(w,null,delegate(Window d){Fill(d,"once",new DateTime(2030,2,5,19,0,0));if(p=="empty title")Title(d).Text="  ";if(p=="long title")Title(d).Text=new string('x',201);if(p=="no date")Controls<DatePicker>(d)[0].SelectedDate=null;if(p=="past date")Controls<DatePicker>(d)[0].SelectedDate=Now.Date.AddDays(-1);string text=Title(d).Text;Click(Button(d,"Сохранить"));Assert(d.IsVisible&&Live(w).Reminders.Count==0&&Title(d).Text==text&&InvalidVisible(d),"Validation failed to preserve editor values");});}finally{w.Close();}
        });}
        Case("calendar navigates month, year and decade and updates selected date",delegate{
            MainWindow w=Open();try{Edit(w,null,delegate(Window d){DatePicker picker=Controls<DatePicker>(d)[0];picker.IsDropDownOpen=true;Drain();Popup popup=(Popup)picker.Template.FindName("PART_Popup",picker);Calendar calendar=Visual<Calendar>(popup.Child);CalendarItem item=Visual<CalendarItem>(calendar);
                Click((Button)item.Template.FindName("PART_NextButton",item));Assert(calendar.DisplayDate.Month==2,"Next month button failed");Click((Button)item.Template.FindName("PART_PreviousButton",item));Assert(calendar.DisplayDate.Month==1,"Previous month failed");
                Click((Button)item.Template.FindName("PART_HeaderButton",item));Assert(calendar.DisplayMode==CalendarMode.Year&&((Grid)item.Template.FindName("PART_YearView",item)).IsVisible,"Year view missing");Click((Button)item.Template.FindName("PART_HeaderButton",item));Assert(calendar.DisplayMode==CalendarMode.Decade,"Decade view missing");
                calendar.DisplayMode=CalendarMode.Month;calendar.DisplayDate=new DateTime(2030,2,1);calendar.SelectedDate=new DateTime(2030,2,28);Drain();Assert(picker.SelectedDate==new DateTime(2030,2,28),"Calendar selection did not reach DatePicker");picker.IsDropDownOpen=false;
            });}finally{w.Close();}
        });
        Case("buttons remain inside client at minimum window size",delegate{
            MainWindow w=Open();try{Edit(w,null,delegate(Window d){d.Width=390;d.Height=280;Drain();FrameworkElement root=(FrameworkElement)d.Content;Rect bounds=root.TransformToAncestor(d).TransformBounds(new Rect(root.RenderSize));foreach(string name in new string[]{"Сохранить","Отмена"}){Button b=Button(d,name);Rect r=b.TransformToAncestor(d).TransformBounds(new Rect(b.RenderSize));Assert(b.IsVisible&&r.Bottom<=bounds.Bottom+0.5&&r.Right<=bounds.Right+0.5,"Buttons cropped after resize");}Assert(Visual<ScrollViewer>(root)!=null,"Editor has no scroll fallback");});}finally{w.Close();}
        });
        Case("calendar can correct invalid text by selecting the same date",delegate{
            MainWindow w=Open();try{Edit(w,null,delegate(Window d){Fill(d,"once",new DateTime(2030,2,5,19,0,0));DatePicker picker=Controls<DatePicker>(d)[0];picker.Text="31.02.2030";Drain();picker.IsDropDownOpen=true;Drain();Calendar calendar=Visual<Calendar>(((Popup)picker.Template.FindName("PART_Popup",picker)).Child);CalendarItem item=Visual<CalendarItem>(calendar);Grid days=(Grid)item.Template.FindName("PART_MonthView",item);bool found=false;foreach(UIElement child in days.Children){CalendarDayButton b=child as CalendarDayButton;if(b!=null&&b.DataContext is DateTime&&(DateTime)b.DataContext==new DateTime(2030,2,5)){Click(b);found=true;break;}}Assert(found,"Selected calendar day missing");picker.IsDropDownOpen=false;Drain();Click(Button(d,"Сохранить"));Assert(!d.IsVisible&&Live(w).Reminders.Count==1,"Selecting a valid calendar day did not correct invalid text");});}finally{w.Close();}
        });
        Case("yearly leap anchor survives non-leap century",delegate{ReminderItem item=Item("yearly",new DateTime(2096,2,29,12,0,0));item=Next(item,new DateTime(2099,3,1));Due(item,new DateTime(2100,2,28,12,0,0));item=Next(item,new DateTime(2103,3,1));Due(item,new DateTime(2104,2,29,12,0,0));});
        Case("invalid anchors are rejected without replacing good export",delegate{
            ReminderDocument doc=new ReminderDocument();doc.Reminders.Add(Item("yearly",new DateTime(2032,2,29,12,0,0)));string path=Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"),"valid-anchor.json");BackupStore.ExportReminders(path,doc);string before=File.ReadAllText(path);SetAnchor(doc.Reminders[0],"RepeatDay",30);SetAnchor(doc.Reminders[0],"RepeatMonth",2);bool rejected=false;try{BackupStore.ExportReminders(path,doc);}catch(InvalidDataException){rejected=true;}Assert(rejected&&File.ReadAllText(path)==before,"Invalid recurrence destroyed good export");
        });
        foreach(bool hours in new bool[]{true,false}){bool h=hours;Case("mouse spinner bounds and independent "+(h?"hours":"minutes"),delegate{
            MainWindow w=Open();try{Edit(w,null,delegate(Window d){string name=h?"ReminderHours":"ReminderMinutes";TextBox input=Named<TextBox>(d,name);TextBox other=Named<TextBox>(d,h?"ReminderMinutes":"ReminderHours");string preserved=other.Text;DateTime? date=Controls<DatePicker>(d)[0].SelectedDate;Button up=Named<Button>(d,name+"Up"),down=Named<Button>(d,name+"Down");input.Text="00";Assert(!down.IsEnabled,"Down enabled below zero");Click(down);Assert(input.Text=="00","Lower boundary changed");Click(up);Assert(input.Text=="01","Spinner step is not one");input.Text=h?"23":"59";Assert(!up.IsEnabled,"Up enabled beyond range");Click(up);Assert(input.Text==(h?"23":"59"),"Upper boundary changed");Click(down);Assert(input.Text==(h?"22":"58"),"Down step incorrect");Assert(other.Text==preserved&&Controls<DatePicker>(d)[0].SelectedDate==date,"Spinner changed adjacent field/date");});}finally{w.Close();}
        });}
        foreach(bool hours in new bool[]{true,false}){bool h=hours;Case("keyboard spinner bounds "+(h?"hours":"minutes"),delegate{
            MainWindow w=Open();try{Edit(w,null,delegate(Window d){TextBox input=Named<TextBox>(d,h?"ReminderHours":"ReminderMinutes");input.Text="00";ArrowKey(input,Key.Down);Assert(input.Text=="00","Keyboard crossed zero");ArrowKey(input,Key.Up);Assert(input.Text=="01","Keyboard increment wrong");input.Text=h?"23":"59";ArrowKey(input,Key.Up);Assert(input.Text==(h?"23":"59"),"Keyboard crossed maximum");ArrowKey(input,Key.Down);Assert(input.Text==(h?"22":"58"),"Keyboard decrement wrong");});}finally{w.Close();}
        });}
        foreach(bool hours in new bool[]{true,false}){bool h=hours;Case("typed numeric replacement and range filter "+(h?"hours":"minutes"),delegate{
            MainWindow w=Open();try{Edit(w,null,delegate(Window d){TextBox input=Named<TextBox>(d,h?"ReminderHours":"ReminderMinutes");Type(input,d,h?"23":"59");Assert(input.Text==(h?"23":"59"),"Valid keyboard input rejected");Type(input,d,h?"24":"60");Assert(input.Text==(h?"23":"59"),"Out-of-range input replaced valid data");Type(input,d,"ab");Assert(input.Text==(h?"23":"59"),"Nonnumeric input replaced valid data");Type(input,d,"5");Title(d).Focus();Drain();Assert(input.Text=="05","Focus loss did not zero-pad single digit");});}finally{w.Close();}
        });}
        Case("invalid paste preserves both time fields",delegate{
            MainWindow w=Open();try{Edit(w,null,delegate(Window d){TextBox hours=Named<TextBox>(d,"ReminderHours"),minutes=Named<TextBox>(d,"ReminderMinutes");string before=Time(d).Text;Assert(!Paste(hours,"24")&&!Paste(minutes,"60")&&!Paste(hours,"-1")&&!Paste(minutes,"xx")&&!Paste(hours,"123"),"Invalid paste accepted");Assert(Time(d).Text==before,"Invalid paste lost old values");});}finally{w.Close();}
        });
        Case("valid paste and keyboard deletion",delegate{
            MainWindow w=Open();try{Edit(w,null,delegate(Window d){TextBox input=Named<TextBox>(d,"ReminderMinutes");Assert(Paste(input,"07")&&input.Text=="07","Valid paste rejected");d.Activate();input.Focus();Keyboard.Focus(input);input.SelectAll();EditingCommands.Backspace.Execute(null,input);Assert(input.Text=="","Backspace did not clear selected digits");Type(input,d,"8");Title(d).Focus();Drain();Assert(input.Text=="08","Retyping after deletion failed");});}finally{w.Close();}
        });
        Case("one digit hours and minutes save in HH:mm",delegate{
            MainWindow w=Open();try{Edit(w,null,delegate(Window d){Fill(d,"once",new DateTime(2030,2,5,19,0,0));Time(d).Text="9:5";Click(Button(d,"Сохранить"));Assert(!d.IsVisible&&Time(d).Text=="09:05","Single digits were not normalized");});Due(Live(w).Reminders[0],new DateTime(2030,2,5,9,5,0));}finally{w.Close();}
        });
        Case("empty field disables arrows and preserves pending input",delegate{
            MainWindow w=Open();try{Edit(w,null,delegate(Window d){Fill(d,"once",new DateTime(2030,2,5,19,17,0));TextBox hours=Named<TextBox>(d,"ReminderHours");hours.Text="";Assert(!Named<Button>(d,"ReminderHoursUp").IsEnabled&&!Named<Button>(d,"ReminderHoursDown").IsEnabled,"Empty value enables stepping");Click(Named<Button>(d,"ReminderHoursUp"));Click(Button(d,"Сохранить"));Assert(d.IsVisible&&Live(w).Reminders.Count==0&&hours.Text==""&&Named<TextBox>(d,"ReminderMinutes").Text=="17"&&InvalidVisible(d),"Empty input was overwritten or saved");});}finally{w.Close();}
        });
        Case("mouse time changes save and reopen correctly",delegate{
            MainWindow w=Open();try{Edit(w,null,delegate(Window d){Fill(d,"monthly",new DateTime(2030,3,31,19,23,0));Click(Named<Button>(d,"ReminderHoursUp"));Click(Named<Button>(d,"ReminderMinutesDown"));Click(Button(d,"Сохранить"));});ReminderItem item=Live(w).Reminders[0];Due(item,new DateTime(2030,3,31,20,22,0));Edit(w,item,delegate(Window d){Assert(Time(d).Text=="20:22","Saved time not restored");ArrowKey(Named<TextBox>(d,"ReminderMinutes"),Key.Up);Click(Button(d,"Сохранить"));});Due(item,new DateTime(2030,3,31,20,23,0));Assert(Snapshot(Load())==Snapshot(Live(w)),"Spinner save disk/live mismatch");}finally{w.Close();}
        });
        Case("cancel discards spinner changes",delegate{
            MainWindow w=Open();try{ReminderItem item=Item("weekly",new DateTime(2030,3,31,19,23,0));Live(w).Reminders.Add(item);ReminderStore.Save(Live(w));string before=Snapshot(Live(w));Edit(w,item,delegate(Window d){Click(Named<Button>(d,"ReminderHoursDown"));ArrowKey(Named<TextBox>(d,"ReminderMinutes"),Key.Up);Click(Button(d,"Отмена"));});Assert(Snapshot(Live(w))==before&&Snapshot(Load())==before,"Cancelled spinner edits persisted");}finally{w.Close();}
        });
        Case("initial plus thirty minutes crosses midnight",delegate{
            DateTime previous=Now;Now=new DateTime(2030,1,31,23,45,0);MainWindow w=Open();try{Edit(w,null,delegate(Window d){Assert(Time(d).Text=="00:15"&&Controls<DatePicker>(d)[0].SelectedDate==new DateTime(2030,2,1),"Default date/time rollover incorrect");});}finally{w.Close();Now=previous;}
        });
        Case("Tab moves from hours to minutes without arrow buttons",delegate{
            MainWindow w=Open();try{Edit(w,null,delegate(Window d){d.Activate();TextBox hours=Named<TextBox>(d,"ReminderHours"),minutes=Named<TextBox>(d,"ReminderMinutes");hours.Focus();Keyboard.Focus(hours);Assert(hours.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)),"Tab traversal failed");Drain();Assert(minutes.IsKeyboardFocused,"Tab focused a spinner arrow instead of minutes");});}finally{w.Close();}
        });
        Case("mouse arrow focuses its own numeric field for keyboard continuation",delegate{
            MainWindow w=Open();try{Edit(w,null,delegate(Window d){d.Activate();TextBox hours=Named<TextBox>(d,"ReminderHours"),minutes=Named<TextBox>(d,"ReminderMinutes");minutes.Focus();Keyboard.Focus(minutes);Click(Named<Button>(d,"ReminderHoursUp"));Assert(hours.IsKeyboardFocused,"Clicked spinner did not focus hours");ArrowKey(hours,Key.Up);Assert(hours.Text=="20"&&minutes.Text=="30","Keyboard continuation affected wrong field");});}finally{w.Close();}
        });
        Console.WriteLine("Reminder calendar/UI scenarios passed="+passed+"; failed="+failed);
        app.Shutdown();if(failed!=0)throw new Exception("Reminder calendar/UI failures: "+failed);
    }
}
'@
    $options = New-Object CodeDom.Compiler.CompilerParameters
    $options.GenerateInMemory = $true
    $options.TreatWarningsAsErrors = $true
    $framework = [Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()
    foreach ($ref in @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Windows.Forms.dll','System.Drawing.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','System.Xml.dll','System.Xaml.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll')) { [void]$options.ReferencedAssemblies.Add((Join-Path $framework $ref)) }
    $result = $provider.CompileAssemblyFromSource($options,[string[]]$sources)
    if ($result.Errors.Count) { throw ($result.Errors | Out-String) }
    $result.CompiledAssembly.GetType('ReminderUiTests').GetMethod('Run').Invoke($null,@())
    Write-Output "PASS isolated reminder calendar/UI tests: $temp"
} finally { $provider.Dispose(); $env:APPSHELF_TEST_DATA = $old }
