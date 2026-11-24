using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Documents;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace DesktopAI.Gui;

public partial class MainWindow : Window
{
    private AsuisuiAssistantCore core = new();
    private CancellationTokenSource? operation;
    private bool ready;
    private bool selectingProfile;
    private byte[]? pendingImage;
    private readonly Paragraph transcript = new() { Margin = new Thickness(0) };

    public MainWindow()
    {
        InitializeComponent();
        Conversation.Document.Blocks.Clear();
        Conversation.Document.Blocks.Add(transcript);
        RefreshScreens();
        Append("歡迎使用 Asuisui AI。請選擇對話模式，然後輸入訊息。\n");
        SetReady(true, "準備就緒");
    }

    private void Append(string text)
    {
        int offset = 0;
        foreach (Match match in Regex.Matches(text, @"\[[^\]\r\n]+\]\((?<markdown>https?://[^\s)]+)\)|(?<plain>https?://[^\s<>]+)"))
        {
            transcript.Inlines.Add(new Run(text[offset..match.Index]));
            bool markdown = match.Groups["markdown"].Success;
            string rawUrl = markdown ? match.Groups["markdown"].Value : match.Groups["plain"].Value;
            string url = rawUrl.TrimEnd('.', ',', '。', '，', ')', '）');
            var link = new Hyperlink(new Run("連結")) { NavigateUri = new Uri(url), ToolTip = url, Foreground = new SolidColorBrush(Color.FromRgb(139, 181, 255)) };
            link.RequestNavigate += (_, args) => { try { Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(ex.Message, "無法開啟連結"); } args.Handled = true; };
            transcript.Inlines.Add(link);
            if (!markdown) transcript.Inlines.Add(new Run(rawUrl[url.Length..]));
            offset = match.Index + match.Length;
        }
        transcript.Inlines.Add(new Run(text[offset..]));
        Conversation.ScrollToEnd();
    }
    private void SetReady(bool value, string status)
    {
        ready = value; StatusText.Text = status; SendButton.IsEnabled = value;
        MemoriesButton.IsEnabled = ProfileButton.IsEnabled = SettingsButton.IsEnabled = value;
        ScreenBox.IsEnabled = ScreenshotButton.IsEnabled = ImageButton.IsEnabled = value;
    }

    private async Task SendAsync(string text, bool raw = false)
    {
        if (!ready || string.IsNullOrWhiteSpace(text)) return;
        text = text.Trim();
        if (text.Contains('\n') || text.Contains('\r')) text = string.Join(" ", text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        if (selectingProfile && int.TryParse(text, out int profileIndex))
        {
            var profiles = core.GetChromeProfiles();
            if (profileIndex >= 1 && profileIndex <= profiles.Count)
            {
                core.SetChromeProfile(profiles[profileIndex - 1].Path);
                Append($"已選擇 Chrome 設定檔：{profiles[profileIndex - 1].Name}\n");
                selectingProfile = false;
                return;
            }
        }
        selectingProfile = false;
        bool sendImage = pendingImage is not null && !raw && !text.StartsWith('/');
        AssistantMode mode = sendImage ? AssistantMode.GoogleAi : ModeBox.SelectedIndex switch { 1 => AssistantMode.GoogleAi, 2 => AssistantMode.ChatGpt, 3 => AssistantMode.Web, _ => AssistantMode.Local };
        Append($"\n你：{text}\n");
        SetReady(false, "處理中…");
        operation = new CancellationTokenSource();
        var progress = new Progress<string>(message => StatusText.Text = message);
        try
        {
            var response = await core.AskAsync(text, mode, sendImage ? pendingImage : null, progress, operation.Token);
            Append($"\n{(response.IsExternal ? mode == AssistantMode.ChatGpt ? "ChatGPT" : "Google AI" : "AI")}：{response.Answer}\n");
            if (response.Sources.Count > 0)
            {
                Append("來源：\n");
                foreach (var source in response.Sources) Append($"• {source.Title}\n  {source.Url}\n");
            }
            if (sendImage) ClearImage();
        }
        catch (OperationCanceledException) { Append("已停止目前工作。\n"); }
        catch (Exception ex) { Append($"處理失敗：{ex.Message}\n"); }
        finally { operation?.Dispose(); operation = null; SetReady(true, "準備就緒"); }
    }

    private async void Send_Click(object sender, RoutedEventArgs e) { string text = InputBox.Text; InputBox.Clear(); await SendAsync(text); }
    private async void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        if (!ready) return;
        string text = InputBox.Text; InputBox.Clear(); await SendAsync(text);
    }
    private void Memories_Click(object sender, RoutedEventArgs e) => Append("\n永久記憶：\n" + (string.IsNullOrWhiteSpace(core.GetMemories()) ? "目前沒有已儲存的永久記憶。" : core.GetMemories()) + "\n");
    private void Profile_Click(object sender, RoutedEventArgs e)
    {
        var profiles = core.GetChromeProfiles();
        if (profiles.Count == 0) { Append("找不到 Chrome 設定檔。\n"); return; }
        selectingProfile = true;
        Append("\n選擇用於 Google AI 與 ChatGPT 的 Chrome 設定檔：\n" + string.Join("\n", profiles.Select((profile, index) => $"{index + 1}. {profile.Name}")) + "\n請在輸入框輸入編號。\n");
    }
    private void Settings_Click(object sender, RoutedEventArgs e) => Append("顯示設定正在遷移到視窗版；目前可直接使用模式選擇與圖片預覽。\n");
    private void ClearImage() { pendingImage = null; ImagePreview.Source = null; ImagePreviewPanel.Visibility = Visibility.Collapsed; }
    private void RemoveImage_Click(object sender, RoutedEventArgs e) => ClearImage();

    private void SetImage(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
        using var buffer = new MemoryStream(); encoder.Save(buffer);
        if (buffer.Length > 12_000_000) throw new InvalidOperationException("圖片超過 12 MB，請選擇較小的圖片。");
        pendingImage = buffer.ToArray(); ImagePreview.Source = source; ImagePreviewPanel.Visibility = Visibility.Visible;
        Append("已加入圖片。確認預覽並輸入問題後，按送出傳給 Google AI。\n");
    }

    private void ChooseImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "圖片檔案|*.png;*.jpg;*.jpeg;*.bmp;*.webp|所有檔案|*.*", Title = "選擇要傳給 Google AI 的圖片" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(dialog.FileName); image.EndInit(); image.Freeze(); SetImage(image);
        }
        catch (Exception ex) { MessageBox.Show(this, $"無法讀取圖片：{ex.Message}", "選擇圖片"); }
    }

    private async void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ScreenBox.SelectedItem is not ScreenInfo selected) throw new InvalidOperationException("請先選擇要截取的螢幕。");
            Hide(); await Task.Delay(500);
            var current = GetScreens().FirstOrDefault(screen => screen.Device == selected.Device)
                ?? throw new InvalidOperationException("所選螢幕已不存在，請重新選擇。");
            var image = CaptureScreen(current);
            Show(); Activate(); SetImage(image);
        }
        catch (Exception ex) { Show(); Activate(); MessageBox.Show(this, $"無法截取畫面：{ex.Message}", "截取螢幕"); }
    }

    private sealed record ScreenInfo(string Device, int Left, int Top, int Width, int Height, bool Primary)
    {
        public string Label => $"{Device.Replace(@"\\.\", "")} {(Primary ? "（主螢幕）" : "")} {Width}×{Height}";
    }

    private void RefreshScreens()
    {
        string? selected = (ScreenBox.SelectedItem as ScreenInfo)?.Device;
        var screens = GetScreens();
        ScreenBox.ItemsSource = screens;
        ScreenBox.SelectedItem = screens.FirstOrDefault(screen => screen.Device == selected) ?? screens.FirstOrDefault(screen => screen.Primary) ?? screens.FirstOrDefault();
    }
    private void ScreenBox_DropDownOpened(object sender, EventArgs e) => RefreshScreens();

    private static List<ScreenInfo> GetScreens()
    {
        var screens = new List<ScreenInfo>();
        MonitorEnum callback = (IntPtr monitor, IntPtr dc, ref NativeRect rect, IntPtr data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
            if (GetMonitorInfo(monitor, ref info))
                screens.Add(new ScreenInfo(info.Device, info.Monitor.Left, info.Monitor.Top, info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top, (info.Flags & 1) != 0));
            return true;
        };
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero)) throw new InvalidOperationException("無法取得螢幕清單。");
        return screens.OrderBy(screen => screen.Device, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static BitmapSource CaptureScreen(ScreenInfo screen)
    {
        int width = screen.Width, height = screen.Height;
        if (width <= 0 || height <= 0) throw new InvalidOperationException("無法取得螢幕大小。");
        IntPtr source = GetDC(IntPtr.Zero), memory = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            if (source == IntPtr.Zero || (memory = CreateCompatibleDC(source)) == IntPtr.Zero || (bitmap = CreateCompatibleBitmap(source, width, height)) == IntPtr.Zero) throw new InvalidOperationException("無法建立畫面影像。");
            previous = SelectObject(memory, bitmap);
            if (!BitBlt(memory, 0, 0, width, height, source, screen.Left, screen.Top, 0x40CC0020)) throw new InvalidOperationException("螢幕截取失敗。");
            var image = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze(); return image;
        }
        finally { if (previous != IntPtr.Zero && memory != IntPtr.Zero) SelectObject(memory, previous); if (bitmap != IntPtr.Zero) DeleteObject(bitmap); if (memory != IntPtr.Zero) DeleteDC(memory); if (source != IntPtr.Zero) ReleaseDC(IntPtr.Zero, source); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo
    {
        public int Size; public NativeRect Monitor; public NativeRect Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    private delegate bool MonitorEnum(IntPtr monitor, IntPtr dc, ref NativeRect rect, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnum callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint rasterOperation);
    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (operation is not null) { operation.Cancel(); return; }
        core.ClearConversation(); ClearImage(); Append("\n已清除目前對話脈絡。\n");
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        operation?.Cancel(); core.Dispose();
    }
}
